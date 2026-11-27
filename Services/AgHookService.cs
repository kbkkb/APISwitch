using System.IO;
using System.Text;
using System.Text.Json;

namespace APISwitch.Services;

/// <summary>
/// 管理 Antigravity 2.x 原生全局 Stop 生命周期插件钩子，
/// 在 APISwitch 未运行、用户在 IDE 内手动切号时仍能以零网络请求、&lt;30ms 无窗口方式精确记录每轮对话所属账号。
/// </summary>
public static class AgHookService
{
    public const string PluginFolderName = "apiswitch-usage-tracker";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string TurnLogPath =>
        Path.Combine(AgPaths.AppData, "APISwitch", "ag_turn_accounts.jsonl");

    public static string PluginDir
    {
        get
        {
            if (AgPaths.IsSandboxed)
                return Path.Combine(AgPaths.AppData, "gemini_config", "plugins", PluginFolderName);

            var home = ClaudeCli.HomeDir;
            return Path.Combine(home, ".gemini", "config", "plugins", PluginFolderName);
        }
    }

    public static string PluginManifestPath => Path.Combine(PluginDir, "plugin.json");
    public static string HooksConfigPath => Path.Combine(PluginDir, "hooks.json");

    public readonly record struct HookTurnEntry(long Ts, string ConversationId, string Email);

    /// <summary>
    /// 极速 CLI 钩子入口（由 Program.Main 在加载任何 WPF 程序集前直接调用）。
    /// 从 stdin 读取 Stop 事件的 conversationId，从本地明文配置读取当前登录邮箱并追加落盘，stdout 返回 {"decision":"stop"}。
    /// </summary>
    public static int RunCliHook()
    {
        try
        {
            string rawInput = ReadStdinWithTimeout(450);
            string convId = ExtractConversationId(rawInput);
            string email = ReadCurrentAntigravityEmailFast();

            if (!string.IsNullOrWhiteSpace(email))
            {
                AppendTurnRecord(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), convId, email.Trim());
            }
        }
        catch
        {
            // 钩子任何异常均静默降级，绝不阻塞 Antigravity 正常对话结束
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            var bytes = Utf8NoBom.GetBytes("{\"decision\":\"stop\"}\n");
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch { }

        return 0;
    }

    /// <summary>按当前应用设置同步安装或卸载全局追踪插件。</summary>
    public static void SyncWithSettings(string? explicitExePath = null)
    {
        EnsureHookInstalled(AppSettingsService.Current.AntigravityAccountTrackingEnabled, explicitExePath);
    }

    public static bool IsHookInstalled() =>
        File.Exists(PluginManifestPath) && File.Exists(HooksConfigPath);

