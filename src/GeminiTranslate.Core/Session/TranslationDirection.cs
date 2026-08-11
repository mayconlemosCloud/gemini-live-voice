using GeminiTranslate.Core.Contracts;
using GeminiTranslate.Core.Diagnostics;
using GeminiTranslate.Core.Signal;

namespace GeminiTranslate.Core.Session;

/// <summary>Parâmetros de uma direção de tradução.</summary>
/// <param name="Name">Rótulo exibido no log e nos arquivos ("Entrada" ou "Saída").</param>
/// <param name="ApiKey">Chave do Google AI Studio.</param>
/// <param name="Model">Modelo de tradução ao vivo.</param>
/// <param name="TargetLang">Código do idioma em que esta direção deve sair.</param>
/// <param name="OriginalVolume">Volume da voz original tocada por baixo, de 0 a 1.</param>
/// <param name="CatchUp">Se a tradução pode acelerar para recuperar fila.</param>
public sealed record DirectionOptions(
    string Name,
    string ApiKey,
    string Model,
    string TargetLang,
    float OriginalVolume,
    bool CatchUp);

/// <summary>
/// Um fluxo de tradução completo: captura, modelo, reprodução — com a voz original misturada
/// baixa por baixo da tradução.
/// </summary>
/// <remarks>
/// Recebe todas as peças prontas, sem construir nenhuma: é o que permite exercitar a direção
/// inteira, incluindo medição de atraso e saldo de fala, com dublês em memória e sem placa de
/// som nem rede.
/// </remarks>
public sealed class TranslationDirection : IDisposable
{
    /// <summary>
    /// Faixa plausível para a razão entre tradução e fala. Nenhum par de idiomas dobra nem corta
    /// pela metade a duração da fala: fora desta faixa o que está errado é a CONTAGEM.
    /// </summary>
    private const double MinPlausibleRatio = 0.55;

    /// <summary>Ver <see cref="MinPlausibleRatio"/>.</summary>
    private const double MaxPlausibleRatio = 1.8;

    /// <summary>Fala mínima antes de valer a pena questionar a razão medida.</summary>
    private const double RatioCheckMinSpokenMs = 3000;

    private readonly IAudioSource _input;
    private readonly IAudioSink _output;
    private readonly ITranslationStream _stream;
    private readonly IResampler _resampler;
    private readonly IDiagnosticRecorder _diagnostics;
    private readonly InputGain _gain;
    private readonly LatencyProbe _probe;
    private readonly SpeechBalance _balance = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Rótulo desta direção.</summary>
    public string Name { get; }

    /// <summary>Transcrição do que foi dito no idioma original.</summary>
    public event Action<string>? OriginalText;

    /// <summary>Transcrição da tradução.</summary>
    public event Action<string>? TranslatedText;

    /// <summary>Nível de entrada, para o medidor da interface.</summary>
    public event Action<float>? Level;

    /// <summary>Mudança de estado legível para a interface.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Se a entrada está muda. Mutar é o único sinal local de "stream pausado" que se manda ao
    /// servidor — nunca um timeout de silêncio adivinhado.
    /// </summary>
    public bool Muted
    {
        get => _input.Muted;
        set
        {
            if (value && !_input.Muted) _stream.EnqueueAudioStreamEnd();
            _input.Muted = value;
        }
    }

    /// <summary>Volume da voz original tocada por baixo da tradução.</summary>
    public float OriginalVolume
    {
        set => _output.OriginalVolume = value;
    }

    /// <summary>Formato do mix renderizado por esta direção, para a gravação da conversa.</summary>
    public AudioFormat OutputMixFormat => _output.MixFormat;

    /// <summary>Tradução enfileirada para tocar — o atraso ao vivo que o ouvinte sente.</summary>
    public TimeSpan TranslationQueue => _output.TranslationQueue;

    /// <summary>
    /// Atraso REAL medido da fala até o fone, e a idade da medição.
    /// </summary>
    /// <remarks>
    /// É isto que o indicador da interface mostra. A <see cref="TranslationQueue"/> sozinha nunca
    /// serviu para isso, porque o dub chega em 1× e a fila fica sempre rasa.
    /// </remarks>
    public (double LagMs, double AgeMs) Lag => _probe.LastLag();

    /// <summary>Velocidade de reprodução agora. Acima de 1 significa recuperando fila.</summary>
    public double CatchUpSpeed => _output.CatchUpSpeed;

    /// <summary>
    /// Derivação do mix renderizado por esta direção — exatamente o que seu ouvinte escuta.
    /// </summary>
    public Action<float[], int, int>? OutputTap
    {
        set => _output.RenderTap = value;
    }

