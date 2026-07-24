using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GeminiTranslateV2;

/// <summary>
/// Preenche o RichTextBox da outra pessoa com a tradução em streaming e, quando uma frase
/// termina em "?", finaliza essa frase como um trecho sublinhado e clicável. Clicar dispara
/// <c>_onQuestion(pergunta, contextoRecente)</c>, que abre a janela de sugestão.
///
/// O clique NÃO usa Hyperlink.Click (que costuma não disparar num RichTextBox somente-leitura);
/// em vez disso um handler de PreviewMouseLeftButtonUp faz hit-test na posição e verifica se o
/// Run sob o cursor é uma pergunta (marcada via Tag). Se <c>_onQuestion</c> for nulo (assistente
/// desligado), o texto é exibido normalmente. Todos os métodos rodam na thread da UI.
/// </summary>
public sealed class QuestionTranscript
{
    private const int MaxContextChars = 2000;
    private static readonly char[] SentenceEnders = { '.', '!', '?', '…' };

    private sealed record QuestionData(string Question, string Context);

    private readonly RichTextBox _box;
    private readonly Paragraph _para;
    private readonly Action<string, string>? _onQuestion;
    private readonly StringBuilder _plain = new(); // texto completo já recebido (para contexto)
    private Run? _pending;                          // frase em construção, exibida ao vivo

    public QuestionTranscript(RichTextBox box, Action<string, string>? onQuestion)
    {
        _box = box;
        _onQuestion = onQuestion;
        _para = new Paragraph { Margin = new Thickness(0) };
        var doc = new FlowDocument(_para) { PagePadding = new Thickness(0) };
        _box.Document = doc;

        if (_onQuestion is not null)
            _box.PreviewMouseLeftButtonUp += OnBoxClick;
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _plain.Append(text);
        if (_plain.Length > MaxContextChars * 2)
            _plain.Remove(0, _plain.Length - MaxContextChars * 2);

        _pending ??= AddRun("");
        _pending.Text += text;

        // Fatia todas as frases completas do texto pendente.
        int idx;
        while ((idx = _pending.Text.IndexOfAny(SentenceEnders)) >= 0)
        {
            int cut = idx + 1;
            while (cut < _pending.Text.Length && _pending.Text[cut] == ' ') cut++;

            string sentence = _pending.Text[..cut];
            string remainder = _pending.Text[cut..];

            _pending.Text = sentence;
            FinalizeSentence(_pending, sentence);

            _pending = AddRun(remainder);
        }

        _box.ScrollToEnd();
    }

    public void Clear()
    {
        _para.Inlines.Clear();
        _plain.Clear();
        _pending = null;
    }

    private Run AddRun(string text)
    {
        var run = new Run(text);
        _para.Inlines.Add(run);
        return run;
    }

    private void FinalizeSentence(Run run, string sentence)
    {
        bool isQuestion = sentence.TrimEnd().EndsWith("?");
        if (!isQuestion || _onQuestion is null)
            return; // frase comum: deixa o Run como está.

        string context = _plain.ToString();
        if (context.Length > MaxContextChars)
            context = context[^MaxContextChars..];

        run.Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0xAB, 0xF7));
        run.TextDecorations = TextDecorations.Underline;
        run.Cursor = Cursors.Hand;
        run.ToolTip = "Clique para ver uma sugestão de resposta";
        run.Tag = new QuestionData(sentence.Trim(), context);
        Log.Write("Pergunta", $"detectada e marcada como clicável: '{sentence.Trim()}'");
    }

    private void OnBoxClick(object sender, MouseButtonEventArgs e)
    {
        if (_onQuestion is null) return;
        var pos = _box.GetPositionFromPoint(e.GetPosition(_box), true);
        if (pos?.Parent is Run { Tag: QuestionData q })
        {
            Log.Write("Pergunta", $"clique detectado numa pergunta: '{q.Question}'");
            _onQuestion(q.Question, q.Context);
            e.Handled = true;
        }
        else
        {
            Log.Write("Pergunta", $"clique fora de pergunta (parent={pos?.Parent?.GetType().Name ?? "null"}).");
        }
    }
}
