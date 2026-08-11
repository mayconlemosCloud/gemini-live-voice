using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GeminiTranslate.Core.Diagnostics;

namespace GeminiTranslate.App.Ui;

/// <summary>
/// Preenche o painel da outra pessoa com a tradução em streaming e, quando uma frase termina em
/// interrogação, a finaliza como um trecho sublinhado e clicável.
/// </summary>
/// <remarks>
/// O clique NÃO usa Hyperlink.Click, que costuma não disparar num RichTextBox somente-leitura.
/// Em vez disso, um manipulador de PreviewMouseLeftButtonUp faz hit-test na posição e verifica se
/// o trecho sob o cursor é uma pergunta. Com o assistente desligado, o texto é exibido
/// normalmente. Todos os métodos rodam na thread da interface.
/// </remarks>
public sealed class QuestionTranscript
{
    private const int MaxContextChars = 2000;
    private static readonly char[] SentenceEnders = ['.', '!', '?', '…'];
    private static readonly Brush QuestionBrush = FrozenQuestionBrush();

    private sealed record QuestionData(string Question, string Context);

    private readonly RichTextBox _box;
    private readonly Paragraph _paragraph;
    private readonly Action<string, string>? _onQuestionClicked;
    private readonly Action<string>? _onQuestionDetected;

    /// <summary>Texto completo já recebido, usado como contexto de uma pergunta clicada.</summary>
    private readonly StringBuilder _plainText = new();

    /// <summary>Frase em construção, exibida ao vivo.</summary>
    private Run? _pending;

    /// <param name="box">Painel onde a tradução é escrita.</param>
    /// <param name="onQuestionClicked">Clique numa pergunta. Null significa assistente desligado.</param>
    /// <param name="onQuestionDetected">
    /// Toda pergunta detectada, clicada ou não — usado para guardar "a última pergunta" e poder
    /// respondê-la pelo overlay sem precisar clicar.
    /// </param>
    public QuestionTranscript(
        RichTextBox box,
        Action<string, string>? onQuestionClicked,
        Action<string>? onQuestionDetected = null)
    {
        _box = box;
        _onQuestionClicked = onQuestionClicked;
        _onQuestionDetected = onQuestionDetected;

        _paragraph = new Paragraph { Margin = new Thickness(0) };
        _box.Document = new FlowDocument(_paragraph) { PagePadding = new Thickness(0) };

        if (_onQuestionClicked is not null) _box.PreviewMouseLeftButtonUp += OnClick;
    }

    /// <summary>Pincel congelado das perguntas, criado uma vez para todas as instâncias.</summary>
    private static Brush FrozenQuestionBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4D, 0xAB, 0xF7));
        brush.Freeze();
        return brush;
    }

    /// <summary>Acrescenta um fragmento de tradução, fechando as frases que se completarem.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        RememberForContext(text);

        _pending ??= AddRun("");
        _pending.Text += text;
        SplitCompletedSentences();

        _box.ScrollToEnd();
    }

    /// <summary>Esvazia o painel e o contexto acumulado.</summary>
    public void Clear()
    {
        _paragraph.Inlines.Clear();
        _plainText.Clear();
        _pending = null;
    }

    private void RememberForContext(string text)
    {
        _plainText.Append(text);
        if (_plainText.Length > MaxContextChars * 2)
            _plainText.Remove(0, _plainText.Length - MaxContextChars * 2);
    }

    /// <summary>Fatia todas as frases completas do texto pendente, deixando o resto em construção.</summary>
    private void SplitCompletedSentences()
    {
        int end;
        while (_pending is not null && (end = _pending.Text.IndexOfAny(SentenceEnders)) >= 0)
        {
            int cut = end + 1;
            while (cut < _pending.Text.Length && _pending.Text[cut] == ' ') cut++;

            string sentence = _pending.Text[..cut];
            string remainder = _pending.Text[cut..];

            _pending.Text = sentence;
            FinalizeSentence(_pending, sentence);
            _pending = AddRun(remainder);
        }
    }

    private Run AddRun(string text)
    {
        var run = new Run(text);
        _paragraph.Inlines.Add(run);
        return run;
    }

    /// <summary>Marca a frase como pergunta clicável, ou a deixa como texto comum.</summary>
    private void FinalizeSentence(Run run, string sentence)
    {
        if (!sentence.TrimEnd().EndsWith('?')) return;

        var question = sentence.Trim();
        _onQuestionDetected?.Invoke(question);
        if (_onQuestionClicked is null) return;

        run.Foreground = QuestionBrush;
        run.TextDecorations = TextDecorations.Underline;
        run.Cursor = Cursors.Hand;
        run.ToolTip = "Clique para ver uma sugestão de resposta";
        run.Tag = new QuestionData(question, RecentContext());

        Log.Write("Pergunta", $"detectada e marcada como clicável: '{question}'");
    }

    private string RecentContext()
    {
        var context = _plainText.ToString();
        return context.Length > MaxContextChars ? context[^MaxContextChars..] : context;
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (_onQuestionClicked is null) return;

        var position = _box.GetPositionFromPoint(e.GetPosition(_box), true);
        if (position?.Parent is Run { Tag: QuestionData question })
        {
            Log.Write("Pergunta", $"clique detectado numa pergunta: '{question.Question}'");
            _onQuestionClicked(question.Question, question.Context);
            e.Handled = true;
            return;
        }

        Log.Write("Pergunta", $"clique fora de pergunta (parent={position?.Parent?.GetType().Name ?? "null"}).");
    }
}
