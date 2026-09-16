using System.IO;

namespace APISwitch.Services;

public static class AgPaths
{
    public static string AppData =>
        Environment.GetEnvironmentVariable("APISWITCH_APPDATA") is { Length: > 0 } a
            ? a
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<string> CandidateUserDirs { get; } = new[]
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
