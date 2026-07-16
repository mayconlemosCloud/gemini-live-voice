using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiTranslateV2;

/// <summary>
/// Gemini Live translate session over a raw WebSocket (proven more reliable in this project
/// than the official Google.GenAI SDK — see GeminiTranslateLite / share-tab.html, both of which
/// used this same raw approach successfully). Turn segmentation is the server's job
/// (realtimeInputConfig.automaticActivityDetection.silenceDurationMs below); we only signal
/// audioStreamEnd on an explicit local pause (mic muted), never on a guessed silence timeout —
/// that guessing is what caused problems in earlier versions of this app.
/// </summary>
public sealed class LiveClient : IDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLang;
    private readonly string _tag;
    private readonly int _inputRate;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _closing;
    private volatile bool _ready;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? InputText;
    public event Action<string>? OutputText;
    public event Action<string>? Status;

    public LiveClient(string apiKey, string model, string targetLang, int inputRate, string tag)
    {
        _apiKey = apiKey;
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
        try { _ws?.Dispose(); } catch { }
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}"), ct);

        var setup = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = $"models/{_model}",
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    ["translationConfig"] = new JsonObject
                    {
                        ["targetLanguageCode"] = _targetLang,
                        ["echoTargetLanguage"] = false
                    }
                },
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject(),
                ["realtimeInputConfig"] = new JsonObject
                {
                    ["automaticActivityDetection"] = new JsonObject
                    {
                        ["silenceDurationMs"] = 1500
                    }
                }
            }
        };
        Log.Write(_tag, "setup: " + setup.ToJsonString());
        await SendAsync(setup.ToJsonString(), ct);
    }

    public async Task SendAudioAsync(byte[] pcm, CancellationToken ct)
    {
        if (!_ready || _ws is not { State: WebSocketState.Open }) return;
        var msg = new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(pcm),
                    ["mimeType"] = $"audio/pcm;rate={_inputRate}"
                }
            }
        };
        try { await SendAsync(msg.ToJsonString(), ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(_tag, "erro ao enviar áudio: " + ex.Message); }
    }

    /// <summary>
    /// Tells the server the input paused (mic muted) so it flushes/closes the current turn
    /// cleanly. Only fired on an explicit local mute — never on a guessed silence timeout.
    /// </summary>
    public async Task SendAudioStreamEndAsync(CancellationToken ct)
    {
        if (!_ready || _ws is not { State: WebSocketState.Open }) return;
        var msg = new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } };
        try { await SendAsync(msg.ToJsonString(), ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(_tag, "erro ao enviar audioStreamEnd: " + ex.Message); }
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try { await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (!ct.IsCancellationRequested && !_closing)
        {
            try
            {
                while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Log.Write(_tag, $"servidor fechou: {_ws.CloseStatus} '{_ws.CloseStatusDescription}'");
                            goto reconnect;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    Handle(ms.ToArray());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write(_tag, "erro de recepção: " + ex.Message); }

        reconnect:
            if (ct.IsCancellationRequested || _closing) return;
            _ready = false;
            Status?.Invoke($"{_tag}: reconectando…");
            try { await Task.Delay(1000, ct); } catch { return; }
            try { await OpenAsync(ct); }
            catch (Exception ex) { Log.Write(_tag, "falha ao reconectar: " + ex.Message); }
        }
    }

    private static readonly Regex LongBase64 = new("\"[A-Za-z0-9+/=]{120,}\"", RegexOptions.Compiled);

    private void Handle(byte[] data)
    {
        // Full (redacted) dump of every message — whatever the server sends, even fields this
        // class doesn't parse below, lands in the log instead of vanishing silently.
        try
        {
            var text = Encoding.UTF8.GetString(data);
            Log.Write(_tag, "recv: " + LongBase64.Replace(text, m => $"\"<{m.Length} chars>\""));
        }
        catch { }

        JsonNode? root;
        try { root = JsonNode.Parse(data); } catch { return; }
        if (root is null) return;

        if (root["setupComplete"] is not null)
        {
            _ready = true;
            Log.Write(_tag, "sessão pronta.");
            Status?.Invoke($"{_tag}: pronto");
            return;
        }

        if (root["error"] is JsonNode err)
        {
            var msg = err["message"]?.GetValue<string>() ?? err.ToJsonString();
            Log.Write(_tag, "erro do servidor: " + msg);
            Status?.Invoke($"{_tag}: erro — {msg}");
            return;
        }

        var sc = root["serverContent"];
        if (sc is null) return;

        var inText = sc["inputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(inText)) InputText?.Invoke(inText!);

        var outText = sc["outputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(outText)) OutputText?.Invoke(outText!);

        var parts = sc["modelTurn"]?["parts"]?.AsArray();
        if (parts is null) return;
        foreach (var part in parts)
        {
            var b64 = part?["inlineData"]?["data"]?.GetValue<string>();
            if (b64 is null) continue;
            try { AudioReceived?.Invoke(Convert.FromBase64String(b64)); } catch { }
        }
    }

    public void Dispose()
    {
        _closing = true;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
