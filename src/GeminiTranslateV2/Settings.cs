using System.IO;
using System.Text.Json;

namespace GeminiTranslateV2;

public sealed record Language(string Code, string Name)
{
    public override string ToString() => Name;
}

public static class Languages
{
    public static readonly IReadOnlyList<Language> All = new[]
    {
        new Language("pt", "Português"),
        new Language("en", "Inglês (English)"),
        new Language("es", "Espanhol (Español)"),
        new Language("fr", "Francês (Français)"),
        new Language("de", "Alemão (Deutsch)"),
        new Language("it", "Italiano"),
        new Language("ja", "Japonês (日本語)"),
        new Language("ko", "Coreano (한국어)"),
        new Language("zh-Hans", "Chinês simplificado (中文)"),
        new Language("ru", "Russo (Русский)"),
        new Language("hi", "Hindi (हिन्दी)"),
        new Language("ar", "Árabe (العربية)"),
    };

    public static Language ByCode(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
}

/// <summary>Persisted in %AppData%\GeminiTranslateV2\settings.json.</summary>
public sealed class Settings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.5-live-translate-preview";

    /// <summary>Process name (e.g. "Teams", "chrome", "WhatsApp") whose audio we listen to — Process Loopback, not a device.</summary>
    public string? EntradaProcessName { get; set; }

    /// <summary>Render endpoint captured via WASAPI loopback (the Lite/cable approach). When set, wins over EntradaProcessName.</summary>
    public string? EntradaDeviceId { get; set; }
    public string? HeadphonesDeviceId { get; set; }  // where I hear the incoming translation
    public string? MicDeviceId { get; set; }         // my real microphone
    public string? VirtualMicDeviceId { get; set; }  // render side of the cable the call app uses as mic

    public string MyLang { get; set; } = "pt";
    public string TheirLang { get; set; } = "en";

    public double OriginalVolume { get; set; } = 0.20;

    /// <summary>
    /// Recuperar atraso acelerando a tradução até 1,12× quando ela se acumula na fila, sem alterar
    /// o pitch (WSOLA — ver TimeStretch). Ganho limitado por natureza: só alcança o que está na
    /// fila de reprodução (90–330 ms medidos), nunca o tempo que o modelo leva para responder.
    /// </summary>
    public bool CatchUpEnabled { get; set; } = true;

    /// <summary>Onde o usuário largou a etiqueta flutuante de atraso. null = canto inferior direito.</summary>
    public double? LagLeft { get; set; }
    public double? LagTop { get; set; }

    /// <summary>
    /// Ao iniciar, torna os cabos virtuais os dispositivos padrão do Windows (e restaura ao parar),
    /// para não precisar configurar entrada/saída dentro do Teams, WhatsApp, Meet...
    /// </summary>
    public bool MakeCablesDefault { get; set; } = true;

    /// <summary>
    /// Oculta as janelas do app de compartilhamento de tela, gravação e print (SetWindowDisplayAffinity).
    /// Você continua vendo tudo; quem está do outro lado da chamada, não.
    /// </summary>
    public bool HideFromScreenShare { get; set; } = true;

    // ---- Assistente de respostas (usa a mesma API do Google/Gemini, camada gratuita) ----
    /// <summary>
    /// Modelo Gemini (generateContent) do assistente. NÃO afeta a tradução ao vivo, que roda no
    /// gemini-3.5-live-translate-preview e não tem limite de requisições.
    ///
    /// Flash-Lite e não Flash por causa da cota gratuita, que é por REQUISIÇÃO e não por token:
    /// o 2.5-flash dá 20 pedidos por DIA (5/min), e o 3.5-flash-lite dá 500 por dia (15/min) —
    /// 25× mais, com os mesmos 250K tokens/min. Conferido em aistudio.google.com/rate-limit em
    /// 11/08/2026; um print e uma pergunta de texto custam exatamente 1 requisição cada.
    /// </summary>
    public string AssistantModel { get; set; } = DefaultAssistantModel;

    public const string DefaultAssistantModel = "gemini-3.5-flash-lite";

    /// <summary>
    /// Modelos que já foram padrão e que ninguém escolheu de propósito. Quem tem um destes salvo
    /// no settings.json está batendo no teto de 20 pedidos/dia sem saber, então a atualização leva
    /// junto — um valor diferente destes é escolha do usuário e fica como está.
    /// </summary>
    private static readonly string[] SupersededAssistantModels = ["gemini-2.5-flash"];
    /// <summary>Quando ligado, perguntas da outra pessoa ficam sublinhadas e clicáveis para sugerir uma resposta.</summary>
    public bool AssistantEnabled { get; set; }
    /// <summary>Contexto opcional sobre você (cargo, tema da reunião...) para deixar as sugestões mais relevantes.</summary>
    public string AssistantContext { get; set; } = "";

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GeminiTranslateV2");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (SupersededAssistantModels.Contains(loaded.AssistantModel))
                {
                    Log.Write("Assistente",
                        $"modelo '{loaded.AssistantModel}' substituído por '{DefaultAssistantModel}' " +
                        "(cota gratuita 25× maior).");
                    loaded.AssistantModel = DefaultAssistantModel;
                    loaded.Save();
                }
                return loaded;
            }
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
