using GeminiLiveTranslate.Audio;
using GeminiLiveTranslate.Config;
using GeminiLiveTranslate.Logging;

namespace GeminiLiveTranslate.Translation;

/// <summary>
/// Owns both translation directions and exposes a single Start/Stop surface to the UI.
///   Incoming: meeting output (loopback) -> translate to your language -> your headphones.
///   Outgoing: your mic -> translate to their language -> virtual mic the meeting uses.
/// </summary>
public sealed class TranslationEngine
{
    private TranslationDirection? _incoming;
    private TranslationDirection? _outgoing;

    public bool Running { get; private set; }

    private long _inTokens, _outTokens, _totalTokens;

    public event Action<string>? IncomingText;   // what the other person said, in your language
    public event Action<string>? OutgoingText;    // what you said, in their language
    public event Action<float>? IncomingLevel;
    public event Action<float>? OutgoingLevel;
    public event Action<string>? Status;
    /// <summary>Cumulative (inputTokens, outputTokens, totalTokens) across both sessions.</summary>
    public event Action<long, long, long>? UsageChanged;

    public async Task StartAsync(AppSettings s)
    {
        if (Running) return;

        _inTokens = _outTokens = _totalTokens = 0;
        Log.Info("Engine", $"StartAsync — modelo='{s.Model}', entrada→'{s.IncomingTargetLang}', saída→'{s.OutgoingTargetLang}', saídaAtiva={s.EnableOutgoing}");

        if (string.IsNullOrWhiteSpace(s.ApiKey))
            throw new InvalidOperationException("Informe a sua API key do Google AI Studio.");
        if (string.IsNullOrWhiteSpace(s.MeetingOutputDeviceId) || string.IsNullOrWhiteSpace(s.HeadphonesDeviceId))
            throw new InvalidOperationException("Selecione o dispositivo de áudio da reunião e o seu fone.");

        // Anti-feedback guard: capturing and playing on the same endpoint loops the audio
        // back into the model (the repetition/echo bug).
        if (s.MeetingOutputDeviceId == s.HeadphonesDeviceId)
            throw new InvalidOperationException(
                "O 'Áudio da reunião' (captura) e o 'fone' (saída) são o MESMO dispositivo — isso causa eco e a fala fica repetindo em loop. " +
                "Use dispositivos diferentes: ex. capturar do 'CABLE Input' e ouvir nos 'Altofalantes', ou capturar dos alto-falantes e ouvir num fone separado.");

        // Incoming: understand the other person.
        var meetingDevice = AudioDeviceService.GetById(s.MeetingOutputDeviceId!);
        var headphones = AudioDeviceService.GetById(s.HeadphonesDeviceId!);
        _incoming = new TranslationDirection(
            "Entrada", meetingDevice, loopback: true, headphones,
            s.ApiKey, s.Model, s.IncomingTargetLang, echoTargetLanguage: false,
            continuousStreaming: s.ContinuousStreaming, duckOriginal: s.DuckOriginal);
        _incoming.TranslatedText += t => IncomingText?.Invoke(t);
        _incoming.InputLevel += l => IncomingLevel?.Invoke(l);
        _incoming.Status += m => Status?.Invoke(m);
        _incoming.Usage += OnUsage;

        // Outgoing: let the other person understand you.
        if (s.EnableOutgoing)
        {
            if (string.IsNullOrWhiteSpace(s.MicDeviceId) || string.IsNullOrWhiteSpace(s.VirtualMicDeviceId))
                throw new InvalidOperationException("Selecione o seu microfone e o microfone virtual (VB-CABLE).");

            var mic = AudioDeviceService.GetById(s.MicDeviceId!);
            var virtualMic = AudioDeviceService.GetById(s.VirtualMicDeviceId!);
            _outgoing = new TranslationDirection(
                "Saída", mic, loopback: false, virtualMic,
                s.ApiKey, s.Model, s.OutgoingTargetLang, echoTargetLanguage: false,
                continuousStreaming: s.ContinuousStreaming, duckOriginal: s.DuckOriginal);
            _outgoing.TranslatedText += t => OutgoingText?.Invoke(t);
            _outgoing.InputLevel += l => OutgoingLevel?.Invoke(l);
            _outgoing.Status += m => Status?.Invoke(m);
            _outgoing.Usage += OnUsage;
        }

        try
        {
            await _incoming.StartAsync();
            if (_outgoing is not null)
                await _outgoing.StartAsync();
            Running = true;
            Log.Info("Engine", "Tradução ativa nas duas direções." );
        }
        catch (Exception ex)
        {
            Log.Error("Engine", "Falha ao iniciar — revertendo", ex);
            await StopAsync();
            throw;
        }
    }

    private void OnUsage(long prompt, long resp, long total)
    {
        // Live API bills per turn for the whole context window, so each usageMetadata is summed.
        long i = Interlocked.Add(ref _inTokens, prompt);
        long o = Interlocked.Add(ref _outTokens, resp);
        long t = Interlocked.Add(ref _totalTokens, total);
        UsageChanged?.Invoke(i, o, t);
    }

    public async Task StopAsync()
    {
        if (Running || _incoming is not null || _outgoing is not null)
            Log.Info("Engine", "StopAsync");
        Running = false;
        if (_incoming is not null) { try { await _incoming.StopAsync(); } catch { } _incoming = null; }
        if (_outgoing is not null) { try { await _outgoing.StopAsync(); } catch { } _outgoing = null; }
    }
}
