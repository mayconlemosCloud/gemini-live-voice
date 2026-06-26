namespace GeminiLiveTranslate.Config;

public sealed record Language(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A practical subset of the 70+ languages supported by the translate model.</summary>
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
        new Language("nl", "Holandês (Nederlands)"),
        new Language("pl", "Polonês (Polski)"),
        new Language("tr", "Turco (Türkçe)"),
    };

    public static Language ByCode(string code) =>
        All.FirstOrDefault(l => l.Code == code) ?? All[0];
}
