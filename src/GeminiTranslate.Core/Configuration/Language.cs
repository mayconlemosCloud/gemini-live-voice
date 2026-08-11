namespace GeminiTranslate.Core.Configuration;

/// <summary>Idioma oferecido na interface: código BCP-47 mandado à API e rótulo exibido.</summary>
public sealed record Language(string Code, string Name)
{
    /// <summary>Exibe o rótulo — os combos da interface fazem bind direto no objeto.</summary>
    public override string ToString() => Name;
}

/// <summary>Catálogo de idiomas suportados pela tradução ao vivo.</summary>
public static class Languages
{
    /// <summary>Todos os idiomas, na ordem em que aparecem nos combos.</summary>
    public static readonly IReadOnlyList<Language> All =
    [
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
    ];

    /// <summary>Idioma pelo código, caindo no primeiro da lista quando o código é desconhecido.</summary>
    public static Language ByCode(string code) => All.FirstOrDefault(l => l.Code == code) ?? All[0];
}
