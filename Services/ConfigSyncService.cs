using System.IO;
using System.Text.Json;
using APISwitch.Models;

namespace APISwitch.Services;

public class BackupBundle
{
    public string AppVersion { get; set; } = "v0.1.1";
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public string DeviceName { get; set; } = Environment.MachineName;

    public List<ClaudeProvider>? ClaudeCliProviders { get; set; }
    public List<ClaudeProvider>? ClaudeDesktopProviders { get; set; }
    public List<CodexProvider>? CodexProviders { get; set; }
    public List<OpenCodeProvider>? OpenCodeProviders { get; set; }
    public List<PiProvider>? PiProviders { get; set; }
    public List<PiAccount>? PiAccounts { get; set; }
    public List<Profile>? AntigravityProfiles { get; set; }
    public List<string>? TabOrder { get; set; }
}

public class ImportResult
{
    public int ClaudeCliCount { get; set; }
    public int ClaudeDesktopCount { get; set; }
    public int CodexCount { get; set; }
    public int OpenCodeCount { get; set; }
    public int PiCount { get; set; }
    public int PiAccountsCount { get; set; }
    public int ProfilesCount { get; set; }
    public int TotalCount => ClaudeCliCount + ClaudeDesktopCount + CodexCount + OpenCodeCount + PiCount + ProfilesCount;
}