    /// <summary>
    /// Estado da fala atual para a barra de saldo: quanto se falou, quanto já saiu, e a DISTÂNCIA
    /// entre as duas cabeças.
    /// </summary>
    /// <remarks>
    /// A distância vem do <see cref="LatencyProbe"/>, e não de subtrair os contadores: aquele
    /// estimador correlaciona envelopes e é invariante a nível, então não se importa se um lado
    /// chega mais alto que o outro. Foi exatamente essa assimetria que travou a versão anterior
    /// em "tradução em dia".
    /// </remarks>
    public BalanceSnapshot Balance
    {
        get
        {
            var snapshot = _balance.Read(_output.TranslationQueue.TotalMilliseconds);
            if (!snapshot.Active) return snapshot with { GapMs = 0 };

            var (lagMs, _) = _probe.LastLag();
            if (double.IsNaN(lagMs)) return snapshot;

            return snapshot with { GapMs = Math.Max(0, lagMs - snapshot.SinceSpeechMs) };
        }
    }

    /// <param name="options">Configuração desta direção.</param>
    /// <param name="input">Captura de onde vem a fala.</param>
    /// <param name="output">Saída onde a tradução será tocada.</param>
    /// <param name="stream">Sessão de tradução ao vivo.</param>
    /// <param name="resampler">Conversor para a taxa de entrada da rede.</param>
    /// <param name="diagnostics">Gravação de diagnóstico dos dois sentidos.</param>
    public TranslationDirection(
        DirectionOptions options,
        IAudioSource input,
        IAudioSink output,
        ITranslationStream stream,
        IResampler resampler,
        IDiagnosticRecorder diagnostics)
    {
        Name = options.Name;
        _input = input;
        _output = output;
        _stream = stream;
        _resampler = resampler;
        _diagnostics = diagnostics;
        _gain = new InputGain(options.Name);
        _probe = new LatencyProbe(options.Name);

        _input.ChunkAvailable += OnCaptured;
        _input.Level += level => Level?.Invoke(level);
        _stream.AudioReceived += OnTranslationReceived;
        _stream.InputText += text => OriginalText?.Invoke(text);
        _stream.OutputText += text => TranslatedText?.Invoke(text);
        _stream.Status += status => Status?.Invoke(status);
    }

    /// <summary>
    /// Trata um chunk capturado: toca o original, e manda a versão de rede ao modelo.
    /// </summary>
    /// <remarks>
    /// O stream vai INTEIRO para a rede — silêncio, pausas e respiração incluídos. Já existiu
    /// aqui um cortador de pausa para economizar atraso, mas ritmo é uma das três coisas que este
    /// modelo reproduz, junto de entonação e tom: cortar pausa do que ele ouve é apagar prosódia
    /// na origem. Pior, o limiar daquele cortador caía no p10 medido da fala real neste setup — o
    /// fim de uma vogal sustentada, que decai de volume, corria risco de ser tratado como silêncio.
    /// </remarks>
    private void OnCaptured(byte[] chunk)
    {
        _output.EnqueueOriginal(chunk);

        var wire = _resampler.Feed(chunk);
        if (wire is null) return;

        _probe.Spoke(wire);
        _gain.Apply(wire);
        MeasureBalanceAfterGain(wire);
        _diagnostics.WriteSent(wire);
        _stream.EnqueueAudio(wire);
    }

    /// <summary>
    /// Contabiliza a fala DEPOIS do ganho, de propósito.
    /// </summary>
    /// <remarks>
    /// O limiar de fala do <see cref="SpeechBalance"/> é absoluto, e o microfone chega com
    /// envelope de cerca de 0,11 FS enquanto o dub volta bem mais alto. Medindo antes do ganho,
    /// metade da fala não era contada e a sessão fechava com "10,2 s de fala / 27,0 s de
    /// tradução" — razão 2,65, impossível para um par de idiomas. O ganho normaliza os dois lados
    /// para perto do mesmo alvo, que é o que torna a contagem comparável.
    /// </remarks>
    private void MeasureBalanceAfterGain(byte[] wire) => _balance.Spoke(wire);

    private void OnTranslationReceived(byte[] pcm)
    {
        _probe.Heard(pcm, _stream.OutboxBacklog, _output.TranslationQueue.TotalMilliseconds);
        _balance.Heard(pcm);
        _diagnostics.WriteReceived(pcm);
        _output.EnqueueTranslation(pcm);
    }

    /// <summary>Abre a saída, conecta ao modelo e começa a capturar.</summary>
    public async Task StartAsync()
    {
        _output.Start();
        await _stream.ConnectAsync(_cts.Token);
        await _input.StartAsync();
        Log.Write(Name, "direção iniciada.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _input.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _output.Dispose(); } catch { }
        try { _diagnostics.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }

        LogSessionTotals();
    }

    private void LogSessionTotals()
    {
        var (spokenMs, dubbedMs) = _balance.Totals();
        double ratio = spokenMs > 0 ? dubbedMs / spokenMs : 1;

        Log.Write(Name, $"direção parada. Sessão: {spokenMs / 1000:0.0} s de fala entraram, " +
                        $"{dubbedMs / 1000:0.0} s de tradução saíram (razão {ratio:0.00}).");

        if (spokenMs > RatioCheckMinSpokenMs && (ratio > MaxPlausibleRatio || ratio < MinPlausibleRatio))
            Log.Write(Name, $"AVISO: razão fala/tradução em {ratio:0.00} — implausível para um par " +
                            "de idiomas. Suspeite do limiar de fala do SpeechBalance ou de os dois " +
                            "lados estarem sendo medidos em níveis diferentes.");
    }
}
