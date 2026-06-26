using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GeminiLiveTranslate.Logging;

namespace GeminiLiveTranslate.Gemini;

/// <summary>
/// One bidirectional Live API session (BidiGenerateContent) configured for
/// speech-to-speech translation toward a single target language.
///
/// Wire protocol (JSON text frames over WSS):
///   setup            -> { setup: { model, generationConfig, translationConfig, ... } }
///   setupComplete    <- session ready
///   realtimeInput    -> { realtimeInput: { audio: { data, mimeType } } }
///   serverContent    <- { serverContent: { modelTurn: { parts:[{inlineData}] }, outputTranscription, ... } }
/// </summary>
public sealed class GeminiLiveClient : IDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLang;
    private readonly bool _echoTargetLanguage;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    private long _audioChunksSent;
    private long _audioBytesSent;
    private long _audioChunksDropped;
    private long _audioChunksReceived;
    private long _audioBytesReceived;
    private long _messagesReceived;

    /// <summary>Short label used as the log category (e.g. "Entrada"/"Saída").</summary>
    public string Tag { get; set; } = "WS";

    public event Action<byte[]>? AudioReceived;
    public event Action? Interrupted;
    /// <summary>(promptTokens, responseTokens, totalTokens) from a usageMetadata message.</summary>
    public event Action<long, long, long>? UsageUpdated;
    public event Action<string>? InputTranscript;
    public event Action<string>? OutputTranscript;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    public bool IsReady { get; private set; }

    public GeminiLiveClient(string apiKey, string model, string targetLang, bool echoTargetLanguage)
    {
        _apiKey = apiKey;
        _model = model;
        _targetLang = targetLang;
        _echoTargetLanguage = echoTargetLanguage;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        var uri = new Uri($"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}");
        Log.Info(Tag, $"Conectando WebSocket — modelo='{_model}', alvo='{_targetLang}', echo={_echoTargetLanguage}, key={Log.Mask(_apiKey)}");
        Log.Debug(Tag, $"Endpoint: {Endpoint}?key=***");
        try
        {
            await _ws.ConnectAsync(uri, ct);
            Log.Info(Tag, $"WebSocket conectado (estado={_ws.State}).");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Falha ao conectar o WebSocket", ex);
            throw;
        }

        var setup = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = $"models/{_model}",
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    // translationConfig must live INSIDE generationConfig, not at setup top level.
                    ["translationConfig"] = new JsonObject
                    {
                        ["targetLanguageCode"] = _targetLang,
                        ["echoTargetLanguage"] = _echoTargetLanguage
                    }
                },
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject(),
                // Matches Google's official example: let the server do voice-activity
                // detection / turn segmentation on the continuous stream.
                ["realtimeInputConfig"] = new JsonObject
                {
                    ["automaticActivityDetection"] = new JsonObject { ["disabled"] = false }
                }
            }
        };
        Log.Info(Tag, "Enviando setup: " + setup.ToJsonString());
        await SendJsonAsync(setup, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        StatusChanged?.Invoke("conectado");
    }

    public async Task SendAudioAsync(byte[] pcm16k, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open } || !IsReady)
        {
            // Log occasionally so we can see if audio is being dropped pre-handshake.
            if (Interlocked.Increment(ref _audioChunksDropped) % 50 == 1)
                Log.Debug(Tag, $"Áudio DESCARTADO (sessão não pronta: ws={_ws?.State}, pronto={IsReady}).");
            return;
        }

        var msg = new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(pcm16k),
                    ["mimeType"] = "audio/pcm;rate=16000"
                }
            }
        };

        try
        {
            await SendJsonAsync(msg, ct);
            long n = Interlocked.Increment(ref _audioChunksSent);
            Interlocked.Add(ref _audioBytesSent, pcm16k.Length);
            if (n == 1) Log.Info(Tag, "Primeiro chunk de áudio enviado ao Gemini.");
            if (n % 50 == 0) Log.Debug(Tag, $"Áudio enviado: {n} chunks / {_audioBytesSent / 1024} KB.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(Tag, "Erro ao enviar áudio", ex);
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private async Task SendJsonAsync(JsonObject obj, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        Log.Debug(Tag, "Loop de recepção iniciado — aguardando setupComplete…");
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
                        IsReady = false;
                        Log.Warn(Tag, $"⛔ Servidor FECHOU a conexão: code={(int?)_ws.CloseStatus} ({_ws.CloseStatus}) " +
                                      $"descrição='{_ws.CloseStatusDescription}'");
                        StatusChanged?.Invoke($"fechado pelo servidor: {_ws.CloseStatusDescription ?? _ws.CloseStatus?.ToString()}");
                        ErrorOccurred?.Invoke(_ws.CloseStatusDescription ?? "Conexão fechada pelo servidor");
                        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                HandleMessage(ms.ToArray());
            }
            Log.Info(Tag, $"Loop de recepção encerrado (ws={_ws?.State}).");
        }
        catch (OperationCanceledException) { Log.Debug(Tag, "Recepção cancelada."); }
        catch (WebSocketException wex)
        {
            Log.Error(Tag, $"WebSocketException (wsErr={wex.WebSocketErrorCode}, wsState={_ws?.State}, " +
                           $"closeCode={(int?)_ws?.CloseStatus}, closeDesc='{_ws?.CloseStatusDescription}')", wex);
            ErrorOccurred?.Invoke(wex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Erro no loop de recepção", ex);
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private void HandleMessage(byte[] data)
    {
        long n = Interlocked.Increment(ref _messagesReceived);
        string raw = Encoding.UTF8.GetString(data);

        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Mensagem não-JSON ({data.Length} bytes): {raw[..Math.Min(200, raw.Length)]} :: {ex.Message}");
            return;
        }
        if (root is null) return;

        // Log every server message (audio base64 shortened). First few logged fully-ish.
        Log.Debug(Tag, $"RX #{n}: {Log.Summarize(raw)}");

        // Token usage (billing signal) can arrive in any message.
        var um = root["usageMetadata"];
        if (um is not null)
        {
            long prompt = GetLong(um, "promptTokenCount");
            long resp = GetLong(um, "responseTokenCount");
            if (resp == 0) resp = GetLong(um, "candidatesTokenCount");
            long total = GetLong(um, "totalTokenCount");
            if (total == 0) total = prompt + resp;
            Log.Debug(Tag, $"usageMetadata: prompt={prompt} resp={resp} total={total}");
            UsageUpdated?.Invoke(prompt, resp, total);
        }

        if (root["setupComplete"] is not null)
        {
            IsReady = true;
            Log.Info(Tag, "setupComplete recebido — sessão PRONTA.");
            StatusChanged?.Invoke("pronto");
            return;
        }

        if (root["error"] is JsonNode error)
        {
            var emsg = error["message"]?.GetValue<string>() ?? error.ToJsonString();
            Log.Error(Tag, "Erro do servidor: " + emsg);
            ErrorOccurred?.Invoke(emsg);
            return;
        }

        var sc = root["serverContent"];
        if (sc is null)
        {
            Log.Debug(Tag, "Mensagem sem 'serverContent' (ignorada para áudio).");
            return;
        }

        // Barge-in: server tells us its previous output is no longer valid → drop pending audio.
        if (sc["interrupted"]?.GetValue<bool>() == true)
        {
            Log.Info(Tag, "interrupted — limpando áudio pendente de reprodução.");
            Interrupted?.Invoke();
        }

        var inputText = sc["inputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(inputText))
        {
            Log.Info(Tag, $"Transcrição (original): \"{inputText}\"");
            InputTranscript?.Invoke(inputText!);
        }

        var outputText = sc["outputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(outputText))
        {
            Log.Info(Tag, $"Transcrição (traduzido): \"{outputText}\"");
            OutputTranscript?.Invoke(outputText!);
        }

        if (sc["turnComplete"]?.GetValue<bool>() == true)
            Log.Debug(Tag, "turnComplete.");

        var parts = sc["modelTurn"]?["parts"]?.AsArray();
        if (parts is null) return;

        foreach (var part in parts)
        {
            var inline = part?["inlineData"];
            if (inline is null) continue;
            var mime = inline["mimeType"]?.GetValue<string>();
            var b64 = inline["data"]?.GetValue<string>();
            if (b64 is null) continue;
            if (mime is not null && !mime.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug(Tag, $"inlineData não-áudio ignorado (mime={mime}).");
                continue;
            }
            try
            {
                var bytes = Convert.FromBase64String(b64);
                long rc = Interlocked.Increment(ref _audioChunksReceived);
                Interlocked.Add(ref _audioBytesReceived, bytes.Length);
                if (rc == 1) Log.Info(Tag, $"Primeiro áudio recebido do Gemini (mime={mime}, {bytes.Length} bytes).");
                if (rc % 50 == 0) Log.Debug(Tag, $"Áudio recebido: {rc} chunks / {_audioBytesReceived / 1024} KB.");
                AudioReceived?.Invoke(bytes);
            }
            catch (Exception ex) { Log.Error(Tag, "Falha ao decodificar áudio base64", ex); }
        }
    }

    private static long GetLong(JsonNode node, string key)
    {
        try { return node[key]?.GetValue<long>() ?? 0; }
        catch
        {
            try { return (long)(node[key]?.GetValue<double>() ?? 0); }
            catch { return 0; }
        }
    }

    public async Task CloseAsync()
    {
        IsReady = false;
        Log.Info(Tag, $"Fechando sessão. Enviados {_audioChunksSent} / descartados {_audioChunksDropped} chunks; " +
                      $"recebidos {_audioChunksReceived} chunks de áudio em {_messagesReceived} mensagens.");
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_ws is { State: WebSocketState.Open })
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
