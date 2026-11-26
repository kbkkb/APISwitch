using System.Diagnostics;
using System.IO;

namespace APISwitch.Services;

public static class AgPaths
{
    private static string? _cachedIdeVersion;
    internal static bool IsSandboxed =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APISWITCH_APPDATA"));

    public static string AppData =>
        Environment.GetEnvironmentVariable("APISWITCH_APPDATA") is { Length: > 0 } a
            ? a
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<string> CandidateUserDirs => new[]
    {
        Path.Combine(AppData, "Antigravity IDE"),
        Path.Combine(AppData, "Antigravity"),
    };

    public static string? FindStateDb()
    {
        foreach (var dir in CandidateUserDirs)
        {
            var db = Path.Combine(dir, "User", "globalStorage", "state.vscdb");
            if (File.Exists(db)) return db;
        }
        return null;
    }

    public static IReadOnlyList<string> FindAllStateDbs() =>
        CandidateUserDirs
            .Select(dir => Path.Combine(dir, "User", "globalStorage", "state.vscdb"))
            .Where(File.Exists)
            .ToList();

    public static string HubAppStoragePath =>
        Path.Combine(AppData, "Antigravity", "app_storage.json");

    public static string? FindIdeExecutable()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Programs", "antigravity", "Antigravity.exe"),
            Path.Combine(local, "Programs", "Antigravity", "Antigravity.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>动态读取本机已安装的 Antigravity.exe 真实版本号（如 2.35.2），未安装时回退最新已知正式版。</summary>
    public static string GetInstalledIdeVersion()
    {
        if (!string.IsNullOrEmpty(_cachedIdeVersion))
            return _cachedIdeVersion;

        const string fallback = "2.35.2";
        try
        {
            var exe = FindIdeExecutable();
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var raw = FileVersionInfo.GetVersionInfo(exe).FileVersion?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var clean = raw.Split('+', '-', ' ')[0];
                    var parts = clean.Split('.');
                    if (parts.Length == 4 && parts[3] == "0")
                        clean = string.Join('.', parts.Take(3));
                    if (parts.Length >= 2)
                    {
                        _cachedIdeVersion = clean;
                        return clean;
                    }
                }
            }
        }
        catch { }

        _cachedIdeVersion = fallback;
        return fallback;
    }

    public static string ProfilesDir
    {
        get
        {
            var dir = Path.Combine(AppData, "APISwitch", "accounts");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
