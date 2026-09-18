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
    static string PiProvidersFile => Path.Combine(AgPaths.AppData, "APISwitch", "pi-providers.json");
    static string PiFile => Path.Combine(AgPaths.AppData, "APISwitch", "pi-accounts.json");

    public static List<ClaudeProvider> LoadClaude()
    {
        var list = Load<List<ClaudeProvider>>(ClaudeFile);
        if (list == null)
        {
            list = new List<ClaudeProvider> { new() { Name = "Anthropic 官方", IsOfficial = true } };
            SaveClaude(list);
            return list;
        }
        var deduped = DeduplicateClaude(list);
        if (deduped.Count != list.Count)
        {
            SaveClaude(deduped);
        }
        return deduped;
    }

    public static void SaveClaude(List<ClaudeProvider> list) => Save(ClaudeFile, DeduplicateClaude(list));

    public static List<ClaudeProvider> LoadClaudeDesktop()
    {
        var list = Load<List<ClaudeProvider>>(ClaudeDesktopFile);
        if (list == null)
        {
            list = new List<ClaudeProvider> { new() { Name = "Claude 官方", IsOfficial = true } };
            SaveClaudeDesktop(list);
            return list;
        }
        var deduped = DeduplicateClaude(list);
        if (deduped.Count != list.Count)
        {
            SaveClaudeDesktop(deduped);
        }
        return deduped;
    }

    public static void SaveClaudeDesktop(List<ClaudeProvider> list) => Save(ClaudeDesktopFile, DeduplicateClaude(list));

    public static List<CodexProvider> LoadCodex()
    {
        var list = Load<List<CodexProvider>>(CodexFile);
        if (list == null)
        {
            list = new List<CodexProvider> { new() { Name = "OpenAI 官方", IsOfficial = true } };
            SaveCodex(list);
            return list;
        }
        var deduped = DeduplicateCodex(list);
        if (deduped.Count != list.Count)
        {
            SaveCodex(deduped);
        }
        return deduped;
    }

    public static void SaveCodex(List<CodexProvider> list) => Save(CodexFile, DeduplicateCodex(list));

    public static List<ClaudeProvider> DeduplicateClaude(List<ClaudeProvider> list)
    {
        if (list == null) return new List<ClaudeProvider>();
        var result = new List<ClaudeProvider>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (item == null) continue;
            if (seenNames.Contains(item.Name)) continue;
            if (!string.IsNullOrEmpty(item.Id) && seenIds.Contains(item.Id)) continue;

            seenNames.Add(item.Name);
            if (!string.IsNullOrEmpty(item.Id)) seenIds.Add(item.Id);
            result.Add(item);
        }
        return result;
    }

    public static List<CodexProvider> DeduplicateCodex(List<CodexProvider> list)
    {
        if (list == null) return new List<CodexProvider>();
        var result = new List<CodexProvider>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (item == null) continue;
            if (seenNames.Contains(item.Name)) continue;
            if (!string.IsNullOrEmpty(item.Id) && seenIds.Contains(item.Id)) continue;

            seenNames.Add(item.Name);
            if (!string.IsNullOrEmpty(item.Id)) seenIds.Add(item.Id);
            result.Add(item);
        }
        return result;
    }

    public static List<OpenCodeProvider> DeduplicateOpenCode(List<OpenCodeProvider> list)
    {
        if (list == null) return new List<OpenCodeProvider>();
        var result = new List<OpenCodeProvider>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (item == null) continue;
            var name = item.Name?.Trim() ?? "";
            var id = item.Id?.Trim() ?? "";
            if (!string.IsNullOrEmpty(name) && seenNames.Contains(name)) continue;
            if (!string.IsNullOrEmpty(id) && seenIds.Contains(id)) continue;

            if (!string.IsNullOrEmpty(name)) seenNames.Add(name);
            if (!string.IsNullOrEmpty(id)) seenIds.Add(id);
            result.Add(item);
        }
        return result;
    }

    public static List<PiProvider> DeduplicatePi(List<PiProvider> list)
    {
        if (list == null) return new List<PiProvider>();
        var result = new List<PiProvider>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (item == null) continue;
            var name = item.Name?.Trim() ?? "";
            var id = item.Id?.Trim() ?? "";
            if (!string.IsNullOrEmpty(name) && seenNames.Contains(name)) continue;
            if (!string.IsNullOrEmpty(id) && seenIds.Contains(id)) continue;

            if (!string.IsNullOrEmpty(name)) seenNames.Add(name);
            if (!string.IsNullOrEmpty(id)) seenIds.Add(id);
            result.Add(item);
        }
        return result;
    }

    public static List<OpenCodeProvider> LoadOpencode()
    {
        var list = Load<List<OpenCodeProvider>>(OpenCodeFile) ?? new List<OpenCodeProvider>();
        list = DeduplicateOpenCode(list);
        try
        {
            var fromNative = OpenCodeCli.LoadProviders();
            if (fromNative.Count > 0)
            {
                bool changed = false;
                foreach (var native in fromNative)
                {
                    if (string.IsNullOrWhiteSpace(native.Id) && string.IsNullOrWhiteSpace(native.Name)) continue;

                    var existing = list.FirstOrDefault(x =>
                        (!string.IsNullOrWhiteSpace(native.Id) && string.Equals(x.Id, native.Id, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(native.Name) && string.Equals(x.Name, native.Name, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(native.BaseUrl) && string.Equals(x.BaseUrl?.TrimEnd('/'), native.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    );

                    if (existing == null)
                    {
                        list.Add(native);
                        changed = true;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(native.Id) && existing.Id != native.Id)
                        {
                            existing.Id = native.Id;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.BaseUrl) && existing.BaseUrl != native.BaseUrl)
                        {
                            existing.BaseUrl = native.BaseUrl;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.ApiKey) && existing.ApiKey != native.ApiKey)
                        {
                            existing.ApiKey = native.ApiKey;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.Name) && existing.Name != native.Name)
                        {
                            existing.Name = native.Name;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.Npm) && existing.Npm != native.Npm)
                        {
                            existing.Npm = native.Npm;
                            changed = true;
                        }
                        if (native.CustomModels.Count > 0 && (existing.CustomModels.Count == 0 || !string.IsNullOrWhiteSpace(native.ModelsJson)))
                        {
                            existing.CustomModels = native.CustomModels;
                            existing.ModelsJson = native.ModelsJson;
                            changed = true;
                        }
                    }
                }
                list = DeduplicateOpenCode(list);
                if (changed || list.Count == 0)
                {
                    SaveOpencode(list);
                }
            }
        }
        catch { }
        return DeduplicateOpenCode(list);
    }

    public static void SaveOpencode(List<OpenCodeProvider> list) => Save(OpenCodeFile, DeduplicateOpenCode(list));

    public static List<PiProvider> LoadPiProviders()
    {
        var list = Load<List<PiProvider>>(PiProvidersFile) ?? new List<PiProvider>();
        list = DeduplicatePi(list);
        try
        {
            var fromNative = PiCli.LoadProviders();
            if (fromNative.Count > 0)
            {
                bool changed = false;
                foreach (var native in fromNative)
                {
                    if (string.IsNullOrWhiteSpace(native.Id) && string.IsNullOrWhiteSpace(native.Name)) continue;

                    var existing = list.FirstOrDefault(x =>
                        (!string.IsNullOrWhiteSpace(native.Id) && string.Equals(x.Id, native.Id, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(native.Name) && string.Equals(x.Name, native.Name, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(native.BaseUrl) && string.Equals(x.BaseUrl?.TrimEnd('/'), native.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    );

                    if (existing == null)
                    {
                        list.Add(native);
                        changed = true;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(native.Id) && existing.Id != native.Id)
                        {
                            existing.Id = native.Id;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.BaseUrl) && existing.BaseUrl != native.BaseUrl)
                        {
                            existing.BaseUrl = native.BaseUrl;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.ApiKey) && existing.ApiKey != native.ApiKey)
                        {
                            existing.ApiKey = native.ApiKey;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.Name) && existing.Name != native.Name)
                        {
                            existing.Name = native.Name;
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(native.Api) && existing.Api != native.Api)
                        {
                            existing.Api = native.Api;
                            changed = true;
                        }
                        if (native.CustomModels.Count > 0 && (existing.CustomModels.Count == 0 || !string.IsNullOrWhiteSpace(native.ModelsJson)))
                        {
                            existing.CustomModels = native.CustomModels;
                            existing.ModelsJson = native.ModelsJson;
                            changed = true;
                        }
                    }
                }
                list = DeduplicatePi(list);
                if (changed || list.Count == 0)
                {
                    SavePiProviders(list);
                }
            }
        }
        catch { }
        return DeduplicatePi(list);
    }

    public static void SavePiProviders(List<PiProvider> list) => Save(PiProvidersFile, DeduplicatePi(list));

    static string ProxyConfigFile => Path.Combine(AgPaths.AppData, "APISwitch", "proxy-config.json");

    public class ProxySettings
    {
        public bool Enabled { get; set; } = true;
        public bool CodexEnabled { get; set; } = true;
        public bool ClaudeCliEnabled { get; set; } = false;
        public bool ClaudeDesktopEnabled { get; set; } = false;
        public int Port { get; set; } = 15725;
        public string Host { get; set; } = "127.0.0.1";
    }

    public static ProxySettings LoadProxySettings() =>
        Load<ProxySettings>(ProxyConfigFile) ?? new ProxySettings();

    public static void SaveProxySettings(ProxySettings settings) =>
        Save(ProxyConfigFile, settings);

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
