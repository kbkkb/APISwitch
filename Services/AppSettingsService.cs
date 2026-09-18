using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace APISwitch.Services;

public class AppSettings
{
    public bool AutoCheckUpdate { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
}

public static class AppSettingsService
{
    private static readonly string SettingsFile = Path.Combine(AgPaths.AppData, "APISwitch", "settings.json");
    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var text = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(text);
                if (settings != null) return settings;
            }
        }
        catch { }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("APISwitch") != null;
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue("APISwitch", $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue("APISwitch", false);
            }
        }
        catch { }
    }
}
