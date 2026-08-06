using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace GeminiTranslateV2;

/// <summary>
/// Gemini Live translate session over a raw WebSocket (proven more reliable in this project
/// than the official Google.GenAI SDK — see GeminiTranslateLite / share-tab.html, both of which
/// used this same raw approach successfully).
///
/// Segmentação de turno é problema do servidor, e agora INTEIRAMENTE dele: não mandamos mais
/// realtimeInputConfig. O gemini-3.5-live-translate-preview espera um stream contínuo e cru e
/// cuida sozinho de detectar idioma, fechar frases e preservar entonação/ritmo/tom. Tudo que
/// mexe nesse stream antes de ele chegar lá — VAD nosso, gate de silêncio, corte de pausa —
/// tira do modelo a informação de prosódia que ele usa. Do nosso lado só existe audioStreamEnd
/// numa pausa local explícita (mic mutado), nunca num timeout de silêncio adivinhado.
/// </summary>
public sealed class LiveClient : IDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>
    /// Duração de um chunk de áudio, em ms — o tamanho que as capturas produzem e que a doc do
    /// live-translate especifica ("send audio in chunks of 100ms"). Estava escrito 40 ms espalhado
    /// pelos comentários e pelas contas de backlog desde que o chunk voltou para 100 ms, o que
    /// fazia todo relatório de atraso de fila sair 2,5× menor do que era de verdade.
    /// </summary>
    public const int ChunkMs = 100;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLang;
    private readonly string _tag;
    private readonly int _inputRate;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Fila de saída de produtor único (thread de captura) e consumidor único (PumpOutboxAsync).
    /// Existe para garantir ORDEM: antes os chunks eram enviados em fire-and-forget e disputavam
    /// _sendLock, que não é FIFO — sob rajada o áudio chegava embaralhado ao servidor, atrapalhando
    /// o VAD e a tradução. null = audioStreamEnd, que assim também respeita a ordem em relação
    /// ao áudio já enfileirado.
    /// </summary>
    private readonly Channel<byte[]?> _outbox;
    private int _dropCount;
    private int _loggedDrops;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private volatile bool _closing;
    private volatile bool _ready;

    /// <summary>
    /// Último handle de retomada mandado pelo servidor. A sessão de áudio dura 15 min e a conexão
    /// WebSocket ~10 min; passando disso o servidor manda goAway e derruba. Reabrir COM este handle
    /// devolve o estado da sessão em vez de começar do zero — sem ele cada reconexão custava uma
    /// sessão fria, e era esse degrau que fazia o atraso saltar de ~3 s para ~6 s depois dos
    /// 10 minutos de chamada. O servidor já mandava estes handles antes; eram todos descartados.
    /// </summary>
    private volatile string? _resumeHandle;

    /// <summary>Reconexão pedida pelo próprio servidor (goAway) — imediata e com handle.</summary>
    private volatile bool _reconnectRequested;

    /// <summary>
    /// Até quando vale SEGURAR o áudio em vez de descartá-lo enquanto a sessão não está pronta.
    /// Numa reconexão planejada (goAway) a sessão volta em ~1 s com o estado preservado, então
    /// descartar aqui é perder palavra à toa. Numa queda inesperada, de duração desconhecida,
    /// continua valendo o descarte: áudio velho numa tradução ao vivo nunca é recuperado.
    /// </summary>
    private long _holdAudioUntil;

    /// <summary>
    /// Se o servidor aceita sessionResumption/contextWindowCompression. A doc deste modelo já
    /// esteve errada sobre onde campos de setup moram (ver OpenAsync), então uma recusa derruba
    /// só o recurso, não o app: cai para o comportamento antigo em vez de ficar sem conexão.
    /// </summary>
    private volatile bool _sessionFeatures = true;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? InputText;
    public event Action<string>? OutputText;
    public event Action<string>? Status;

    /// <summary>
    /// Chunks de <see cref="ChunkMs"/> ms ainda esperando para ir para a rede. Deve ficar em 0–1.
    /// Qualquer valor que se sustente acima disso é atraso puro criado AQUI (uplink saturado),
    /// não pelo modelo.
    /// </summary>
    public int OutboxBacklog => _outbox.Reader.Count;

    public LiveClient(string apiKey, string model, string targetLang, int inputRate, string tag)
    {
        _apiKey = apiKey;
        _model = model;
        _targetLang = targetLang;
        _inputRate = inputRate;
        _tag = tag;

        // ~50 s a 100 ms/chunk; encher significa que a sessão já está quebrada. DropOldest
        // descarta o áudio velho (inútil numa tradução ao vivo) e volta para o tempo real.
        _outbox = Channel.CreateBounded<byte[]?>(
            new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false // áudio vem da captura, audioStreamEnd vem da UI
            },
            _ => Interlocked.Increment(ref _dropCount));
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await OpenAsync(_cts.Token); // first open awaited so auth errors surface in the UI
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => PumpOutboxAsync(_cts.Token));
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        _ready = false;
        try { _ws?.Dispose(); } catch { }
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri($"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}"), ct);

        // realtimeInputConfig.automaticActivityDetection NÃO entra aqui. A doc do live-translate
        // não documenta VAD nenhum: este modelo segmenta e detecta idioma sozinho, num stream
        // contínuo. Configurar VAD à mão era um override não documentado em cima do único
        // componente que decide onde uma frase começa e termina — e é dessa segmentação que sai a
        // curva de entonação. O AI Studio não manda esse campo.
        //
        // inputAudioTranscription/outputAudioTranscription ficam no nível de SETUP, não dentro de
        // generationConfig. O exemplo da doc mostra os dois dentro de generationConfig, mas o
        // exemplo está errado: a API fecha a conexão com
        //   InvalidPayloadData: Unknown name "inputAudioTranscription" at 'setup.generation_config'
        // (reproduzido no log session-20260729-192203). Não mova de novo.
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

        if (_sessionFeatures)
        {
            // sessionResumption vazio = "comece a me mandar handles". Com handle = "retome ESTA
            // sessão". O servidor mandava sessionResumptionUpdate o tempo todo mesmo sem pedirmos,
            // mas sem declarar o campo aqui não há como usá-los na reabertura.
            body["sessionResumption"] = _resumeHandle is null
                ? new JsonObject()
                : new JsonObject { ["handle"] = _resumeHandle };

            // Janela deslizante: sem isto a sessão de áudio morre no limite de 15 min, com goAway
            // e derrubada. Com isto ela se estende indefinidamente, e o goAway que sobra é só o do
            // limite da conexão WebSocket (~10 min), que a retomada acima cobre sem perder estado.
            body["contextWindowCompression"] = new JsonObject
            {
                ["slidingWindow"] = new JsonObject()
            };
        }

        var setup = new JsonObject { ["setup"] = body };
        Log.Write(_tag, $"setup ({(_resumeHandle is null ? "sessão nova" : "retomando sessão")}): "
                        + setup.ToJsonString());
        await SendAsync(setup.ToJsonString(), ct);
    }

    /// <summary>Enfileira um chunk de áudio. Não bloqueia a thread de captura.</summary>
    public void EnqueueAudio(byte[] pcm) => _outbox.Writer.TryWrite(pcm);

    /// <summary>
    /// Tells the server the input paused (mic muted) so it flushes/closes the current turn
    /// cleanly. Only fired on an explicit local mute — never on a guessed silence timeout.
    /// Vai pela mesma fila do áudio para chegar depois do que já foi capturado.
    /// </summary>
    public void EnqueueAudioStreamEnd() => _outbox.Writer.TryWrite(null);

    private async Task PumpOutboxAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _outbox.Reader.ReadAllAsync(ct))
            {
                if (!await ReadyForSendAsync(ct)) continue;

                int drops = Interlocked.Exchange(ref _dropCount, 0);
                if (drops > 0)
                {
                    _loggedDrops += drops;
                    Log.Write(_tag, $"fila de envio cheia: {drops} chunks descartados (total {_loggedDrops}).");
                }

                var msg = item is null
                    ? new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }
                    : new JsonObject
                    {
                        ["realtimeInput"] = new JsonObject
                        {
                            ["audio"] = new JsonObject
                            {
                                ["data"] = Convert.ToBase64String(item),
                                ["mimeType"] = $"audio/pcm;rate={_inputRate}"
                            }
                        }
                    };

                long t0 = Environment.TickCount64;
                try { await SendAsync(msg.ToJsonString(), ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Write(_tag, "erro ao enviar: " + ex.Message); }
                ReportBackpressure(Environment.TickCount64 - t0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write(_tag, $"fila de envio morreu: {ex}"); }
    }

    /// <summary>
    /// Espera a sessão poder receber áudio. Retorna false quando este chunk deve ser DESCARTADO.
    /// Durante uma reconexão planejada (goAway) segura o chunk na mão — a sessão volta em ~1 s
    /// com o estado preservado, e descartar aqui era perda de fala pura. Fora dessa janela mantém
    /// o descarte de antes: numa queda de duração desconhecida, mandar o áudio velho depois só
    /// empurraria a conversa para trás (o servidor devolve tudo em 1× e nada é recuperado).
    /// </summary>
    private async Task<bool> ReadyForSendAsync(CancellationToken ct)
    {
        while (!_ready || _ws is not { State: WebSocketState.Open })
        {
            if (ct.IsCancellationRequested || _closing) return false;
            if (Environment.TickCount64 >= _holdAudioUntil) return false;
            try { await Task.Delay(25, ct); } catch { return false; }
        }
        return true;
    }

    private long _lastBacklogLog;

    /// <summary>
    /// Um chunk de <see cref="ChunkMs"/> ms tem esse mesmo tempo para sair. Se o SendAsync demora
    /// mais que isso de forma sustentada, o uplink não acompanha e a fila vira atraso permanente —
    /// o servidor devolve a tradução em 1× tempo real, então nada do que se atrasou aqui é
    /// recuperado depois. Amostrado no máximo a cada 5 s para não poluir o log.
    /// </summary>
    private void ReportBackpressure(long sendMs)
    {
        int backlog = _outbox.Reader.Count;
        if (backlog <= 2 && sendMs < 100) return;

        long now = Environment.TickCount64;
        if (now - _lastBacklogLog < 5000) return;
        _lastBacklogLog = now;
        Log.Write(_tag, $"uplink lento: envio levou {sendMs} ms, {backlog} chunks na fila " +
                        $"(~{backlog * ChunkMs} ms de atraso acumulado).");
    }

    /// <summary>
    /// Fecha a conexão atual de forma limpa. O goAway é exatamente um pedido para o cliente
    /// fechar; não fazer isso era o que rendia o PolicyViolation "failed to close the connection
    /// after receiving a GoAway signal" nos logs.
    /// </summary>
    private async Task CloseCurrentAsync()
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            using var timeout = new CancellationTokenSource(2000);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "goAway", timeout.Token);
        }
        catch { /* já estava caindo; reabrir é o que importa */ }
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
                    if (_reconnectRequested) goto reconnect;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write(_tag, "erro de recepção: " + ex.Message); }

        reconnect:
            if (ct.IsCancellationRequested || _closing) return;
            _ready = false;

            // Planejada = pedida pelo servidor (goAway), com a conexão ainda viva. Não há erro do
            // qual recuar, e cada ms aqui é fala que o ouvinte perde, então reabre na hora. Só a
            // queda inesperada mantém o recuo de 1 s.
            bool planned = _reconnectRequested;
            _reconnectRequested = false;
            Status?.Invoke($"{_tag}: reconectando…");

            if (planned) await CloseCurrentAsync();
            else { try { await Task.Delay(1000, ct); } catch { return; } }

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

        // Handle de retomada: guardar é tudo que precisa acontecer aqui. É o que permite a próxima
        // reabertura continuar a mesma sessão em vez de começar fria.
        if (root["sessionResumptionUpdate"] is JsonNode sru)
        {
            if (sru["resumable"]?.GetValue<bool>() == true)
            {
                var handle = sru["newHandle"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(handle)) _resumeHandle = handle;
            }
            return;
        }

        // O servidor avisa com antecedência (timeLeft, tipicamente "50s") que vai fechar. Ignorar
        // este aviso era o que fazia a conexão morrer com PolicyViolation e voltar fria; reconectar
        // agora, de propósito e com handle, troca essa queda por uma emenda de ~1 s.
        if (root["goAway"] is JsonNode ga)
        {
            var left = ga["timeLeft"]?.GetValue<string>() ?? "?";
            Log.Write(_tag, $"goAway ({left} restantes) — reconectando com handle antes de o servidor derrubar.");
            _holdAudioUntil = Environment.TickCount64 + 10_000;
            _reconnectRequested = true;
            return;
        }

        if (root["error"] is JsonNode err)
        {
            var msg = err["message"]?.GetValue<string>() ?? err.ToJsonString();
            Log.Write(_tag, "erro do servidor: " + msg);

            // Recusa dos campos de sessão: desliga o recurso e volta ao comportamento antigo, em
            // vez de deixar o app sem conexão nenhuma (ver _sessionFeatures).
            if (_sessionFeatures && msg.Contains("Unknown name")
                && (msg.Contains("sessionResumption") || msg.Contains("contextWindowCompression")))
            {
                _sessionFeatures = false;
                _resumeHandle = null;
                Log.Write(_tag, "servidor recusou os campos de sessão — reabrindo sem eles.");
                _reconnectRequested = true;
                return;
            }

            // Handle inválido/expirado (valem 2 h): esquece o handle e recomeça limpo.
            if (_resumeHandle is not null)
            {
                _resumeHandle = null;
                Log.Write(_tag, "erro com handle de retomada — reabrindo como sessão nova.");
                _reconnectRequested = true;
                return;
            }

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
        try { _outbox.Writer.TryComplete(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
