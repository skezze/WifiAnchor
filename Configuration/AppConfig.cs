using System.Text.Json;
using System.Text;
using WifiAnchor.Infrastructure;

namespace WifiAnchor.Configuration;

internal sealed class AppConfig
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WifiAnchor");

    public static string FilePath => Path.Combine(Directory, "wifi-config.json");

    // Старое расположение (рядом с exe) — только для переноса настроек.
    private static string LegacyPath => Path.Combine(
        AppContext.BaseDirectory, "wifi-config.json");

    /// <summary>Имя сети (SSID), в которой нужно держаться за одну точку.</summary>
    public string Ssid { get; set; } = "";

    /// <summary>MAC-адрес (BSSID) выбранной точки / репитера.</summary>
    public string Bssid { get; set; } = "";

    /// <summary>
    /// Необязательно: имя профиля Wi-Fi в Windows, если оно не совпадает с SSID.
    /// </summary>
    public string Profile { get; set; } = "";

    /// <summary>Пользовательские названия точек: BSSID → название.</summary>
    public Dictionary<string, string> Names { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Закрытие окна оставляет программу работать в трее.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Показывать уведомления о потере и возврате точки.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Подсказка «программа осталась в трее» уже показывалась.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Код языка интерфейса (en, ru, de...). Пусто — язык системы.</summary>
    public string Language { get; set; } = "";

    /// <summary>Проверять и скачивать обновления в фоне.</summary>
    public bool AutoUpdate { get; set; } = true;

    public bool HasTarget =>
        Ssid.Length > 0 && Mac.IsValid(Bssid);

    public string NameOf(string bssid) =>
        Names.TryGetValue(Mac.Normalize(bssid), out string? name) ? name : "";

    public void SetName(string bssid, string name)
    {
        string key = Mac.Normalize(bssid);
        if (key.Length == 0)
            return;

        if (string.IsNullOrWhiteSpace(name))
            Names.Remove(key);
        else
            Names[key] = name.Trim();
    }

    public static AppConfig Load(out string? warning)
    {
        warning = null;

        string path = File.Exists(FilePath) ? FilePath
            : File.Exists(LegacyPath) ? LegacyPath
            : "";

        if (path.Length == 0)
            return new AppConfig();

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json);

            if (config != null)
            {
                config.Ssid ??= "";
                config.Profile ??= "";
                config.Language ??= "";
                config.Bssid = Mac.Normalize(config.Bssid);
                config.Names = new Dictionary<string, string>(
                    config.Names ?? new(), StringComparer.OrdinalIgnoreCase);
                return config;
            }
        }
        catch (Exception ex)
        {
            warning = $"Could not read the config ({ex.Message}). " +
                      "The access point has to be selected again.";
        }

        return new AppConfig();
    }

    public bool TrySave(out string? error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    this, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
