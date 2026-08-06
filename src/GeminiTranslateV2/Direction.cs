using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiTranslateV2;

/// <summary>
/// One translation flow: capture → Gemini → playback, with the original voice mixed low
/// under the translation. "Entrada" uses ProcessCapture or LoopbackCapture (the call app's
/// audio). "Saída" uses MicCapture (your real mic, with Windows' own noise suppression enabled).
/// </summary>
public sealed class Direction : IDisposable
{
    private readonly IAudioSource _in;
    private readonly AudioOut _out;
    private readonly LiveClient _client;
    private readonly Resample16k _wire;
    private readonly InputGain _gain;
    private readonly LatencyProbe _probe;
    private readonly SpeechBalance _balance = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private readonly WaveFileWriter? _sentWav;
    private readonly WaveFileWriter? _recvWav;
    private readonly object _wavLock = new();

    public string Name { get; }

    public event Action<string>? OriginalText;
    public event Action<string>? TranslatedText;
    public event Action<float>? Level;
    public event Action<string>? Status;

    /// <summary>Muting is the only local "stream paused" signal we send — never a guessed silence timeout.</summary>
    public bool Muted
    {
        get => _in.Muted;
        set
        {
            if (value && !_in.Muted) _client.EnqueueAudioStreamEnd();
            _in.Muted = value;
        }
    }

    public float OriginalVolume { set => _out.OriginalVolume = value; }

    /// <summary>Format of this direction's rendered mix, for ConversationRecorder.</summary>
    public WaveFormat OutputMixFormat => _out.MixFormat;

    /// <summary>Translated audio queued for playback — the live delay the listener hears.</summary>
    public TimeSpan TranslationQueue => _out.TranslationQueue;

    /// <summary>
    /// Atraso REAL medido da fala até o fone, e a idade da medição. É isto que o indicador da UI
    /// mostra — <see cref="TranslationQueue"/> sozinha nunca serviu para isso, porque o dub chega
    /// em 1× e a fila fica sempre rasa (ver LatencyProbe).
    /// </summary>
    public (double LagMs, double AgeMs) Lag => _probe.LastLag();

    /// <summary>Velocidade de reprodução agora (1,0 = normal); &gt; 1 significa recuperando fila.</summary>
    public double CatchUpSpeed => _out.CatchUpSpeed;

    /// <summary>
    /// Estado da fala atual para a barra: quanto se falou, quanto já saiu, e a DISTÂNCIA entre as
    /// duas cabeças. A fila de reprodução entra aqui porque o que está nela chegou mas ainda não
    /// saiu no alto-falante.
    ///
    /// A distância vem do <see cref="LatencyProbe"/>, não de subtrair os contadores: aquele
    /// estimador correlaciona envelopes e é invariante a nível, então não se importa se um lado
    /// chega mais alto que o outro. Foi exatamente essa assimetria que travou a versão anterior
    /// em "tradução em dia".
    /// </summary>
    public BalanceSnapshot Balance
    {
        get
        {
            var b = _balance.Read(_out.TranslationQueue.TotalMilliseconds);
            if (!b.Active) return b with { GapMs = 0 };

            var (lagMs, _) = _probe.LastLag();
            if (double.IsNaN(lagMs)) return b; // GapMs continua NaN: ainda medindo

            // Falando, a distância é o atraso medido. Depois que a fala para, ela decai sozinha
            // conforme o que ficou pendente vai saindo — senão a barra congelaria no último valor.
            return b with { GapMs = Math.Max(0, lagMs - b.SinceSpeechMs) };
        }
    }

    /// <summary>Tap on this direction's rendered mix (exactly what its listener hears).</summary>
    public Action<float[], int, int>? OutputTap { set => _out.RenderTap = value; }