    /// <summary>安装或卸载 ~/.gemini/config/plugins/apiswitch-usage-tracker 全局钩子。</summary>
    public static void EnsureHookInstalled(bool enable, string? explicitExePath = null)
    {
        try
        {
            var dir = PluginDir;
            if (!enable)
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }

            var exePath = string.IsNullOrWhiteSpace(explicitExePath)
                ? Environment.ProcessPath
                : explicitExePath;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            Directory.CreateDirectory(dir);

            const string manifestJson = """
{
  "name": "APISwitch Usage Tracker",
  "version": "1.0.0",
  "description": "Records local Antigravity conversation account email on turn completion for APISwitch per-account usage analytics."
}
""";
            WriteFileIfChanged(PluginManifestPath, manifestJson);

            string commandStr = $"\"{exePath}\" --ag-hook";
            var hooksObj = new Dictionary<string, object>
            {
                [PluginFolderName] = new Dictionary<string, object>
                {
                    ["Stop"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "command",
                            ["command"] = commandStr,
                            ["timeout"] = 5,
                        }
                    }
                }
            };
            var hooksJson = JsonSerializer.Serialize(hooksObj, new JsonSerializerOptions { WriteIndented = true });
            WriteFileIfChanged(HooksConfigPath, hooksJson);
        }
        catch { }
    }

    /// <summary>读取全部离线回合账号记录，返回日志签名（length^mtimeTicks）与条目列表。</summary>
    public static (long Signature, List<HookTurnEntry> Entries) LoadTurnEntries()
    {
        var list = new List<HookTurnEntry>();
        try
        {
            var fi = new FileInfo(TurnLogPath);
            if (!fi.Exists || fi.Length == 0) return (0L, list);

            long sig = (fi.Length * 397L) ^ fi.LastWriteTimeUtc.Ticks;
            using var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    long ts = root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out var t) ? t : 0;
                    string cid = root.TryGetProperty("cid", out var cidEl) && cidEl.ValueKind == JsonValueKind.String
                        ? cidEl.GetString() ?? ""
                        : "";
                    string email = root.TryGetProperty("email", out var emEl) && emEl.ValueKind == JsonValueKind.String
                        ? emEl.GetString() ?? ""
                        : "";
                    if (ts > 0 && !string.IsNullOrWhiteSpace(email))
                        list.Add(new HookTurnEntry(ts, cid.Trim(), email.Trim()));
                }
                catch { }
            }
            return (sig, list);
        }
        catch
        {
            return (0L, list);
        }
    }

    /// <summary>
    /// 仅读取本地轻量 JSON 文件获取当前 Antigravity 登录邮箱（不读加密凭据库、不发起任何网络请求）。
    /// </summary>
    public static string ReadCurrentAntigravityEmailFast()
    {
        // 1. Antigravity 2.x Hub: %APPDATA%\Antigravity\app_storage.json -> agy.onboarding.lastLoginUsername
        try
        {
            var hubPath = AgPaths.HubAppStoragePath;
            if (File.Exists(hubPath))
            {
                using var fs = new FileStream(hubPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("agy.onboarding.lastLoginUsername", out var uEl) &&
                    uEl.ValueKind == JsonValueKind.String)
                {
                    var u = uEl.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(u) && u.Contains('@'))
                        return u;
                }
            }
        }
        catch { }

        // 2. Fallback (非沙盒环境): ~/.gemini/google_accounts.json -> active
        if (!AgPaths.IsSandboxed)
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var gaPath = Path.Combine(home, ".gemini", "google_accounts.json");
                if (File.Exists(gaPath))
                {
                    using var fs = new FileStream(gaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = JsonDocument.Parse(fs);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("active", out var actEl) &&
                        actEl.ValueKind == JsonValueKind.String)
                    {
                        var act = actEl.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(act) && act.Contains('@'))
                            return act;
                    }
                }
            }
            catch { }
        }

        return "";
    }

    private static string ReadStdinWithTimeout(int timeoutMs)
    {
        try
        {
            var task = Task.Run(() =>
            {
                using var stdin = Console.OpenStandardInput();
                if (stdin == Stream.Null || !stdin.CanRead) return "";
                using var sr = new StreamReader(stdin, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                return sr.ReadToEnd();
            });
            return task.Wait(timeoutMs) ? task.Result : "";
        }
        catch
        {
            return "";
        }
    }

    private static string ExtractConversationId(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";
            if (root.TryGetProperty("conversationId", out var c1) && c1.ValueKind == JsonValueKind.String)
                return c1.GetString()?.Trim() ?? "";
            if (root.TryGetProperty("conversation_id", out var c2) && c2.ValueKind == JsonValueKind.String)
                return c2.GetString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    public static void AppendTurnRecord(long tsSec, string? convId, string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var path = TurnLogPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 超过 4MB 时保留尾部 2MB（对齐到换行符），防止常年累积膨胀
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 4_194_304)
            {
                const int keep = 2_097_152;
                var tail = new byte[keep];
                using var rfs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                rfs.Seek(-keep, SeekOrigin.End);
                int read = 0;
                while (read < keep)
                {
                    int n = rfs.Read(tail, read, keep - read);
                    if (n <= 0) break;
                    read += n;
                }
                int firstNl = Array.IndexOf(tail, (byte)'\n', 0, read);
                int offset = firstNl >= 0 && firstNl + 1 < read ? firstNl + 1 : 0;
                rfs.SetLength(0);
                rfs.Position = 0;
                rfs.Write(tail, offset, read - offset);
            }
        }
        catch { }

        string payload = JsonSerializer.Serialize(new
        {
            ts = tsSec,
            cid = convId ?? "",
            email = email.Trim()
        }) + "\n";
        var bytes = Utf8NoBom.GetBytes(payload);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes, 0, bytes.Length);
    }

    private static void WriteFileIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path, Utf8NoBom);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                    return;
            }
        }
        catch { }
        File.WriteAllText(path, content, Utf8NoBom);
    }
}
