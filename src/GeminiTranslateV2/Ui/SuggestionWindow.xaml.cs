using System.Windows;
using System.Windows.Input;
using GeminiTranslate.App.Platform;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Janela com a sugestão de resposta à pergunta atual.
/// </summary>
/// <remarks>
/// É REUTILIZADA a cada nova pergunta, em vez de abrir uma janela por clique. Antes cada pergunta
/// abria a sua, todas na mesma posição e fora da barra de tarefas: elas se empilhavam exatamente
/// uma sobre a outra, e fechar a de cima só revelava outra idêntica embaixo — o que parecia que o
/// X não estava funcionando.
///
/// Fica sempre no topo, como o overlay e a etiqueta de saldo. Sem isso ela abria ATRÁS do app de
/// reunião em tela cheia, onde não havia como achá-la nem fechá-la.
/// </remarks>
public partial class SuggestionWindow : Window
{
    /// <summary>Monta a janela vazia. O conteúdo vem de <see cref="BeginQuestion"/>.</summary>
    public SuggestionWindow()
    {
        InitializeComponent();
        Stealth.Register(this);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Prepara a janela para uma pergunta nova e a traz para frente.</summary>
    public void BeginQuestion(string question)
    {
        QuestionText.Text = question;
        AnswerBox.Text = "Gerando sugestão…";
        CopyButton.IsEnabled = false;

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Mostra a resposta gerada e libera a cópia.</summary>
    public void SetAnswer(string answer)
    {
        AnswerBox.Text = answer;
        CopyButton.IsEnabled = true;
        Log.Write("Sugestão", $"resposta preenchida ({answer.Length} chars).");
    }

    /// <summary>Mostra por que a sugestão não pôde ser gerada.</summary>
    public void SetError(string message)
    {
        AnswerBox.Text = "Não foi possível gerar a sugestão:\n" + message;
        CopyButton.IsEnabled = false;
        Log.Write("Sugestão", "falha ao gerar: " + message);
    }

    /// <summary>Esc fecha a janela.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        Log.Write("Sugestão", "Esc pressionado — fechando.");
        Close();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AnswerBox.Text);
            Log.Write("Sugestão", "resposta copiada.");
        }
        catch (Exception ex)
        {
            Log.Write("Sugestão", "falha ao copiar: " + ex.Message);
        }
    }
}
