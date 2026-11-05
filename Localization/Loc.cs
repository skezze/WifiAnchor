using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WifiAnchor.Localization;

internal sealed record LanguageInfo(string Code, string NativeName);

/// <summary>
/// Переводы интерфейса. Каждый язык — JSON-файл Localization\{код}.json,
/// вшитый в exe. Английский обязателен и служит запасным вариантом: если
/// ключа нет в выбранном языке, берётся английский текст.
/// Чтобы добавить язык, достаточно положить файл и добавить его в Languages.
/// </summary>
internal static class Loc
{
    public const string Auto = "";
    private const string Fallback = "en";

    public static readonly IReadOnlyList<LanguageInfo> Languages = new LanguageInfo[]
    {
        new("en", "English"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("pt", "Português"),
        new("pl", "Polski"),
        new("tr", "Türkçe"),
        new("zh", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어")
    };

    private static readonly Dictionary<string, string> English = Load(Fallback);
    private static volatile Dictionary<string, string> current = English;
    private static string currentCode = Fallback;

    /// <summary>Код языка, который действует сейчас (никогда не пустой).</summary>
    public static string CurrentCode => currentCode;

    /// <summary>Язык переключён. Вызывается из потока, сменившего язык.</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// Применяет настройку: пустая строка — язык системы, иначе код языка.
    /// </summary>
    public static void SetLanguage(string? setting)
    {
        string code = Resolve(setting);

        if (code == currentCode)
            return;

        currentCode = code;
        current = code == Fallback ? English : Load(code);
        LanguageChanged?.Invoke();
    }

    public static string T(string key)
    {
        if (current.TryGetValue(key, out string? text))
            return text;

        return English.TryGetValue(key, out text) ? text : key;
    }

    public static string T(string key, params object[] args)
    {
        string template = T(key);

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>Превращает настройку в код существующего языка.</summary>
    public static string Resolve(string? setting)
    {
        string wanted = string.IsNullOrWhiteSpace(setting)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : setting.Trim();

        // «pt-BR» → «pt».
        int dash = wanted.IndexOf('-');
        if (dash > 0)
            wanted = wanted[..dash];

        wanted = wanted.ToLowerInvariant();

        return Languages.Any(l => l.Code == wanted) ? wanted : Fallback;
    }

    private static Dictionary<string, string> Load(string code)
    {
        string name = $"WifiAnchor.Localization.{code}.json";

        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(name);

        if (stream == null)
            return new Dictionary<string, string>();

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? new Dictionary<string, string>();
    }
}
