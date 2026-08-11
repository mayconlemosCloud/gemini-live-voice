using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Core.Configuration;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Session;
using GeminiTranslate.Core.Signal;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Etiqueta flutuante sempre no topo com o SALDO de cada direção: quanto se falou e quanto dessa
/// fala já saiu traduzida.
/// </summary>
/// <remarks>
/// Existe separada da janela principal porque esta fica minimizada durante a chamada — e é
/// durante a chamada que a pergunta "já saiu tudo o que eu falei?" importa. Sem barra de
/// tarefas, arrastável, e some do compartilhamento de tela junto com o resto do app.
/// </remarks>
public partial class BalanceWindow : Window
{
    /// <summary>Largura da trilha da barra, igual à do XAML.</summary>
    private const double BarWidth = 170;

    /// <summary>
    /// Quanto de linha do tempo a barra inteira representa.
    /// </summary>
    /// <remarks>
    /// FIXO de propósito: se a barra medisse a fala atual, um vão de 2 s numa fala de 60 s
    /// viraria um fiapo de 3 px, e o mesmo atraso pareceria menor só porque a pessoa falou mais.
    /// Com janela fixa, distância na tela é sempre a mesma coisa em segundos.
    /// </remarks>
    private const double WindowMs = 8000;

    private const double InSyncMs = 250;
    private const double OkMs = 1500;
    private const double WarnMs = 3000;

    /// <summary>4 Hz: rápido o bastante para a barra parecer contínua, sem custar nada.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static readonly Brush Ok = Frozen(0x6E, 0xC2, 0x7A);
    private static readonly Brush Warn = Frozen(0xE0, 0xB0, 0x4A);
    private static readonly Brush Behind = Frozen(0xE0, 0x6C, 0x6C);
    private static readonly Brush Dim = Frozen(0x6B, 0x6E, 0x73);
    private static readonly Brush Normal = Frozen(0xEA, 0xEA, 0xEA);

    private readonly Settings _settings;
    private readonly ISettingsStore _store;
    private readonly DispatcherTimer _timer;
    private TranslationDirection? _incoming;
    private TranslationDirection? _outgoing;

    /// <param name="settings">Preferências, para lembrar onde a etiqueta foi largada.</param>
    /// <param name="store">Onde gravar a posição escolhida.</param>
    public BalanceWindow(Settings settings, ISettingsStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        Stealth.Register(this);

        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => Refresh();

        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Liga a etiqueta às direções e começa a atualizar.</summary>
    public void Bind(TranslationDirection outgoing, TranslationDirection incoming)
    {
        _outgoing = outgoing;
        _incoming = incoming;
        Refresh();
        _timer.Start();
    }

    /// <summary>
    /// Restaura a posição salva, desde que ela ainda caiba na área de trabalho atual: trocar de
    /// monitor não pode deixar a etiqueta fora da tela, sem como trazê-la de volta.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;

        bool fits = _settings.LagLeft is double left && _settings.LagTop is double top
                    && left >= work.Left - 4 && top >= work.Top - 4
                    && left + ActualWidth <= work.Right + 4 && top + ActualHeight <= work.Bottom + 4;

        if (fits)
        {
            Left = _settings.LagLeft!.Value;
            Top = _settings.LagTop!.Value;
            return;
        }

        Left = work.Right - ActualWidth - 12;
        Top = work.Bottom - ActualHeight - 12;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try { DragMove(); } catch { }

        _settings.LagLeft = Left;
        _settings.LagTop = Top;
        _store.Save(_settings);
    }

    private void Refresh()
    {
        if (_outgoing is null || _incoming is null) return;

        Apply(_outgoing.Balance, OutFill, OutHead, OutText, OutRow);
        Apply(_incoming.Balance, InFill, InHead, InText, InRow);
    }

    /// <summary>
    /// Desenha as duas cabeças na mesma linha do tempo.
    /// </summary>
    /// <remarks>
    /// A borda direita da trilha é sempre "agora", onde a fala está. A cabeça branca é onde a
    /// tradução está. O trecho escuro entre elas é o que já foi dito e ainda não saiu: é a
    /// distância que o usuário quer enxergar.
    /// </remarks>
    private static void Apply(BalanceSnapshot balance, Border fill, Border head, TextBlock text, UIElement row)
    {
        if (!balance.Active)
        {
            ShowMessage(fill, head, text, "sem fala pendente");
            row.Opacity = 0.5;
            return;
        }

        row.Opacity = 1.0;

        if (double.IsNaN(balance.GapMs))
        {
            ShowMessage(fill, head, text, "medindo a distância…");
            return;
        }

        head.Visibility = Visibility.Visible;

        double filled = BarWidth * Math.Clamp(1 - balance.GapMs / WindowMs, 0, 1);
        fill.Width = filled;
        head.Margin = new Thickness(Math.Min(filled, BarWidth - 2), 0, 0, 0);

        fill.Background = balance.GapMs < OkMs ? Ok : balance.GapMs < WarnMs ? Warn : Behind;
        text.Foreground = Normal;
        text.Text = balance.GapMs < InSyncMs
            ? "tradução em dia"
            : $"tradução ~{balance.GapMs / 1000:0.0} s atrás";
    }

    /// <summary>
    /// Recolhe a barra e escreve um estado em vez de um número.
    /// </summary>
    /// <remarks>
    /// O estimador precisa de cerca de 20 s de conversa antes do primeiro alinhamento confiável.
    /// Até lá, dizer isso é mais honesto que desenhar uma barra que não mede nada.
    /// </remarks>
    private static void ShowMessage(Border fill, Border head, TextBlock text, string message)
    {
        fill.Width = 0;
        head.Margin = new Thickness(0);
        head.Visibility = Visibility.Collapsed;
        text.Text = message;
        text.Foreground = Dim;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
