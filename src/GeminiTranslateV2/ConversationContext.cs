using System.Text;

namespace GeminiTranslateV2;

/// <summary>
/// Acumula a conversa traduzida dos dois lados (rótulo + texto) para servir de contexto às
/// ações de IA do overlay (sugerir resposta, analisar print). O texto de tradução chega em
/// fragmentos por direção; agrupamos por falante e mantemos só os últimos N caracteres.
/// Thread-safe — alimentado de várias direções.
/// </summary>
public sealed class ConversationContext
{
    private const int MaxChars = 4000;

    private readonly object _lock = new();
    private readonly StringBuilder _sb = new();
    private string? _lastSpeaker;

    public void Add(string speaker, string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        lock (_lock)
        {
            if (speaker != _lastSpeaker)
            {
                if (_sb.Length > 0) _sb.Append('\n');
                _sb.Append(speaker).Append(": ");
                _lastSpeaker = speaker;
            }
            _sb.Append(fragment);
            if (_sb.Length > MaxChars * 2)
                _sb.Remove(0, _sb.Length - MaxChars);
        }
    }

    public void Clear()
    {
        lock (_lock) { _sb.Clear(); _lastSpeaker = null; }
    }

    /// <summary>Retorna os últimos <paramref name="maxChars"/> caracteres da conversa.</summary>
    public string GetRecent(int maxChars = MaxChars)
    {
        lock (_lock)
        {
            var s = _sb.ToString();
            return s.Length <= maxChars ? s : s[^maxChars..];
        }
    }

    public bool IsEmpty { get { lock (_lock) return _sb.Length == 0; } }
}
