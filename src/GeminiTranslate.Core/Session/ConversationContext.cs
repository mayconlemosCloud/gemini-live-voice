using System.Text;

namespace GeminiTranslate.Core.Session;

/// <summary>
/// Acumula a conversa traduzida dos dois lados para servir de contexto às ações de IA.
/// </summary>
/// <remarks>
/// O texto de tradução chega em fragmentos por direção; aqui é agrupado por falante e limitado
/// aos últimos caracteres. Thread-safe, porque é alimentado pelas duas direções ao mesmo tempo.
/// </remarks>
public sealed class ConversationContext
{
    private const int MaxChars = 4000;

    /// <summary>
    /// Depois disso a última pergunta é considerada velha demais para ser "a pergunta atual" —
    /// uma pergunta de dez minutos atrás já foi respondida.
    /// </summary>
    private static readonly TimeSpan QuestionFreshness = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private string? _lastSpeaker;
    private string? _lastQuestion;
    private DateTime _lastQuestionAt;

    /// <summary>True quando nada foi acumulado ainda.</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return _text.Length == 0; }
    }

    /// <summary>A última pergunta, se ainda for recente o bastante para valer a pena responder.</summary>
    public string? RecentQuestion
    {
        get
        {
            lock (_gate)
                return _lastQuestion is not null && DateTime.UtcNow - _lastQuestionAt <= QuestionFreshness
                    ? _lastQuestion
                    : null;
        }
    }

    /// <summary>Acrescenta um fragmento de fala, abrindo uma linha nova quando o falante muda.</summary>
    public void Add(string speaker, string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;

        lock (_gate)
        {
            if (speaker != _lastSpeaker)
            {
                if (_text.Length > 0) _text.Append('\n');
                _text.Append(speaker).Append(": ");
                _lastSpeaker = speaker;
            }

            _text.Append(fragment);
            if (_text.Length > MaxChars * 2) _text.Remove(0, _text.Length - MaxChars);
        }
    }

    /// <summary>Esquece a conversa acumulada.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _text.Clear();
            _lastSpeaker = null;
            _lastQuestion = null;
        }
    }

    /// <summary>Registra a última pergunta detectada na fala da outra pessoa.</summary>
    public void NoteQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;

        lock (_gate)
        {
            _lastQuestion = question.Trim();
            _lastQuestionAt = DateTime.UtcNow;
        }
    }

    /// <summary>Os últimos <paramref name="maxChars"/> caracteres da conversa.</summary>
    public string GetRecent(int maxChars = MaxChars)
    {
        lock (_gate)
        {
            var all = _text.ToString();
            return all.Length <= maxChars ? all : all[^maxChars..];
        }
    }
}
