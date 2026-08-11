using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;

namespace GeminiTranslate.Infrastructure.Gemini;

/// <summary>
/// Sessão de tradução ao vivo do Gemini sobre um WebSocket cru.
/// </summary>
/// <remarks>
/// O WebSocket cru provou-se mais confiável neste projeto do que o SDK oficial Google.GenAI.
///
/// A segmentação de turno é problema do servidor, e agora inteiramente dele: não se manda mais
/// realtimeInputConfig. O modelo espera um stream contínuo e cru e cuida sozinho de detectar
/// idioma, fechar frases e preservar entonação, ritmo e tom. Tudo que mexe nesse stream antes de
/// ele chegar lá — VAD nosso, gate de silêncio, corte de pausa — tira do modelo a informação de
/// prosódia que ele usa. Do nosso lado só existe audioStreamEnd numa pausa local explícita, com
/// o microfone mudo, nunca num timeout de silêncio adivinhado.
/// </remarks>
public sealed class LiveTranslateClient : ITranslationStream
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>
    /// Capacidade da fila de saída: cerca de 50 s de áudio. Encher significa que a sessão já está
    /// quebrada, e descartar o mais antigo devolve a conversa ao tempo real.
    /// </summary>
    private const int OutboxCapacity = 500;

    /// <summary>Quanto segurar o áudio numa reconexão planejada, antes de voltar a descartar.</summary>
    private const int PlannedReconnectHoldMs = 10_000;

    /// <summary>Recuo antes de reabrir depois de uma queda inesperada.</summary>
    private const int UnplannedReconnectDelayMs = 1000;

    private const int BacklogLogIntervalMs = 5000;
    private const int ReceiveBufferBytes = 64 * 1024;

    private static readonly Regex LongBase64 = new("\"[A-Za-z0-9+/=]{120,}\"", RegexOptions.Compiled);

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLang;
    private readonly string _tag;
    private readonly int _inputRate;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Fila de saída com produtor único (thread de captura) e consumidor único.
    /// </summary>
    /// <remarks>
    /// Existe para garantir ORDEM. Antes os chunks eram enviados em fire-and-forget e disputavam
    /// o semáforo de envio, que não é FIFO: sob rajada o áudio chegava embaralhado ao servidor,
    /// atrapalhando a segmentação e a tradução. Um item null representa audioStreamEnd, que assim
    /// também respeita a ordem em relação ao áudio já enfileirado.
    /// </remarks>
    private readonly Channel<byte[]?> _outbox;

    private int _dropCount;
    private int _loggedDrops;
    private long _lastBacklogLogAt;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private volatile bool _closing;
    private volatile bool _ready;

    /// <summary>
    /// Último handle de retomada mandado pelo servidor.
    /// </summary>
    /// <remarks>
    /// A sessão de áudio dura 15 min e a conexão WebSocket cerca de 10 min; passando disso o
    /// servidor manda goAway e derruba. Reabrir COM este handle devolve o estado da sessão em vez
    /// de começar do zero — sem ele cada reconexão custava uma sessão fria, e era esse degrau que
    /// fazia o atraso saltar de ~3 s para ~6 s depois dos 10 minutos de chamada. O servidor já
    /// mandava estes handles antes; eram todos descartados.
    /// </remarks>
    private volatile string? _resumeHandle;

    /// <summary>Reconexão pedida pelo próprio servidor (goAway) — imediata e com handle.</summary>
    private volatile bool _reconnectRequested;

    /// <summary>
    /// Até quando vale SEGURAR o áudio em vez de descartá-lo enquanto a sessão não está pronta.
    /// </summary>
    /// <remarks>
    /// Numa reconexão planejada a sessão volta em cerca de 1 s com o estado preservado, então
    /// descartar aqui é perder palavra à toa. Numa queda inesperada, de duração desconhecida,
    /// continua valendo o descarte: áudio velho numa tradução ao vivo nunca é recuperado.
    /// </remarks>
    private long _holdAudioUntil;

    /// <summary>
    /// Se o servidor aceita sessionResumption e contextWindowCompression.
    /// </summary>
    /// <remarks>
    /// A documentação deste modelo já esteve errada sobre onde campos de setup moram, então uma
    /// recusa derruba só o recurso, não o app: cai para o comportamento antigo em vez de ficar
    /// sem conexão.
    /// </remarks>
    private volatile bool _sessionFeatures = true;

    /// <summary>Chunk de tradução falada recebido do servidor.</summary>
    public event Action<byte[]>? AudioReceived;

    /// <summary>Transcrição do que entrou.</summary>
    public event Action<string>? InputText;

    /// <summary>Transcrição do que saiu traduzido.</summary>
    public event Action<string>? OutputText;

    /// <summary>Mudança de estado legível para a interface.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Chunks ainda esperando para ir para a rede. Deve ficar em 0 ou 1: qualquer valor que se
    /// sustente acima disso é atraso puro criado AQUI, por uplink saturado, e não pelo modelo.
    /// </summary>
    public int OutboxBacklog => _outbox.Reader.Count;

    /// <param name="apiKey">Chave do Google AI Studio.</param>
    /// <param name="model">Modelo de tradução ao vivo.</param>
    /// <param name="targetLang">Código do idioma de saída.</param>
    /// <param name="inputRate">Taxa do áudio enviado.</param>
    /// <param name="tag">Origem exibida no log.</param>
    public LiveTranslateClient(string apiKey, string model, string targetLang, int inputRate, string tag)
    {
        _apiKey = apiKey;
        _model = model;
        _targetLang = targetLang;
        _inputRate = inputRate;
        _tag = tag;

        _outbox = Channel.CreateBounded<byte[]?>(
            new BoundedChannelOptions(OutboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _dropCount));
    }

    /// <summary>
    /// Abre a sessão. A primeira abertura é aguardada para que erros de autenticação apareçam na
    /// interface; os laços de recepção e envio seguem em segundo plano.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await OpenAsync(_cts.Token);
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => PumpOutboxAsync(_cts.Token));
    }

    /// <summary>Enfileira um chunk de áudio sem bloquear a thread de captura.</summary>
    public void EnqueueAudio(byte[] pcm) => _outbox.Writer.TryWrite(pcm);

    /// <summary>
    /// Avisa o servidor que a entrada pausou, para ele fechar o turno atual de forma limpa. Vai
    /// pela mesma fila do áudio para chegar depois do que já foi capturado.
    /// </summary>
    public void EnqueueAudioStreamEnd() => _outbox.Writer.TryWrite(null);

    private async Task OpenAsync(CancellationToken ct)
    {
        _ready = false;
        try { _socket?.Dispose(); } catch { }

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(new Uri($"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}"), ct);

        var setup = new JsonObject { ["setup"] = BuildSetupBody() };
        Log.Write(_tag, $"setup ({(_resumeHandle is null ? "sessão nova" : "retomando sessão")}): "
                        + setup.ToJsonString());
        await SendAsync(setup.ToJsonString(), ct);
    }

    /// <summary>
    /// Monta o corpo de setup da sessão.
    /// </summary>
    /// <remarks>
    /// realtimeInputConfig.automaticActivityDetection NÃO entra aqui. A documentação do
    /// live-translate não documenta VAD nenhum: este modelo segmenta e detecta idioma sozinho num
    /// stream contínuo. Configurar VAD à mão era um override não documentado em cima do único
    /// componente que decide onde uma frase começa e termina — e é dessa segmentação que sai a
    /// curva de entonação.
    ///
    /// inputAudioTranscription e outputAudioTranscription ficam no nível de SETUP, não dentro de
    /// generationConfig. O exemplo da documentação mostra os dois dentro de generationConfig, mas
    /// o exemplo está errado: a API fecha a conexão com
    /// <c>InvalidPayloadData: Unknown name "inputAudioTranscription" at 'setup.generation_config'</c>.
    /// Não mova de novo.
    /// </remarks>
    private JsonObject BuildSetupBody()
    {
        var body = new JsonObject
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
            ["outputAudioTranscription"] = new JsonObject()
        };

        if (!_sessionFeatures) return body;

        body["sessionResumption"] = _resumeHandle is null
            ? new JsonObject()
            : new JsonObject { ["handle"] = _resumeHandle };

        body["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() };
        return body;
    }

    private async Task PumpOutboxAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _outbox.Reader.ReadAllAsync(ct))
            {
                if (!await ReadyForSendAsync(ct)) continue;

                ReportDrops();

                long startedAt = Environment.TickCount64;
                try
                {
                    await SendAsync(BuildRealtimeMessage(item).ToJsonString(), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write(_tag, "erro ao enviar: " + ex.Message);
                }
                ReportBackpressure(Environment.TickCount64 - startedAt);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write(_tag, $"fila de envio morreu: {ex}");
        }
    }

    private JsonObject BuildRealtimeMessage(byte[]? pcm) => pcm is null
        ? new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }
        : new JsonObject
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

    /// <summary>
    /// Espera a sessão poder receber áudio. Retorna false quando este chunk deve ser DESCARTADO.
    /// </summary>
    /// <remarks>
    /// Durante uma reconexão planejada segura o chunk na mão — a sessão volta em cerca de 1 s com
    /// o estado preservado, e descartar aqui era perda de fala pura. Fora dessa janela mantém o
    /// descarte: numa queda de duração desconhecida, mandar o áudio velho depois só empurraria a
    /// conversa para trás, já que o servidor devolve tudo em 1× e nada é recuperado.
    /// </remarks>
    private async Task<bool> ReadyForSendAsync(CancellationToken ct)
    {
        while (!_ready || _socket is not { State: WebSocketState.Open })
        {
            if (ct.IsCancellationRequested || _closing) return false;
            if (Environment.TickCount64 >= _holdAudioUntil) return false;
            try { await Task.Delay(25, ct); } catch { return false; }
        }
        return true;
    }

    private void ReportDrops()
    {
        int drops = Interlocked.Exchange(ref _dropCount, 0);
        if (drops == 0) return;

        _loggedDrops += drops;
        Log.Write(_tag, $"fila de envio cheia: {drops} chunks descartados (total {_loggedDrops}).");
    }

    /// <summary>
    /// Relata uplink saturado.
    /// </summary>
    /// <remarks>
    /// Um chunk tem <see cref="CaptureChunk.DurationMs"/> ms para sair. Se o envio demora mais que
    /// isso de forma sustentada, o uplink não acompanha e a fila vira atraso permanente — o
    /// servidor devolve a tradução em 1× tempo real, então nada do que se atrasou aqui é
    /// recuperado depois. Amostrado no máximo a cada 5 s para não poluir o log.
    /// </remarks>
    private void ReportBackpressure(long sendMs)
    {
        int backlog = _outbox.Reader.Count;
        if (backlog <= 2 && sendMs < CaptureChunk.DurationMs) return;

        long now = Environment.TickCount64;
        if (now - _lastBacklogLogAt < BacklogLogIntervalMs) return;
        _lastBacklogLogAt = now;

        Log.Write(_tag, $"uplink lento: envio levou {sendMs} ms, {backlog} chunks na fila " +
                        $"(~{backlog * CaptureChunk.DurationMs} ms de atraso acumulado).");
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_closing)
        {
            try
            {
                await ReceiveUntilBreakAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write(_tag, "erro de recepção: " + ex.Message);
            }

            if (ct.IsCancellationRequested || _closing) return;
            if (!await ReconnectAsync(ct)) return;
        }
    }

    /// <summary>Lê mensagens até a conexão fechar ou o servidor pedir reconexão.</summary>
    private async Task ReceiveUntilBreakAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var message = new MemoryStream();

        while (!ct.IsCancellationRequested && _socket!.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Write(_tag, $"servidor fechou: {_socket.CloseStatus} '{_socket.CloseStatusDescription}'");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Handle(message.ToArray());
            if (_reconnectRequested) return;
        }
    }

    /// <summary>
    /// Reabre a sessão. Retorna false quando o cancelamento venceu.
    /// </summary>
    /// <remarks>
    /// Uma reconexão planejada é pedida pelo servidor, com a conexão ainda viva: não há erro do
    /// qual recuar e cada milissegundo aqui é fala que o ouvinte perde, então reabre na hora. Só
    /// a queda inesperada mantém o recuo.
    /// </remarks>
    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        _ready = false;
        bool planned = _reconnectRequested;
        _reconnectRequested = false;
        Status?.Invoke($"{_tag}: reconectando…");

        if (planned)
        {
            await CloseCurrentAsync();
        }
        else
        {
            try { await Task.Delay(UnplannedReconnectDelayMs, ct); } catch { return false; }
        }

        try { await OpenAsync(ct); }
        catch (Exception ex) { Log.Write(_tag, "falha ao reconectar: " + ex.Message); }
        return true;
    }

    /// <summary>
    /// Fecha a conexão atual de forma limpa. O goAway é exatamente um pedido para o cliente
    /// fechar; não fazer isso rendia o PolicyViolation "failed to close the connection after
    /// receiving a GoAway signal" nos logs.
    /// </summary>
    private async Task CloseCurrentAsync()
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        try
        {
            using var timeout = new CancellationTokenSource(2000);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "goAway", timeout.Token);
        }
        catch
        {
        }
    }

    private void Handle(byte[] data)
    {
        LogRedacted(data);

        JsonNode? root;
        try { root = JsonNode.Parse(data); } catch { return; }
        if (root is null) return;

        if (HandleSetupComplete(root)) return;
        if (HandleResumptionUpdate(root)) return;
        if (HandleGoAway(root)) return;
        if (HandleError(root)) return;
        HandleServerContent(root["serverContent"]);
    }

    /// <summary>
    /// Despeja toda mensagem recebida no log, com o base64 substituído por seu tamanho: o que o
    /// servidor manda fica registrado mesmo nos campos que esta classe não interpreta.
    /// </summary>
    private void LogRedacted(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            Log.Write(_tag, "recv: " + LongBase64.Replace(text, m => $"\"<{m.Length} chars>\""));
        }
        catch
        {
        }
    }

    private bool HandleSetupComplete(JsonNode root)
    {
        if (root["setupComplete"] is null) return false;

        _ready = true;
        Log.Write(_tag, "sessão pronta.");
        Status?.Invoke($"{_tag}: pronto");
        return true;
    }

    /// <summary>
    /// Guarda o handle de retomada, que é tudo o que precisa acontecer com ele aqui: é o que
    /// permite a próxima reabertura continuar a mesma sessão em vez de começar fria.
    /// </summary>
    private bool HandleResumptionUpdate(JsonNode root)
    {
        if (root["sessionResumptionUpdate"] is not { } update) return false;

        if (update["resumable"]?.GetValue<bool>() == true)
        {
            var handle = update["newHandle"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(handle)) _resumeHandle = handle;
        }
        return true;
    }

    /// <summary>
    /// O servidor avisa com antecedência que vai fechar. Ignorar esse aviso fazia a conexão morrer
    /// com PolicyViolation e voltar fria; reconectar agora, de propósito e com handle, troca essa
    /// queda por uma emenda de cerca de 1 s.
    /// </summary>
    private bool HandleGoAway(JsonNode root)
    {
        if (root["goAway"] is not { } goAway) return false;

        var timeLeft = goAway["timeLeft"]?.GetValue<string>() ?? "?";
        Log.Write(_tag, $"goAway ({timeLeft} restantes) — reconectando com handle antes de o servidor derrubar.");
        _holdAudioUntil = Environment.TickCount64 + PlannedReconnectHoldMs;
        _reconnectRequested = true;
        return true;
    }

    private bool HandleError(JsonNode root)
    {
        if (root["error"] is not { } error) return false;

        var message = error["message"]?.GetValue<string>() ?? error.ToJsonString();
        Log.Write(_tag, "erro do servidor: " + message);

        if (TryDisableSessionFeatures(message)) return true;
        if (TryDropResumeHandle()) return true;

        Status?.Invoke($"{_tag}: erro — {message}");
        return true;
    }

    /// <summary>
    /// Recusa dos campos de sessão: desliga o recurso e volta ao comportamento antigo, em vez de
    /// deixar o app sem conexão nenhuma.
    /// </summary>
    private bool TryDisableSessionFeatures(string message)
    {
        bool refused = _sessionFeatures
                       && message.Contains("Unknown name")
                       && (message.Contains("sessionResumption") || message.Contains("contextWindowCompression"));
        if (!refused) return false;

        _sessionFeatures = false;
        _resumeHandle = null;
        Log.Write(_tag, "servidor recusou os campos de sessão — reabrindo sem eles.");
        _reconnectRequested = true;
        return true;
    }

    /// <summary>Handle inválido ou expirado — eles valem 2 h: esquece o handle e recomeça limpo.</summary>
    private bool TryDropResumeHandle()
    {
        if (_resumeHandle is null) return false;

        _resumeHandle = null;
        Log.Write(_tag, "erro com handle de retomada — reabrindo como sessão nova.");
        _reconnectRequested = true;
        return true;
    }

    private void HandleServerContent(JsonNode? content)
    {
        if (content is null) return;

        var inputText = content["inputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(inputText)) InputText?.Invoke(inputText);

        var outputText = content["outputTranscription"]?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(outputText)) OutputText?.Invoke(outputText);

        if (content["modelTurn"]?["parts"]?.AsArray() is not { } parts) return;
        foreach (var part in parts)
        {
            var encoded = part?["inlineData"]?["data"]?.GetValue<string>();
            if (encoded is null) continue;
            try { AudioReceived?.Invoke(Convert.FromBase64String(encoded)); } catch { }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing = true;
        try { _outbox.Writer.TryComplete(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
