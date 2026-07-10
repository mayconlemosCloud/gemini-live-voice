using System.IO;
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
    private readonly DefaultDeviceManager _defaults = new();
    // Set only while recording; the direction handlers read it live, so a null check disables recording.
    private volatile ConversationRecorder? _recorder;

    public bool Running { get; private set; }

    /// <summary>True while the conversation is being recorded to a .wav.</summary>
    public bool IsRecording => _recorder is not null;

    /// <summary>True when the outgoing (your voice) direction is active.</summary>
    public bool HasOutgoing => _outgoing is not null;

    /// <summary>Live-adjust the incoming original volume (ducking) while running.</summary>
    public float IncomingDuckLevel { set { if (_incoming is not null) _incoming.DuckLevel = value; } }

    /// <summary>
    /// Push-to-talk: when true, your mic is muted and nothing is sent to Gemini, so leaked
    /// audio/noise can never be translated. The UI unmutes only while you hold the talk key.
    /// </summary>
    public bool OutgoingMicMuted
    {
        get => _outgoing?.MicMuted ?? true;
        set { if (_outgoing is not null) _outgoing.MicMuted = value; }
    }

    /// <summary>
    /// Push-to-talk pressed: opens the manual-VAD turn and unmutes your mic. Also ducks the
    /// INCOMING translation playback hard while you talk — with an open mic (laptop array,
    /// speakers) that playback leaks into the capture and gets re-translated, seeding the
    /// repetition loop seen in the logs.
    /// </summary>
    public Task OutgoingTalkStartAsync()
    {
        if (_incoming is not null) _incoming.TranslationDuck = TalkTranslationDuck;
        return _outgoing?.BeginTalkAsync() ?? Task.CompletedTask;
    }

    /// <summary>Push-to-talk released: mutes your mic, closes the turn and restores the incoming volume.</summary>
    public Task OutgoingTalkEndAsync()
    {
        if (_incoming is not null) _incoming.TranslationDuck = 1f;
        return _outgoing?.EndTalkAsync() ?? Task.CompletedTask;
    }

    private long _inTokens, _outTokens, _totalTokens;

    // Anti-echo (does NOT delay your speech): while you hold the talk key (mic armed), the meeting
    // capture is paused so YOUR translation can't loop back through the meeting into the incoming
    // side and get re-translated. Your mic is never gated, so talking has no added latency. A short
    // tail keeps the pause a bit after you release, to swallow the immediate echo return.
    private long _incomingResumeTicks;
    // 1500 ms: the 500 ms tail was shorter than the outgoing translation's own playback plus the
    // meeting round-trip, so the echo ("Hi, how are you?" coming back as meeting audio) slipped
    // through after release — seen in the 2026-07-06 logs.
    private const int EchoTailMs = 1500;
    // Incoming translation volume while you hold push-to-talk (leak into the open mic).
    private const float TalkTranslationDuck = 0.15f;

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
            continuousStreaming: s.ContinuousStreaming, duckOriginal: s.DuckOriginal,
            duckLevel: (float)Math.Clamp(s.DuckOriginalLevel, 0, 1),
            // Keep what you hear close to real time: the old 5 s ceiling let the incoming
            // translation lag several seconds behind the speaker before backlog was dropped.
            maxPlaybackLatencyMs: 1500);
        _incoming.TranslatedText += t => IncomingText?.Invoke(t);
        _incoming.InputLevel += l => IncomingLevel?.Invoke(l);
        _incoming.Status += m => Status?.Invoke(m);
        _incoming.Usage += OnUsage;
        // Left channel of the recording: exactly what you hear (ducked original + translation),
        // tapped straight from the player so it matches the live audio with no re-mixing.
        _incoming.RenderedAudio += (b, o, n) => _recorder?.WriteIncoming(b, o, n);

        // Outgoing: let the other person understand you.
        if (s.EnableOutgoing)
        {
            if (string.IsNullOrWhiteSpace(s.MicDeviceId) || string.IsNullOrWhiteSpace(s.VirtualMicDeviceId))
                throw new InvalidOperationException("Selecione o seu microfone e o microfone virtual (VB-CABLE).");

            // Loop guards: the outgoing translation is rendered to the virtual mic. If that same
            // endpoint is also captured (as the meeting loopback) or is your headphones, the
            // translation re-enters an input and the model re-translates its own output forever.
            if (s.VirtualMicDeviceId == s.MeetingOutputDeviceId)
                throw new InvalidOperationException(
                    "O 'microfone virtual' (saída da sua tradução) é o MESMO dispositivo capturado como 'áudio da reunião'. " +
                    "Isso reinjeta a tradução na entrada e causa loop infinito (ex.: 'Ciao… Ciao…'). Use dispositivos diferentes.");
            if (s.VirtualMicDeviceId == s.HeadphonesDeviceId)
                throw new InvalidOperationException(
                    "O 'microfone virtual' e o seu 'fone' são o MESMO dispositivo — a tradução de saída volta para a captura e gera loop. Separe-os.");

            var mic = AudioDeviceService.GetById(s.MicDeviceId!);
            var virtualMic = AudioDeviceService.GetById(s.VirtualMicDeviceId!);
            // The outgoing playback device is the meeting's virtual mic, so the passthrough of
            // the ORIGINAL (untranslated) audio must always be off here — otherwise your raw
            // voice leaks into the meeting alongside the translation ("como se eu estivesse
            // falando"). Ducking/passthrough only makes sense for the incoming direction.
            // Server-side VAD (manualActivity:false) segments your speech automatically, exactly
            // like the incoming side. Manual VAD only worked for HOLD-to-talk (release = activityEnd);
            // with the F8 TOGGLE the turn stayed open while the mic kept streaming silence, so the
            // model looped/hallucinated ("Hi, Stef, how are you doing? Oh, Stef…"). F8 now just
            // arms/disarms the mic (mute) and the server closes each turn on the natural pause.
            _outgoing = new TranslationDirection(
                "Saída", mic, loopback: false, virtualMic,
                s.ApiKey, s.Model, s.OutgoingTargetLang, echoTargetLanguage: false,
                continuousStreaming: s.ContinuousStreaming, duckOriginal: false,
                recordTranslation: true, manualActivity: false,
                // The other person must hear the FULL translated sentence: never drop backlog on
                // the outgoing player (push-to-talk already bounds accumulation). Dropping was the
                // cause of the translation reaching them choppy/cut off.
                dropPlaybackBacklog: false);
            _outgoing.TranslatedText += t => OutgoingText?.Invoke(t);
            _outgoing.InputLevel += l => OutgoingLevel?.Invoke(l);
            _outgoing.Status += m => Status?.Invoke(m);
            _outgoing.Usage += OnUsage;
            // Right channel of the recording: what the meeting receives — the translation (tapped
            // from the player) plus your original mic voice (the meeting hears both live).
            _outgoing.RenderedAudio += (b, o, n) => _recorder?.WriteOutgoing(b, o, n);
            _outgoing.CapturedAudio += pcm => _recorder?.WriteOutgoingOriginal(pcm);
            // Push-to-talk: start muted (disarmed) so leaked audio/noise is never translated.
            // F8 (or holding the Talk button/SPACE) arms the mic; F8 again disarms it.
            _outgoing.MicMuted = true;

            // Anti-echo (no latency on YOUR speech): pause the meeting capture while you're talking
            // AND while your translated voice is still being played into the meeting, so the
            // outgoing translation can't loop back through the meeting into the incoming side.
            // (The translation keeps playing for seconds after you release the key — gating only
            // on MicArmed let the returning "Hi, how are you?" get re-translated.) Your mic is
            // never gated. A tail keeps the pause a bit longer to swallow the network echo.
            _incoming.SuppressSend = () =>
            {
                if (_outgoing is { MicArmed: true } || _outgoing is { IsTranslationAudible: true })
                {
                    Volatile.Write(ref _incomingResumeTicks, DateTime.UtcNow.AddMilliseconds(EchoTailMs).Ticks);
                    return true;
                }
                return DateTime.UtcNow.Ticks < Volatile.Read(ref _incomingResumeTicks);
            };
        }

        try
        {
            await _incoming.StartAsync();
            if (_outgoing is not null)
                await _outgoing.StartAsync();
            Running = true;
            Log.Info("Engine", "Tradução ativa nas duas direções." );

            // Optionally point Windows' default endpoints at the cable so meeting apps that follow
            // the system default route themselves. The meeting should PLAY to the device we
            // loopback-capture, and (when outgoing is on) use the cable's capture side as its mic.
            if (s.AutoSetDefaultDevice)
            {
                string? meetingMic = s.EnableOutgoing && !string.IsNullOrWhiteSpace(s.VirtualMicDeviceId)
                    ? AudioDeviceService.CaptureCounterpart(s.VirtualMicDeviceId!)?.Id
                    : null;
                _defaults.SetDefaults(s.MeetingOutputDeviceId, meetingMic);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine", "Falha ao iniciar — revertendo", ex);
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Begins recording the whole conversation to a stereo .wav (left = incoming translation you
    /// hear, right = outgoing translation they hear). Requires an active session; returns the path.
    /// </summary>
    public string StartRecording()
    {
        if (!Running)
            throw new InvalidOperationException("Inicie a tradução antes de gravar a conversa.");
        if (_recorder is { } existing) return existing.Path;

        string dir = string.IsNullOrEmpty(Log.LogFolder) ? Path.GetTempPath() : Log.LogFolder;
        string path = Path.Combine(dir, $"conversa-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _recorder = new ConversationRecorder(path, _incoming!.RenderFormat, _outgoing?.RenderFormat);
        Log.Info("Engine", $"Gravação da conversa iniciada: {path}");
        return path;
    }

    /// <summary>Stops recording and finalizes the .wav. Returns the saved path, or null if not recording.</summary>
    public string? StopRecording()
    {
        var rec = _recorder;
        _recorder = null;
        if (rec is null) return null;
        rec.Dispose();
        Log.Info("Engine", $"Gravação da conversa salva: {rec.Path}");
        return rec.Path;
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
        StopRecording();   // finalize the .wav if a recording was in progress
        _defaults.Restore();
        if (_incoming is not null) { try { await _incoming.StopAsync(); } catch { } _incoming = null; }
        if (_outgoing is not null) { try { await _outgoing.StopAsync(); } catch { } _outgoing = null; }
    }
}
