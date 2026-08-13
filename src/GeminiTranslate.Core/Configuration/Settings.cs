namespace GeminiTranslate.Core.Configuration;

/// <summary>
/// Preferências do usuário, serializadas como JSON. É só dado: quem lê e grava é o
/// <c>SettingsStore</c> da infraestrutura.
/// </summary>
public sealed class Settings
{
    /// <summary>Chave do Google AI Studio, usada tanto pela tradução ao vivo quanto pelo assistente.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Modelo da tradução ao vivo. Não tem limite de requisições.</summary>
    public string Model { get; set; } = "gemini-3.5-live-translate-preview";

    /// <summary>
    /// Nome do processo (ex.: "Teams", "chrome", "WhatsApp") cujo áudio é escutado via Process
    /// Loopback. Perde para <see cref="EntradaDeviceId"/> quando os dois estão preenchidos.
    /// </summary>
    public string? EntradaProcessName { get; set; }

    /// <summary>
    /// Endpoint de reprodução capturado por WASAPI loopback (a abordagem de cabo virtual).
    /// Quando preenchido, vence <see cref="EntradaProcessName"/>.
    /// </summary>
    public string? EntradaDeviceId { get; set; }

    /// <summary>Fone onde o usuário ouve a tradução do que a outra pessoa diz.</summary>
    public string? HeadphonesDeviceId { get; set; }

    /// <summary>Microfone real do usuário.</summary>
    public string? MicDeviceId { get; set; }

    /// <summary>Lado de reprodução do cabo que o app de chamada usa como microfone.</summary>
    public string? VirtualMicDeviceId { get; set; }

    /// <summary>Idioma em que o usuário ouve a outra pessoa.</summary>
    public string MyLang { get; set; } = "pt";

    /// <summary>Idioma em que a outra pessoa ouve o usuário.</summary>
    public string TheirLang { get; set; } = "en";

    /// <summary>Volume da voz original tocada por baixo da tradução, de 0 a 1.</summary>
    public double OriginalVolume { get; set; } = 0.20;

    /// <summary>
    /// Recuperar atraso acelerando a tradução até 1,12× quando ela se acumula na fila, sem alterar
    /// o pitch (WSOLA — ver <c>Wsola</c>).
    /// </summary>
    /// <remarks>
    /// O ganho é limitado por natureza: só alcança o que está na fila de reprodução (90–330 ms
    /// medidos), nunca o tempo que o modelo leva para responder.
    /// </remarks>
    public bool CatchUpEnabled { get; set; } = true;

    /// <summary>Posição onde o usuário largou a etiqueta flutuante. Null = canto inferior direito.</summary>
    public double? LagLeft { get; set; }

    /// <summary>Ver <see cref="LagLeft"/>.</summary>
    public double? LagTop { get; set; }

    /// <summary>
    /// Ao iniciar, torna os cabos virtuais os dispositivos padrão do Windows (e restaura ao parar),
    /// para não precisar configurar entrada/saída dentro do Teams, WhatsApp, Meet.
    /// </summary>
    public bool MakeCablesDefault { get; set; } = true;

    /// <summary>
    /// Oculta as janelas do app de compartilhamento de tela, gravação e print
    /// (SetWindowDisplayAffinity). O usuário continua vendo tudo; quem está do outro lado, não.
    /// </summary>
    public bool HideFromScreenShare { get; set; } = false;

    /// <summary>
    /// Modelo Gemini (generateContent) do assistente. Não afeta a tradução ao vivo.
    /// </summary>
    /// <remarks>
    /// Flash-Lite e não Flash por causa da cota gratuita, que é por REQUISIÇÃO e não por token:
    /// o 2.5-flash dá 20 pedidos por dia (5/min) e o 3.5-flash-lite dá 500 por dia (15/min) —
    /// 25× mais, com os mesmos 250K tokens/min. Conferido em aistudio.google.com/rate-limit em
    /// 11/08/2026; um print e uma pergunta de texto custam exatamente 1 requisição cada.
    /// </remarks>
    public string AssistantModel { get; set; } = DefaultAssistantModel;

    /// <summary>Modelo do assistente usado quando nada foi escolhido. Ver <see cref="AssistantModel"/>.</summary>
    public const string DefaultAssistantModel = "gemini-3.5-flash-lite";

    /// <summary>Quando ligado, perguntas da outra pessoa ficam sublinhadas e clicáveis.</summary>
    public bool AssistantEnabled { get; set; }

    /// <summary>Contexto opcional sobre o usuário (cargo, tema da reunião) para as sugestões.</summary>
    public string AssistantContext { get; set; } = "";
}
