using System.IO;
using System.Text.Json;
using APISwitch.Models;

namespace APISwitch.Services;

public static class CliStore
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string ClaudeFile => Path.Combine(AgPaths.AppData, "APISwitch", "claude-providers.json");
    static string ClaudeDesktopFile => Path.Combine(AgPaths.AppData, "APISwitch", "claude-desktop-providers.json");
    static string CodexFile => Path.Combine(AgPaths.AppData, "APISwitch", "codex-providers.json");
    static string OpenCodeFile => Path.Combine(AgPaths.AppData, "APISwitch", "opencode-providers.json");
    static string PiFile => Path.Combine(AgPaths.AppData, "APISwitch", "pi-accounts.json");

    public static List<ClaudeProvider> LoadClaude()
    {
        var list = Load<List<ClaudeProvider>>(ClaudeFile);
        if (list == null)
        {
            list = new List<ClaudeProvider> { new() { Name = "Anthropic 官方", IsOfficial = true } };
            SaveClaude(list);
        }
        return list;
    }

    public static void SaveClaude(List<ClaudeProvider> list) => Save(ClaudeFile, list);

    public static List<ClaudeProvider> LoadClaudeDesktop()
    {
        var list = Load<List<ClaudeProvider>>(ClaudeDesktopFile);
        if (list == null)
        {
            list = new List<ClaudeProvider> { new() { Name = "Claude 官方", IsOfficial = true } };
            SaveClaudeDesktop(list);
        }
        return list;
    }

    public static void SaveClaudeDesktop(List<ClaudeProvider> list) => Save(ClaudeDesktopFile, list);

    public static List<CodexProvider> LoadCodex()
    {
        var list = Load<List<CodexProvider>>(CodexFile);
        if (list == null)
        {
            list = new List<CodexProvider> { new() { Name = "OpenAI 官方", IsOfficial = true } };
            SaveCodex(list);
        }
        return list;
    }

    public static void SaveCodex(List<CodexProvider> list) => Save(CodexFile, list);

    public static List<OpenCodeProvider> LoadOpencode() =>
        Load<List<OpenCodeProvider>>(OpenCodeFile) ?? new List<OpenCodeProvider>();

    public static void SaveOpencode(List<OpenCodeProvider> list) => Save(OpenCodeFile, list);

    public static List<PiAccount> LoadPi() =>
        Load<List<PiAccount>>(PiFile) ?? new List<PiAccount>();

    public static void SavePi(List<PiAccount> list) => Save(PiFile, list);

    static T? Load<T>(string path) where T : class
    {
        try
        {
            var text = FileUtil.ReadTextIfExists(path);
            return text == null ? null : JsonSerializer.Deserialize<T>(text);
        }
        catch
        {
            return null;
        }
    }

    static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }
}