public static class ConfigSyncService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static BackupBundle CreateBackupBundle(string appVersion = "v0.1.1")
    {
        var bundle = new BackupBundle
        {
            AppVersion = appVersion,
            ExportedAt = DateTime.Now,
            DeviceName = Environment.MachineName,
            ClaudeCliProviders = CliStore.LoadClaude().Where(x => !x.IsOfficial).ToList(),
            ClaudeDesktopProviders = CliStore.LoadClaudeDesktop().Where(x => !x.IsOfficial).ToList(),
            CodexProviders = CliStore.LoadCodex().Where(x => !x.IsOfficial).ToList(),
            OpenCodeProviders = CliStore.LoadOpencode().ToList(),
            PiProviders = CliStore.LoadPiProviders().ToList(),
            PiAccounts = CliStore.LoadPi().ToList(),
            AntigravityProfiles = ProfileStore.Load().ToList()
        };

        var tabOrderFile = Path.Combine(AgPaths.AppData, "APISwitch", "tab-order.json");
        if (File.Exists(tabOrderFile))
        {
            try
            {
                bundle.TabOrder = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(tabOrderFile));
            }
            catch { }
        }

        return bundle;
    }

    public static void ExportToFile(string filePath, string appVersion = "v0.1.1")
    {
        var bundle = CreateBackupBundle(appVersion);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, JsonSerializer.Serialize(bundle, JsonOpts));
    }

    public static BackupBundle ReadBackupFile(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("备份文件不存在", filePath);
        var json = File.ReadAllText(filePath);
        var bundle = JsonSerializer.Deserialize<BackupBundle>(json);
        return bundle ?? throw new InvalidOperationException("无法解析该备份文件格式");
    }

    public static ImportResult ImportFromFile(string filePath, bool overwrite)
    {
        var bundle = ReadBackupFile(filePath);
        return ImportBackupBundle(bundle, overwrite);
    }

    public static ImportResult ImportBackupBundle(BackupBundle bundle, bool overwrite)
    {
        var res = new ImportResult();

        if (overwrite)
        {
            // Replace only sections present in the backup. Empty properties mean "keep current data",
            // so an older or hand-edited backup cannot erase newer stores.
            var claudeList = bundle.ClaudeCliProviders == null
                ? CliStore.LoadClaude()
                : new List<ClaudeProvider> { new() { Name = "Anthropic 官方", IsOfficial = true } }.Concat(bundle.ClaudeCliProviders).ToList();
            if (bundle.ClaudeCliProviders != null) res.ClaudeCliCount = bundle.ClaudeCliProviders.Count;
            CliStore.SaveClaude(claudeList);

            var desktopList = bundle.ClaudeDesktopProviders == null
                ? CliStore.LoadClaudeDesktop()
                : new List<ClaudeProvider> { new() { Name = "Claude 官方", IsOfficial = true } }.Concat(bundle.ClaudeDesktopProviders).ToList();
            if (bundle.ClaudeDesktopProviders != null) res.ClaudeDesktopCount = bundle.ClaudeDesktopProviders.Count;
            CliStore.SaveClaudeDesktop(desktopList);

            var codexList = bundle.CodexProviders == null
                ? CliStore.LoadCodex()
                : new List<CodexProvider> { new() { Name = "OpenAI 官方", IsOfficial = true } }.Concat(bundle.CodexProviders).ToList();
            if (bundle.CodexProviders != null) res.CodexCount = bundle.CodexProviders.Count;
            CliStore.SaveCodex(codexList);

            var ocList = bundle.OpenCodeProviders ?? CliStore.LoadOpencode();
            CliStore.SaveOpencode(ocList);
            res.OpenCodeCount = ocList.Count;

            var piList = bundle.PiProviders ?? CliStore.LoadPiProviders();
            CliStore.SavePiProviders(piList);
            res.PiCount = piList.Count;

            var piAcc = bundle.PiAccounts ?? CliStore.LoadPi();
            CliStore.SavePi(piAcc);
            res.PiAccountsCount = piAcc.Count;

            if (bundle.AntigravityProfiles != null)
            {
                var existingProfiles = ProfileStore.Load();
                foreach (var p in existingProfiles) ProfileStore.Delete(p);
                foreach (var p in bundle.AntigravityProfiles) ProfileStore.Save(p);
                res.ProfilesCount = bundle.AntigravityProfiles.Count;
            }
        }

        else
        {
            // Merge mode
            // 1. Claude CLI
            var claudeList = CliStore.LoadClaude();
            if (bundle.ClaudeCliProviders != null)
            {
                foreach (var p in bundle.ClaudeCliProviders)
                {
                    var idx = claudeList.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) claudeList[idx] = p; else claudeList.Add(p);
                }
                res.ClaudeCliCount = bundle.ClaudeCliProviders.Count;
            }
            CliStore.SaveClaude(claudeList);

            // 2. Claude Desktop
            var desktopList = CliStore.LoadClaudeDesktop();
            if (bundle.ClaudeDesktopProviders != null)
            {
                foreach (var p in bundle.ClaudeDesktopProviders)
                {
                    var idx = desktopList.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) desktopList[idx] = p; else desktopList.Add(p);
                }
                res.ClaudeDesktopCount = bundle.ClaudeDesktopProviders.Count;
            }
            CliStore.SaveClaudeDesktop(desktopList);

            // 3. Codex
            var codexList = CliStore.LoadCodex();
            if (bundle.CodexProviders != null)
            {
                foreach (var p in bundle.CodexProviders)
                {
                    var idx = codexList.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) codexList[idx] = p; else codexList.Add(p);
                }
                res.CodexCount = bundle.CodexProviders.Count;
            }
            CliStore.SaveCodex(codexList);

            // 4. OpenCode
            var openCodeList = CliStore.LoadOpencode();
            if (bundle.OpenCodeProviders != null)
            {
                foreach (var p in bundle.OpenCodeProviders)
                {
                    var idx = openCodeList.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) openCodeList[idx] = p; else openCodeList.Add(p);
                }
                res.OpenCodeCount = bundle.OpenCodeProviders.Count;
            }
            CliStore.SaveOpencode(openCodeList);

            // 5. Pi
            var piList = CliStore.LoadPiProviders();
            if (bundle.PiProviders != null)
            {
                foreach (var p in bundle.PiProviders)
                {
                    var idx = piList.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) piList[idx] = p; else piList.Add(p);
                }
                res.PiCount = bundle.PiProviders.Count;
            }
            CliStore.SavePiProviders(piList);

            var piAccounts = CliStore.LoadPi();
            if (bundle.PiAccounts != null)
            {
                foreach (var a in bundle.PiAccounts)
                {
                    var idx = piAccounts.FindIndex(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) piAccounts[idx] = a; else piAccounts.Add(a);
                }
                res.PiAccountsCount = bundle.PiAccounts.Count;
            }
            CliStore.SavePi(piAccounts);

            // 6. Profiles
            if (bundle.AntigravityProfiles != null)
            {
                foreach (var p in bundle.AntigravityProfiles) ProfileStore.Save(p);
                res.ProfilesCount = bundle.AntigravityProfiles.Count;
            }
        }

        if (bundle.TabOrder != null && bundle.TabOrder.Count > 0)
        {
            try
            {
                var tabOrderFile = Path.Combine(AgPaths.AppData, "APISwitch", "tab-order.json");
                File.WriteAllText(tabOrderFile, JsonSerializer.Serialize(bundle.TabOrder));
            }
            catch { }
        }

        return res;
    }
}
