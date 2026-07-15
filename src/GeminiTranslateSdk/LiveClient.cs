using System.Text.Json;
using System.Text.RegularExpressions;
using Google.GenAI;
using Google.GenAI.Types;

namespace GeminiTranslateSdk;

/// <summary>
/// Same Live translate session as GeminiTranslateLite's LiveClient (mono PCM16 in at the
/// capture's native rate, 24 kHz PCM16 translation out, transparent reconnect), but built on
/// the official Google.GenAI SDK's AsyncSession instead of a hand-rolled ClientWebSocket —
/// message framing, base64 audio encoding and JSON shapes are the SDK's job, not ours. The
/// trade-off: the raw client could log the exact JSON on the wire, including the server's
/// "error" field verbatim; LiveServerMessage exposes no Error property, so we can't point at
/// one field for that anymore. Compensated for by logging a full JSON dump of every outgoing
/// config and every incoming message (see Compact) — whatever the SDK actually populates,
/// including fields this class doesn't explicitly branch on, lands in the session log.
/// </summary>
public sealed class LiveClient : IDisposable
{
    private readonly Client _client;
    private readonly string _model;
    private readonly string _targetLang;
    private readonly int _inputRate;
    private readonly string _tag;

    private AsyncSession? _session;
    private CancellationTokenSource? _cts;
    private volatile bool _closing;
    private volatile bool _ready;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? InputText;
    public event Action<string>? OutputText;
    public event Action<string>? Status;

    public LiveClient(string apiKey, string model, string targetLang, int inputRate, string tag)
    {
        // The translate-preview model lives on v1beta; the example Google publishes for this
        // model pins the same version explicitly rather than relying on the SDK's default.
        _client = new Client(apiKey: apiKey, httpOptions: new HttpOptions { ApiVersion = "v1beta" });
        _model = model;
        _targetLang = targetLang;
        _inputRate = inputRate;
        _tag = tag;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await OpenAsync(_cts.Token); // first open awaited so auth errors surface in the UI
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        _ready = false;
        var config = new LiveConnectConfig
        {
            ResponseModalities = new List<Modality> { Modality.Audio },
            TranslationConfig = new TranslationConfig
            {
                TargetLanguageCode = _targetLang,
                EchoTargetLanguage = false
            },
            InputAudioTranscription = new AudioTranscriptionConfig(),
            OutputAudioTranscription = new AudioTranscriptionConfig(),
            // Let the server's own VAD decide turn boundaries instead of guessing client-side:
            // SilenceDurationMs is how long IT waits through silence before ending a speech
            // turn — set generously so a speaker with long natural pauses doesn't get every
            // sentence split into its own turn (see AudioIn's SilenceStopMs, which must stay
            // above this so the server always closes the turn on its own before we ever do).
            RealtimeInputConfig = new RealtimeInputConfig
            {
                AutomaticActivityDetection = new AutomaticActivityDetection { SilenceDurationMs = 1500 }
            }
        };
        Log.Write(_tag, "setup: " + Compact(config));
        _session = await _client.Live.ConnectAsync(_model, config, ct);
        Log.Write(_tag, $"conectado, aguardando setupComplete (modelo: {_model}).");
    }

    public async Task SendAudioAsync(byte[] pcm, CancellationToken ct)
    {
        if (!_ready || _session is null) return;
        try
        {
            await _session.SendRealtimeInputAsync(new LiveSendRealtimeInputParameters
            {
                Audio = new Blob { Data = pcm, MimeType = $"audio/pcm;rate={_inputRate}" }
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(_tag, "erro ao enviar áudio: " + ex); }
    }

    /// <summary>
    /// Tells the server the input paused (mic muted / speaker went quiet) so it flushes/closes
    /// the current turn cleanly instead of being left mid-stream with no explanation — per the
    /// Live API docs, this is the correct signal for a stream pause under automatic VAD.
    /// Reopening afterwards needs no counterpart message: sending audio again is enough.
    /// </summary>
    public async Task SendAudioStreamEndAsync(CancellationToken ct)
    {
        if (!_ready || _session is null) return;
        try
        {
            await _session.SendRealtimeInputAsync(
                new LiveSendRealtimeInputParameters { AudioStreamEnd = true }, ct);
            Log.Write(_tag, "audioStreamEnd enviado.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(_tag, "erro ao enviar audioStreamEnd: " + ex); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_closing)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await _session!.ReceiveAsync(ct);
                    if (msg is null)
                    {
                        Log.Write(_tag, "servidor fechou a sessão.");
                        break;
                    }
                    Handle(msg);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write(_tag, "erro de recepção: " + ex); }

            if (ct.IsCancellationRequested || _closing) return;
            _ready = false;
            Status?.Invoke($"{_tag}: reconectando…");
            try { await Task.Delay(1000, ct); } catch { return; }
            try { await OpenAsync(ct); }
            catch (Exception ex) { Log.Write(_tag, "falha ao reconectar: " + ex); }
        }
    }

    private void Handle(LiveServerMessage msg)
    {
        // Full dump BEFORE we branch on the 3-4 fields this class knows about — a ToolCall,
        // GoAway, UsageMetadata or anything else the SDK doesn't hand us a dedicated event for
        // still lands in the log this way, instead of silently vanishing.
        Log.Write(_tag, "recv: " + Compact(msg));

        if (msg.SetupComplete is not null)
        {
            _ready = true;
            Log.Write(_tag, "sessão pronta.");
            Status?.Invoke($"{_tag}: pronto");
            return;
        }

        var sc = msg.ServerContent;
        if (sc is null) return;

        var inText = sc.InputTranscription?.Text;
        if (!string.IsNullOrEmpty(inText)) InputText?.Invoke(inText!);

        var outText = sc.OutputTranscription?.Text;
        if (!string.IsNullOrEmpty(outText)) OutputText?.Invoke(outText!);

        var parts = sc.ModelTurn?.Parts;
        if (parts is null) return;
        foreach (var part in parts)
        {
            if (part?.InlineData?.Data is { Length: > 0 } data) AudioReceived?.Invoke(data);
        }
    }

    private static readonly Regex LongBase64 = new("\"[A-Za-z0-9+/=]{120,}\"", RegexOptions.Compiled);

    /// <summary>
    /// JSON dump of anything sent/received, for when something breaks and the SDK hides detail
    /// the raw-WebSocket client used to show directly. Long base64-looking string values (audio
    /// Blob.Data, mainly) are collapsed to a length marker — the bytes are already captured
    /// losslessly in Direction's "*-enviado-/-recebido-*.wav" taps, so repeating them here would
    /// only bloat the text log without adding anything a debugger could use.
    /// </summary>
    private static string Compact(object value)
    {
        string json;
        try { json = JsonSerializer.Serialize(value); }
        catch (Exception ex) { return $"<falha ao serializar para log: {ex.Message}>"; }
        return LongBase64.Replace(json, m => $"\"<{m.Length} chars>\"");
    }

    public void Dispose()
    {
        _closing = true;
        try { _cts?.Cancel(); } catch { }
        try { _ = _session?.CloseAsync(); } catch { }
    }
}
