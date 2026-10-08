using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APISwitch.Models;
using Microsoft.Data.Sqlite;

namespace APISwitch.Services;

/// <summary>
/// 通过本地真实的 Antigravity CLI (agy.exe) 执行账号激活。
///
/// 相比手搓 HTTP 握手（自建 loadCodeAssist / streamGenerateContent 请求），
/// 真实 CLI 自带完整鉴权、companion project 解析、配额管理与设备指纹，
/// 与用户在终端里的使用路径完全一致，风控命中率最低，且能真正驱动配额周期。
///
/// 低消耗保证：
///   1. 只选用该账号实时可用模型中最便宜的 Gemini Flash（low effort）；
///   2. 提示词固定为极短文本，单轮即可返回；
///   3. 3P（Claude/GPT）池与 Gemini 池相互独立，各只需一次最小请求。
///
/// 无污染保证：执行前后快照 CLI 的对话目录 / 摘要库 / 历史记录，
///   结束后删除本次新增的对话，并把登录态恢复为激活前的账号。
/// </summary>
public static class AgCliService
{
    /// <summary>激活提示词：极短，单轮即可触发真实配额注册。</summary>
    public const string ActivationPrompt = "hi";

    public sealed record Result(
        bool Ok,
        bool RateLimited,
        string Message,
        string? Model,
        long LatencyMs,
        bool GeminiOk,
        bool ThirdPartyOk);

    /// <summary>CLI 数据目录（~/.gemini/antigravity-cli）。</summary>
    static string CliHome() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");

    static string ConvoDir() => Path.Combine(CliHome(), "conversations");
    static string SummariesDb() => Path.Combine(CliHome(), "conversation_summaries.db");
    static string HistoryPath() => Path.Combine(CliHome(), "history.jsonl");

