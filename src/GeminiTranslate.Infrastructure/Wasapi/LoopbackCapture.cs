using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GeminiTranslate.Infrastructure.Wasapi;

/// <summary>
/// Captura um endpoint de reprodução por WASAPI loopback — a alternativa por cabo virtual ao
/// <see cref="ProcessCapture"/>.
/// </summary>
/// <remarks>
/// Aponte a saída do app de chamada para um cabo dedicado e selecione esse cabo aqui; a reunião
/// então chega aos seus ouvidos só pelo mix deste app (tradução com o original por baixo), nunca
/// dobrada.
///
/// A diferença para o <see cref="ProcessCapture"/> é de LIMPEZA, não de fidelidade: os dois são
/// derivações digitais exatas depois do mixer. O loopback ouve tudo que for roteado ao
/// dispositivo, então é tão limpo quanto o roteamento — num cabo dedicado há silêncio digital
/// verdadeiro entre as frases; nos alto-falantes do dia a dia, cada notificação também vai para
/// o modelo.
/// </remarks>
public sealed class LoopbackCapture : WasapiAudioSource
{
    /// <param name="device">Endpoint de reprodução a ser escutado.</param>
    public LoopbackCapture(MMDevice device)
        : base(new WasapiLoopbackCapture(device), "Loopback", device.FriendlyName)
    {
    }

    /// <summary>
    /// Faz a média dos canais: o loopback carrega um mix estéreo já produzido, em que os canais
    /// são coerentes em fase — ao contrário de um arranjo de microfones (ver <see cref="MicCapture"/>).
    /// </summary>
    protected override ISampleProvider ToMono(ISampleProvider source) => new DownmixToMonoProvider(source);
}
