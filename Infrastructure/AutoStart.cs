using Microsoft.Win32;

namespace WifiAnchor.Infrastructure;

/// <summary>Автозапуск через HKCU\...\Run (права администратора не нужны).</summary>
internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WifiAnchor";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);

        if (enabled)
            key.SetValue(ValueName, BuildCommand());
        else
            key.DeleteValue(ValueName, false);
    }

    /// <summary>Если exe переместили, обновляет путь в автозапуске.</summary>
    public static void Refresh()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, true);

            if (key?.GetValue(ValueName) is string current &&
                current != BuildCommand())
            {
                key.SetValue(ValueName, BuildCommand());
            }
        }
        catch
        {
            // Не критично.
        }
    }

    private static string BuildCommand()
    {
        string exe = Environment.ProcessPath ?? "";

        // Запуск через «dotnet WifiAnchor.dll».
        if (Path.GetFileNameWithoutExtension(exe)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Path.Combine(
                AppContext.BaseDirectory, "WifiAnchor.dll");
            return $"\"{exe}\" \"{dll}\" --minimized";
        }

        return $"\"{exe}\" --minimized";
    }
}