    /// <summary>定位 agy.exe：先查 PATH，再查已知安装位置。</summary>
    public static string? FindCliPath()
    {
        var candidates = new List<string>();

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var d = dir.Trim().Trim('"');
                if (d.Length == 0) continue;
                candidates.Add(Path.Combine(d, "agy.exe"));
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "agy", "bin", "agy.exe"));
        candidates.Add(Path.Combine(CliHome(), "bin", "agy.exe"));

        return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
    }

    public static bool IsAvailable() => FindCliPath() != null;

    // ---------- 激活主流程 ----------

    public static async Task<Result> ActivateAsync(Profile profile, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var cli = FindCliPath();
        if (cli == null)
            return new Result(false, false, I18nService.T("AgQ.CliNotFound"), null, 0, false, false);

        // 1. CLI 读取 ~/.gemini/oauth_creds.json，Token 必须新鲜
        if (!string.IsNullOrEmpty(profile.RefreshToken))
            await AgQuotaService.EnsureFreshTokenAsync(profile);
        if (string.IsNullOrEmpty(profile.AccessToken))
            return new Result(false, false, I18nService.T("AgQ.ErrTokenRefresh"), null, 0, false, false);

        // 2. 备份当前 CLI 登录态，结束后原样恢复（不影响用户正在使用的账号）
        var credsPath = AgToolsService.GeminiCredsPath;
        byte[]? credsBackup = null;
        try { if (File.Exists(credsPath)) credsBackup = await File.ReadAllBytesAsync(credsPath, ct); }
        catch { }

        // 3. 快照对话状态，用于事后精准清理
        var snap = SnapshotConversations();

        bool geminiOk = false, thirdPartyOk = false, rateLimited = false;
        string? usedModel = null;
        string message = "";

        try
        {
            // 4. 把目标账号凭据写到 CLI 读取的位置
            await AgToolsService.ApplySystemCredentialsAsync(profile);

            // 5. 拉取该账号真实可用模型（唯一信任来源，绝不硬编码）
            var available = await AgQuotaService.FetchAvailableModelNamesAsync(profile, profile.AccessToken!);
            var geminiModels = AgQuotaService.ResolveGeminiModels(available);
            var claudeModels = AgQuotaService.ResolveClaudeModels(available);

            // 6. Gemini 池：最便宜模型 + 最低思考强度
            if (geminiModels.Count > 0)
            {
                usedModel = geminiModels[0];
                var r = await RunOnceAsync(cli, usedModel, ct);
                geminiOk = r.Ok;
                rateLimited |= r.RateLimited;
                if (!r.Ok && !string.IsNullOrEmpty(r.Message)) message = r.Message;
            }

            // 7. 3P 池与 Gemini 池独立，尽量一并激活；加一点间隔模拟人类节奏
            if (claudeModels.Count > 0)
            {
                await Task.Delay(Random.Shared.Next(600, 1200), ct);
                var r = await RunOnceAsync(cli, claudeModels[0], ct);
                thirdPartyOk = r.Ok;
                rateLimited |= r.RateLimited;
                if (r.Ok && usedModel == null) usedModel = claudeModels[0];
                if (!r.Ok && string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(r.Message)) message = r.Message;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
        finally
        {
            // 8. 恢复原登录态（字节级还原；原本没有该文件则删除）
            RestoreCreds(credsPath, credsBackup);
            // 9. 删除本次新增的对话，避免污染用户会话列表
            CleanupConversations(snap);
        }

        sw.Stop();
        bool ok = geminiOk || thirdPartyOk;

        if (ok)
        {
            var desc = (geminiOk, thirdPartyOk) switch
            {
                (true, true) => I18nService.T("AgQ.CliOkBoth"),
                (true, false) => I18nService.T("AgQ.CliOkGemini"),
                (false, true) => I18nService.T("AgQ.CliOkThirdParty"),
                _ => "",
            };
            message = I18nService.F("AgQ.CliOkFmt", usedModel ?? "", sw.ElapsedMilliseconds, desc);
        }
        else if (string.IsNullOrEmpty(message))
        {
            message = rateLimited ? I18nService.T("AgQ.ErrRateLimited") : I18nService.T("AgQ.CliFailed");
        }

        return new Result(ok, rateLimited && !ok, message, usedModel, sw.ElapsedMilliseconds, geminiOk, thirdPartyOk);
    }

    // ---------- 单轮执行 ----------

    static async Task<(bool Ok, bool RateLimited, string Message)> RunOnceAsync(
        string cli, string model, CancellationToken ct)
    {
        // 一次性空目录：CLI 不会扫描用户项目，也不会留下工作区记录
        var workDir = Path.Combine(Path.GetTempPath(), "apiswitch-ag-activate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            // 非交互单轮。注意：提示词必须紧贴 --print（--print=<prompt>），
            // 否则 CLI 会把后一个 flag 误当作提示词而报错退出（实测 exit=2）。
            psi.ArgumentList.Add($"--print={ActivationPrompt}");
            psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
            psi.ArgumentList.Add("--effort"); psi.ArgumentList.Add("low");
            psi.ArgumentList.Add("--disable-slash-commands");
            psi.ArgumentList.Add("--print-timeout"); psi.ArgumentList.Add("90s");
            // 临时空目录 + 一次性极短提示词，不存在真实工具调用；
            // 跳过权限提示可确保 CLI 绝不因等待交互而挂起。
            psi.ArgumentList.Add("--dangerously-skip-permissions");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
                return (true, false, "");

            var combined = stdout + "\n" + stderr;
            bool rl = combined.Contains("429", StringComparison.Ordinal)
                   || combined.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                   || combined.Contains("quota", StringComparison.OrdinalIgnoreCase)
                   || combined.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                   || combined.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
            return (false, rl, SummarizeError(combined));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    static string SummarizeError(string combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return I18nService.T("AgQ.CliFailed");
        // 取第一条有信息量的行，压掉堆栈与日志噪声
        foreach (var raw in combined.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("I2", StringComparison.Ordinal) || line.StartsWith("E2", StringComparison.Ordinal)) continue;
            if (line.Length > 200) line = line[..200] + "…";
            return line;
        }
        return I18nService.T("AgQ.CliFailed");
    }

    // ---------- 登录态备份 / 恢复 ----------

    static void RestoreCreds(string credsPath, byte[]? backup)
    {
        try
        {
            if (backup != null)
            {
                File.WriteAllBytes(credsPath, backup);
            }
            else if (File.Exists(credsPath))
            {
                File.Delete(credsPath);
            }
        }
        catch { }
    }

    // ---------- 对话清理 ----------

    sealed record ConvoSnapshot(HashSet<string> Files, HashSet<string> SummaryIds, int HistoryLines);

    static ConvoSnapshot SnapshotConversations()
    {
        var dir = ConvoDir();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir))
                files.Add(Path.GetFileName(f));
        }
        return new ConvoSnapshot(files, LoadSummaryIds(), CountHistoryLines());
    }

    static HashSet<string> LoadSummaryIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var db = SummariesDb();
        if (!File.Exists(db)) return ids;
        try
        {
            using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Private");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT conversation_id FROM conversation_summaries;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0)) ids.Add(r.GetString(0));
            }
        }
        catch { }
        return ids;
    }

    static int CountHistoryLines()
    {
        var hj = HistoryPath();
        if (!File.Exists(hj)) return 0;
        try { return File.ReadAllLines(hj).Length; } catch { return 0; }
    }

    static void CleanupConversations(ConvoSnapshot snap)
    {
        // 1) 删除本次新增的对话文件（只删快照里不存在的，绝不触碰原有对话）
        try
        {
            var dir = ConvoDir();
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (!snap.Files.Contains(Path.GetFileName(f)))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }
        catch { }

        // 2) 摘要库中删除新增行（只删快照之后出现的 id）
        try
        {
            var db = SummariesDb();
            if (File.Exists(db))
            {
                using var conn = new SqliteConnection($"Data Source={db};Cache=Private");
                conn.Open();

                var current = new List<string>();
                using (var read = conn.CreateCommand())
                {
                    read.CommandText = "SELECT conversation_id FROM conversation_summaries;";
                    using var r = read.ExecuteReader();
                    while (r.Read())
                    {
                        if (!r.IsDBNull(0)) current.Add(r.GetString(0));
                    }
                }
                foreach (var id in current)
                {
                    if (snap.SummaryIds.Contains(id)) continue;
                    using var del = conn.CreateCommand();
                    del.CommandText = "DELETE FROM conversation_summaries WHERE conversation_id = $id;";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
            }
        }
        catch { }

        // 3) history.jsonl 截回原行数（纯追加写，按行数回切即精确移除本次记录）
        try
        {
            var hj = HistoryPath();
            if (File.Exists(hj))
            {
                var lines = File.ReadAllLines(hj);
                if (lines.Length > snap.HistoryLines)
                    File.WriteAllLines(hj, lines.Take(snap.HistoryLines));
            }
        }
        catch { }
    }
}