    public Direction(string name, IAudioSource inputSource, MMDevice outputDevice,
        string apiKey, string model, string targetLang, float originalVolume, bool catchUp)
    {
        Name = name;
        _in = inputSource;
        _out = new AudioOut(outputDevice, _in.SampleRate, originalVolume, catchUp, name);
        // A rede recebe 16 kHz (taxa de entrada da Live API); a voz original continua nativa.
        _wire = new Resample16k(_in.SampleRate);
        _client = new LiveClient(apiKey, model, targetLang, Resample16k.Rate, name);
        _gain = new InputGain(name);
        _probe = new LatencyProbe(name);

        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _sentWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-enviado-{stamp}.wav"),
                new WaveFormat(Resample16k.Rate, 16, 1));
            _recvWav = new WaveFileWriter(Path.Combine(Log.Folder, $"{name}-recebido-{stamp}.wav"),
                new WaveFormat(24000, 16, 1));
        }
        catch (Exception ex) { Log.Write(name, "sem gravação de diagnóstico: " + ex.Message); }

        _in.ChunkAvailable += chunk =>
        {
            _out.EnqueueOriginal(chunk); // the original voice always flows
            var wire = _wire.Feed(chunk);
            if (wire is null) return;
            _probe.Spoke(wire);

            // O stream vai INTEIRO para a rede — silêncio, pausas e respiração incluídos. O
            // PauseTrimmer que existia aqui cortava o "dead air" para economizar atraso, mas ritmo
            // é uma das três coisas que este modelo reproduz (entonação, ritmo, tom): cortar pausa
            // do que ele ouve é apagar prosódia na origem. Pior, o limiar dele (RMS 0,01) caía no
            // p10 medido da fala real neste setup — o fim de uma vogal sustentada tipo "testeee",
            // que decai de volume, era exatamente o que corria risco de ser tratado como silêncio.
            _gain.Apply(wire);
            // DEPOIS do ganho, de propósito. O limiar de fala do SpeechBalance é absoluto, e o mic
            // chega com envelope de ~0,11 FS enquanto o dub do Gemini volta bem mais alto: medindo
            // antes do ganho, metade da fala não era contada e a sessão fechava com "10,2 s de fala
            // / 27,0 s de tradução" (razão 2,65 — impossível para um par de idiomas). O ganho
            // normaliza os dois lados para perto do mesmo alvo, que é o que torna a contagem
            // comparável.
            _balance.Spoke(wire);
            lock (_wavLock) { try { _sentWav?.Write(wire, 0, wire.Length); } catch { } }
            _client.EnqueueAudio(wire); // ordem preservada — o servidor é dono da segmentação
        };
        _in.Level += l => Level?.Invoke(l);
        _client.AudioReceived += pcm =>
        {
            _probe.Heard(pcm, _client.OutboxBacklog, _out.TranslationQueue.TotalMilliseconds);
            _balance.Heard(pcm);
            lock (_wavLock) { try { _recvWav?.Write(pcm, 0, pcm.Length); } catch { } }
            _out.EnqueueTranslation(pcm);
        };
        _client.InputText += t => OriginalText?.Invoke(t);
        _client.OutputText += t => TranslatedText?.Invoke(t);
        _client.Status += s => Status?.Invoke(s);
    }

    public async Task StartAsync()
    {
        _out.Start();
        await _client.ConnectAsync(_cts.Token);
        await _in.StartAsync();
        Log.Write(Name, "direção iniciada.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _in.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _out.Dispose(); } catch { }
        lock (_wavLock)
        {
            try { _sentWav?.Dispose(); } catch { }
            try { _recvWav?.Dispose(); } catch { }
        }
        try { _cts.Dispose(); } catch { }
        var (spoken, dubbed) = _balance.Totals();
        double ratio = spoken > 0 ? dubbed / spoken : 1;
        Log.Write(Name, $"direção parada. Sessão: {spoken / 1000:0.0} s de fala entraram, " +
                        $"{dubbed / 1000:0.0} s de tradução saíram (razão {ratio:0.00}).");
        // Nenhum par de idiomas dobra nem corta pela metade a duração da fala. Fora desta faixa o
        // que está errado é a CONTAGEM — tipicamente os dois lados medidos em níveis diferentes,
        // que foi o que já produziu uma razão de 2,65 e travou a barra em "em dia".
        if (spoken > 3000 && (ratio > 1.8 || ratio < 0.55))
            Log.Write(Name, $"AVISO: razão fala/tradução em {ratio:0.00} — implausível para um par " +
                            $"de idiomas. Suspeite do limiar de fala do SpeechBalance ou de os dois " +
                            $"lados estarem sendo medidos em níveis diferentes.");
    }
}
