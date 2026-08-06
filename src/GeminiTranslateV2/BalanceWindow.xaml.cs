using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GeminiTranslateV2;

/// <summary>
/// Etiqueta flutuante sempre-no-topo com o SALDO de cada direção: quanto se falou e quanto dessa
/// fala já saiu traduzida. Existe separada da janela principal porque esta fica minimizada durante
/// a chamada — e é durante a chamada que a pergunta "já saiu tudo o que eu falei?" importa.
/// Sem barra de tarefas, arrastável, e some do compartilhamento de tela junto com o resto do app.
/// </summary>
public partial class BalanceWindow : Window
{
    /// <summary>Largura da trilha da barra, igual à do XAML.</summary>
    private const double BarWidth = 170;

    /// <summary>
    /// Quanto de linha do tempo a barra inteira representa. FIXO de propósito: se a barra medisse
    /// a fala atual, um vão de 2 s numa fala de 60 s viraria um fiapo de 3 px — o mesmo atraso
    /// pareceria menor só porque a pessoa falou mais. Com janela fixa, distância na tela é sempre
    /// a mesma coisa em segundos.
    /// </summary>
    private const double WindowMs = 8000;

    /// <summary>4 Hz: rápido o bastante para a barra parecer contínua, sem custar nada.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static readonly Brush Ok = Frozen(0x6E, 0xC2, 0x7A);
    private static readonly Brush Warn = Frozen(0xE0, 0xB0, 0x4A);
    private static readonly Brush Behind = Frozen(0xE0, 0x6C, 0x6C);
    private static readonly Brush Dim = Frozen(0x6B, 0x6E, 0x73);
    private static readonly Brush Normal = Frozen(0xEA, 0xEA, 0xEA);

    private readonly Settings _settings;
    private readonly DispatcherTimer _timer;
    private Direction? _incoming;
    private Direction? _outgoing;

    public BalanceWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        Stealth.Register(this);
        Loaded += OnLoaded;
        Closed += (_, _) => _timer?.Stop();

        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => Refresh();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void Bind(Direction outgoing, Direction incoming)
    {
        _outgoing = outgoing;
        _incoming = incoming;
        Refresh();
        _timer.Start();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var wa = SystemParameters.WorkArea;
        // Posição salva, desde que ainda caiba na área de trabalho atual — trocar de monitor não
        // pode deixar a etiqueta fora da tela, sem como trazê-la de volta.
        if (_settings.LagLeft is double l && _settings.LagTop is double t
            && l >= wa.Left - 4 && t >= wa.Top - 4
            && l + ActualWidth <= wa.Right + 4 && t + ActualHeight <= wa.Bottom + 4)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* clique virou duplo-clique no meio do arrasto */ }
        _settings.LagLeft = Left;
        _settings.LagTop = Top;
        _settings.Save();
    }

    private void Refresh()
    {
        if (_outgoing is null || _incoming is null) return;
        Apply(_outgoing.Balance, OutFill, OutHead, OutText, OutRow);
        Apply(_incoming.Balance, InFill, InHead, InText, InRow);
    }

    /// <summary>
    /// Desenha as duas cabeças na mesma linha do tempo. A borda DIREITA da trilha é sempre "agora"
    /// — onde a fala está. A cabeça branca é onde a tradução está. O trecho escuro entre elas é o
    /// que já foi dito e ainda não saiu: é a distância que o usuário quer enxergar.
    /// </summary>
    private static void Apply(BalanceSnapshot b, Border fill, Border head, TextBlock text, UIElement row)
    {
        if (!b.Active)
        {
            fill.Width = 0;
            head.Margin = new Thickness(0);
            head.Visibility = Visibility.Collapsed;
            text.Text = "sem fala pendente";
            text.Foreground = Dim;
            row.Opacity = 0.5;
            return;
        }

        row.Opacity = 1.0;

        // O estimador precisa de ~20 s de conversa antes do primeiro alinhamento confiável. Até lá
        // dizer isso é mais honesto que desenhar uma barra que não mede nada.
        if (double.IsNaN(b.GapMs))
        {
            fill.Width = 0;
            head.Visibility = Visibility.Collapsed;
            text.Text = "medindo a distância…";
            text.Foreground = Dim;
            return;
        }

        head.Visibility = Visibility.Visible;

        double filled = BarWidth * Math.Clamp(1 - b.GapMs / WindowMs, 0, 1);
        fill.Width = filled;
        head.Margin = new Thickness(Math.Min(filled, BarWidth - 2), 0, 0, 0);

        // A cor comunica a única decisão que se toma olhando para isto: seguir falando ou dar uma
        // pausa para a tradução alcançar.
        fill.Background = b.GapMs < 1500 ? Ok : b.GapMs < 3000 ? Warn : Behind;
        text.Foreground = Normal;

        // "~" porque a distância vem de uma razão aprendida por sessão (ver SpeechBalance), não de
        // uma subtração exata: a tradução não tem a mesma duração do original.
        text.Text = b.GapMs < 250
            ? "tradução em dia"
            : $"tradução ~{b.GapMs / 1000:0.0} s atrás";
    }
}
