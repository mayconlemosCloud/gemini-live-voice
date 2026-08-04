using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace GeminiTranslateV2;

public partial class SuggestionWindow : Window
{
    private readonly string _question;

    public SuggestionWindow(string question)
    {
        InitializeComponent();
        Stealth.Register(this);
        _question = question;
        QuestionText.Text = question;
        AnswerBox.Text = "Gerando sugestão…";
        CopyButton.IsEnabled = false;
        // Esc fecha a janela.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Log.Write("Sugestão", "Esc pressionado — fechando."); Close(); } };

        Loaded += (_, _) => Log.Write("Sugestão", $"janela Loaded (visível) para: '{_question}'");
        Activated += (_, _) => Log.Write("Sugestão", "janela Activated (recebeu foco).");
        Deactivated += (_, _) => Log.Write("Sugestão", "janela Deactivated (perdeu foco).");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Log de quem/por que fechou — ajuda a diagnosticar fechamentos inesperados.
        Log.Write("Sugestão", "OnClosing disparado. Origem:\n" + Environment.StackTrace);
        base.OnClosing(e);
    }

    public void SetAnswer(string answer)
    {
        AnswerBox.Text = answer;
        CopyButton.IsEnabled = true;
        Log.Write("Sugestão", $"SetAnswer ({answer?.Length ?? 0} chars).");
    }

    public void SetError(string message)
    {
        AnswerBox.Text = "Não foi possível gerar a sugestão:\n" + message;
        CopyButton.IsEnabled = false;
        Log.Write("Sugestão", "SetError: " + message);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AnswerBox.Text); Log.Write("Sugestão", "resposta copiada."); }
        catch (Exception ex) { Log.Write("Sugestão", "falha ao copiar: " + ex.Message); }
    }
}
