using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
 using System.Text;
 using System.Text.Json;
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
 
     /// <summary>每个配额池最多尝试多少个候选模型（全部来自账号实时可用列表）。</summary>
     private const int MaxModelAttempts = 3;
 
     /// <summary>单次 CLI 调用超时（秒）。冷启动 + 一次最小请求足够，超时即判失败换下一个。</summary>
     private const int AttemptTimeoutSeconds = 60;
 
     /// <summary>整个激活流程的时间预算，超过就停手并给出明确提示，绝不久久卡住。</summary>
     private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(150);
 
     /// <summary>
     /// 记住"已被证明可用"的参数组下标。某个模型成功后，后续模型直接从这组开始，
     /// 不再把不支持的参数从头试一遍——那是之前"越试越久、长时间卡住"的主因。
     /// </summary>
     private static int PreferredArgSet;
 
     /// <summary>当前优先使用的参数组（供日志/诊断）。</summary>
     internal static int PreferredArgSetIndex => PreferredArgSet;

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
             ct.ThrowIfCancellationRequested();
            // 4. 把目标账号凭据写到 CLI 读取的位置
            await AgToolsService.ApplySystemCredentialsAsync(profile);

             // 5. 模型来源（按可靠性排序）：
             //    a) CLI 自报的可用模型——这是唯一能直接被 --model 接受的命名体系
             //       （形如 "Gemini 3.8 Flash (Low)"，思考档位写在名字里，所以没有 --effort 参数）；
             //    b) Google API 返回的可用模型（命名体系不同，仅作兜底）。
             var cliModels = await ProbeCliModelsAsync(cli, ct);
             var available = await AgQuotaService.FetchAvailableModelNamesAsync(profile, profile.AccessToken!);
             var geminiModels = PickCheapest(cliModels, geminiPool: true);
             var claudeModels = PickCheapest(cliModels, geminiPool: false);
             var argonModels = PickArgonModels(cliModels, available);
             if (geminiModels.Count == 0)
                 geminiModels = AgQuotaService.OrderByVersionDescending(AgQuotaService.ResolveGeminiModels(available));
             if (claudeModels.Count == 0)
                 claudeModels = AgQuotaService.OrderByVersionDescending(AgQuotaService.ResolveClaudeModels(available));
             App.LogStartup($"AgCli: {profile.Email} cliModels={cliModels.Count} apiModels={available.Count} " +
                            $"gemini=[{string.Join(" | ", geminiModels.Take(4))}] argon=[{string.Join(" | ", argonModels.Take(3))}] claude=[{string.Join(" | ", claudeModels.Take(4))}] " +
                            $"preferredArgSet={PreferredArgSet}");
 
             // 6. Gemini 池：依次尝试实时可用列表里的候选模型（最新优先），
             //    某个模型 CLI 不接受就换下一个；总时长有上限，绝不无限卡住。
             if (geminiModels.Count > 0)
             {
                 foreach (var m in geminiModels.Take(MaxModelAttempts))
                 {
                     if (sw.Elapsed > TotalBudget) { message = I18nService.T("AgQ.CliTimeout"); break; }
                     App.LogStartup($"AgCli: {profile.Email} gemini try model={m}");
                     var r = await RunOnceAsync(cli, m, ct);
                     geminiOk = r.Ok;
                     if (r.Ok) { usedModel = m; break; }
                     rateLimited |= r.RateLimited;
                     if (!string.IsNullOrEmpty(r.Message)) message = r.Message;
                     // 配额用尽 / 鉴权网络类硬失败：换模型没有意义，立刻停
                     if (r.RateLimited || (!r.ModelInvalid && !r.UsageError)) break;
                     await Task.Delay(Random.Shared.Next(200, 500), ct);
                 }
             }

             // 6.5 Gemini 4 Argon 独立配额池（仅当该账号在 API 侧支持非 disabled 的 Argon 模型时一并激活）
             if (argonModels.Count > 0 && sw.Elapsed <= TotalBudget)
             {
                 await Task.Delay(Random.Shared.Next(300, 600), ct);
                 foreach (var m in argonModels.Take(2))
                 {
                     if (sw.Elapsed > TotalBudget) break;
                     App.LogStartup($"AgCli: {profile.Email} argon try model={m}");
                     var r = await RunOnceAsync(cli, m, ct);
                     if (r.Ok) { if (usedModel == null) usedModel = m; break; }
                     if (r.RateLimited || (!r.ModelInvalid && !r.UsageError)) break;
                     await Task.Delay(Random.Shared.Next(200, 400), ct);
                 }
             }
 
             // 7. 3P 池与 Gemini 池独立，尽量一并激活；同样最新优先依次尝试
             if (claudeModels.Count > 0 && sw.Elapsed <= TotalBudget)
             {
                 await Task.Delay(Random.Shared.Next(400, 800), ct);
                 foreach (var m in claudeModels.Take(MaxModelAttempts))
                 {
                     if (sw.Elapsed > TotalBudget) { message = I18nService.T("AgQ.CliTimeout"); break; }
                     App.LogStartup($"AgCli: {profile.Email} claude try model={m}");
                     var r = await RunOnceAsync(cli, m, ct);
                     thirdPartyOk = r.Ok;
                     if (r.Ok) { if (usedModel == null) usedModel = m; break; }
                     rateLimited |= r.RateLimited;
                     if (!r.Ok && string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(r.Message)) message = r.Message;
                     if (r.RateLimited || (!r.ModelInvalid && !r.UsageError)) break;
                     await Task.Delay(Random.Shared.Next(200, 500), ct);
                 }
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
 
     /// <summary>
     /// 参数组合：从"最可能被支持"到"最简"。
     /// 已知事实（实测）：--effort / --disable-slash-commands / --print-timeout 不被支持
     /// （思考档位写在模型名里，如 "Gemini 3.8 Flash (Low)"），所以绝不放进来的——
     /// 放进来只会让每次尝试都先失败一次，白白卡很久。
     /// model 为 null 时表示"不指定模型"，让 CLI 用它自己的默认模型（最后兜底）。
     /// 注意：提示词必须紧贴 --print（--print=&lt;prompt&gt;），
     /// 否则 CLI 会把后一个 flag 误当作提示词而报错退出（实测 exit=2）。
     /// </summary>
     static List<List<string>> BuildArgSets(string? model)
     {
         var sets = new List<List<string>>();
         if (!string.IsNullOrEmpty(model))
         {
             sets.Add(new()
             {
                 $"--print={ActivationPrompt}",
                 "--output-format", "json",
                 "--model", model,
                 "--dangerously-skip-permissions",
             });
             sets.Add(new()
             {
                 $"--print={ActivationPrompt}",
                 "--output-format", "json",
                 "--model", model,
             });
         }
         // 完全不指定模型：让 CLI 用它自己的默认模型
         sets.Add(new()
         {
             $"--print={ActivationPrompt}",
             "--output-format", "json",
             "--dangerously-skip-permissions",
         });
         sets.Add(new()
         {
             $"--print={ActivationPrompt}",
             "--output-format", "json",
         });
         return sets;
     }
 
     /// <summary>
     /// 探测 CLI 自己认哪些模型：用一个不存在的模型名触发它的参数校验，
     /// 错误信息里会带 "Available models:" 全列表。不发出任何真实请求，不耗额度。
     /// </summary>
     static async Task<List<string>> ProbeCliModelsAsync(string cli, CancellationToken ct)
     {
         var workDir = Path.Combine(Path.GetTempPath(), "apiswitch-ag-probe-" + Guid.NewGuid().ToString("N")[..8]);
         Directory.CreateDirectory(workDir);
         try
         {
             const string bogus = "__apiswitch_probe__";
             var args = new List<string>
             {
                 $"--print={ActivationPrompt}",
                 "--output-format", "json",
                 "--model", bogus,
                 "--dangerously-skip-permissions",
             };
             var r = await RunAttemptAsync(cli, workDir, args, bogus, ct);
             var models = ParseAvailableModels(r.Raw);
             App.LogStartup($"AgCli: probe exit-model-list count={models.Count} [{string.Join(" | ", models)}]");
             return models;
         }
         catch (OperationCanceledException)
         {
             throw;
         }
         catch (Exception ex)
         {
             App.LogStartup($"AgCli: probe failed: {ex.Message}");
             return new List<string>();
         }
         finally
         {
             try { Directory.Delete(workDir, true); } catch { }
         }
     }
 
     /// <summary>
     /// 解析 CLI 错误信息里的 "Available models:" 列表。条目形如
     /// "Gemini 3.8 Flash (Low)" / "Claude Sonnet 4.6 (Thinking)"，整行即模型名。
     /// </summary>
     internal static List<string> ParseAvailableModels(string text)
     {
         var result = new List<string>();
         if (string.IsNullOrEmpty(text)) return result;
 
         var lines = text.Replace("\r\n", "\n").Split('\n');
         bool inSection = false;
         foreach (var raw in lines)
         {
             var line = raw.Trim();
             if (!inSection)
             {
                 if (line.Contains("Available models", StringComparison.OrdinalIgnoreCase)) inSection = true;
                 continue;
             }
             if (line.Length == 0)
             {
                 if (result.Count > 0) break;   // 列表结束后遇到空行即结束
                 continue;
             }
             // 列表结束后若又出现 JSON/日志噪声，立刻停
             if (line.StartsWith("{") || line.StartsWith("\"") || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                 break;
             if (!result.Contains(line, StringComparer.OrdinalIgnoreCase)) result.Add(line);
             if (result.Count >= 60) break;
         }
         return result;
     }
 
     /// <summary>
     /// 从 CLI 自报模型里挑最便宜的候选：Flash 优先于 Pro、低思考档优先、
     /// 同档位取版本更新的（老版本多已下线）。geminiPool=false 时取 Claude/GPT 池。
     /// Argon 属于独立配额池，由 PickArgonModels 单独处理。
     /// </summary>
     internal static List<string> PickCheapest(List<string> models, bool geminiPool)
     {
         if (models == null || models.Count == 0) return new List<string>();
         return models
             .Where(m => geminiPool
                 ? m.Contains("Gemini", StringComparison.OrdinalIgnoreCase) && !m.Contains("Argon", StringComparison.OrdinalIgnoreCase)
                 : !m.Contains("Gemini", StringComparison.OrdinalIgnoreCase))
             .OrderBy(EffortRank)
             .ThenBy(PoolRank)
             .ThenByDescending(ModelNumber)
             .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
             .ToList();
     }

     /// <summary>
     /// 筛选 Gemini 4 Argon 独立配额池候选模型（Low 优先）。
     /// 若 API 可用列表仅包含 gemini-4-argon-disabled（表示该账号未开通独立 Argon 额度），则返回空列表。
     /// </summary>
     internal static List<string> PickArgonModels(List<string> cliModels, List<string> apiModels)
     {
         bool hasEnabledArgonInApi = apiModels != null && apiModels.Any(m =>
             m.Contains("argon", StringComparison.OrdinalIgnoreCase) &&
             !m.Contains("disabled", StringComparison.OrdinalIgnoreCase));
         if (!hasEnabledArgonInApi) return new List<string>();

         if (cliModels != null && cliModels.Count > 0)
         {
             var fromCli = cliModels
                 .Where(m => m.Contains("Argon", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(EffortRank)
                 .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
                 .ToList();
             if (fromCli.Count > 0) return fromCli;
         }

         return apiModels!
             .Where(m => m.Contains("argon", StringComparison.OrdinalIgnoreCase) &&
                         !m.Contains("disabled", StringComparison.OrdinalIgnoreCase))
             .OrderBy(m => m.Contains("low", StringComparison.OrdinalIgnoreCase) ? 0 : (m.Contains("medium", StringComparison.OrdinalIgnoreCase) ? 1 : 2))
             .ToList();
     }
 
     // 思考档位越便宜排越前：Low &lt; Medium &lt; High/Thinking
     static int EffortRank(string m)
     {
         if (m.Contains("(Low)", StringComparison.OrdinalIgnoreCase)) return 0;
         if (m.Contains("(Medium)", StringComparison.OrdinalIgnoreCase)) return 1;
         if (m.Contains("(High)", StringComparison.OrdinalIgnoreCase)) return 2;
         if (m.Contains("(Thinking)", StringComparison.OrdinalIgnoreCase)) return 1;
         return 3;
     }
 
     // 家族便宜程度：Gemini 池 Flash&lt;Pro；3P 池 Sonnet&lt;GPT&lt;Opus
     static int PoolRank(string m)
     {
         if (m.Contains("Flash", StringComparison.OrdinalIgnoreCase)) return 0;
         if (m.Contains("Sonnet", StringComparison.OrdinalIgnoreCase)) return 0;
         if (m.Contains("GPT", StringComparison.OrdinalIgnoreCase)) return 1;
         if (m.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return 1;
         if (m.Contains("Opus", StringComparison.OrdinalIgnoreCase)) return 2;
         return 3;
     }
 
     static double ModelNumber(string m)
     {
         var match = System.Text.RegularExpressions.Regex.Match(m, @"(\d+(?:\.\d+)?)");
         if (match.Success && double.TryParse(match.Groups[1].Value,
                 System.Globalization.NumberStyles.Float,
                 System.Globalization.CultureInfo.InvariantCulture, out var v))
             return v;
         return 0;
     }
 
     static async Task<Attempt> RunOnceAsync(string cli, string? model, CancellationToken ct)
     {
         // 一次性空目录：CLI 不会扫描用户项目，也不会留下工作区记录
         var workDir = Path.Combine(Path.GetTempPath(), "apiswitch-ag-activate-" + Guid.NewGuid().ToString("N")[..8]);
         Directory.CreateDirectory(workDir);
         try
         {
             Attempt? last = null;
             var sets = BuildArgSets(model);
             // 从上次证明可用的参数组开始，避免每个模型都把不支持的老参数重试一遍（那样会一直卡）
             for (int i = PreferredArgSet; i < sets.Count; i++)
             {
                 var r = await RunAttemptAsync(cli, workDir, sets[i], model, ct);
                 if (r.Ok)
                 {
                     PreferredArgSet = i;
                     return r;
                 }
                 last = r;
                 // 模型不被接受时不要在这层继续降级参数（换模型更有意义），交给上层换下一个候选；
                 // 参数用法错误才降级重试；429 / 鉴权 / 网络类错误直接返回真实原因。
                 if (r.ModelInvalid && !r.UsageError) return r;
                 if (!r.UsageError) return r;
             }
             return last ?? new Attempt(false, false, false, false, I18nService.T("AgQ.CliFailed"));
         }
         catch (OperationCanceledException)
         {
             throw;
         }
         catch (Exception ex)
         {
             return new Attempt(false, false, false, false, ex.Message);
         }
         finally
         {
             try { Directory.Delete(workDir, true); } catch { }
         }
     }
 
     /// <summary>单次尝试的结果分类。</summary>
     readonly record struct Attempt(
         bool Ok, bool RateLimited, bool UsageError, bool ModelInvalid, string Message, string Raw = "");
 
     /// <summary>单次尝试：全程后台隐藏执行，绝不在前台弹出任何控制台窗口。</summary>
     static async Task<Attempt> RunAttemptAsync(
         string cli, string workDir, List<string> args, string? model, CancellationToken ct)
     {
         var psi = new ProcessStartInfo
         {
             FileName = cli,
             WorkingDirectory = workDir,
             RedirectStandardOutput = true,
             RedirectStandardError = true,
             RedirectStandardInput = true,
             UseShellExecute = false,
             // 关键：GUI 进程启动控制台程序时必须显式 CREATE_NO_WINDOW，
             // 否则 Windows 会为子进程新建一个可见的控制台窗口（用户看到的"命令窗口"）。
             CreateNoWindow = true,
             WindowStyle = ProcessWindowStyle.Hidden,
             StandardOutputEncoding = Encoding.UTF8,
             StandardErrorEncoding = Encoding.UTF8,
         };
         foreach (var a in args) psi.ArgumentList.Add(a);
 
         using var proc = new Process { StartInfo = psi };
         proc.Start();
 
         // 非交互保障：立刻关闭 stdin。CLI 若弹出"是否信任此目录"之类的提问，
         // 读到 EOF 就会按默认值继续，绝不会挂起等待用户输入。
         try
         {
             proc.StandardInput.Write("\n");
             proc.StandardInput.Flush();
             proc.StandardInput.Close();
         }
         catch { }
 
         var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
         var stderrTask = proc.StandardError.ReadToEndAsync(ct);
 
         using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
         cts.CancelAfter(TimeSpan.FromSeconds(AttemptTimeoutSeconds));
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
 
         App.LogStartup(
             $"AgCli: model={model} exit={proc.ExitCode} args=[{string.Join(" ", args)}] " +
             $"out={stdout.Length}B err={stderr.Length}B head={(stderr.Length > 0 ? stderr : stdout).Replace("\n", " | ")[..Math.Min(200, (stderr.Length > 0 ? stderr : stdout).Length)]}");
 
         // CLI 用 --output-format json 时，失败也常常是 exit=0 + JSON {"status":"ERROR","error":"..."}。
         // 只看退出码会把这种失败误判成成功，必须解析 JSON 里的 error 字段。
         var jsonError = ExtractJsonError(stdout);
         var combined = stdout + "\n" + stderr + (jsonError != null ? "\n" + jsonError : "");
         if (proc.ExitCode == 0 && jsonError == null && !string.IsNullOrWhiteSpace(stdout))
             return new Attempt(true, false, false, false, "", combined);
         bool rl = combined.Contains("429", StringComparison.Ordinal)
                || combined.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("quota", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
         bool modelInvalid = IsModelError(combined);
         bool usage = IsUsageError(proc.ExitCode, combined);
         return new Attempt(false, rl, usage, modelInvalid, SummarizeError(combined, proc.ExitCode), combined);
     }
 
     /// <summary>
     /// 从 --output-format json 的输出里提取错误。CLI 失败时也可能退出码为 0，
     /// 只把错误放在 JSON 的 status/error 字段里。
     /// </summary>
     internal static string? ExtractJsonError(string stdout)
     {
         var text = stdout.Trim();
         if (text.Length == 0 || text[0] != '{') return null;
         try
         {
             using var doc = JsonDocument.Parse(text);
             var root = doc.RootElement;
             if (root.ValueKind != JsonValueKind.Object) return null;
 
             if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                 !string.IsNullOrWhiteSpace(err.GetString()))
                 return err.GetString()!;
 
             if (root.TryGetProperty("status", out var st) &&
                 st.ValueKind == JsonValueKind.String &&
                 string.Equals(st.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                 return "status=ERROR";
 
             return null;
         }
         catch
         {
             // 输出不是完整 JSON（被截断/混入日志行），忽略
             return null;
         }
     }
 
     /// <summary>判断是否为"这个模型 CLI 不接受"——值得换下一个候选模型重试。</summary>
     internal static bool IsModelError(string combined)
     {
         return combined.Contains("invalid model", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("model selection", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("unknown model", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("unsupported model", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("model not found", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("no such model", StringComparison.OrdinalIgnoreCase);
     }
 
     /// <summary>判断是否为"参数用法错误"（值得降级重试），而非配额/网络类错误。</summary>
     internal static bool IsUsageError(int exitCode, string combined)
     {
         if (exitCode == 2) return true;
         return combined.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("invalid option", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("is not supported", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("not supported", StringComparison.OrdinalIgnoreCase)
             || combined.Contains("Usage:", StringComparison.OrdinalIgnoreCase);
     }
 
     static string SummarizeError(string combined, int exitCode)
     {
         var fallback = I18nService.T("AgQ.CliFailed") + $" (exit={exitCode})";
         if (string.IsNullOrWhiteSpace(combined)) return fallback;
 
         var lines = combined.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
         if (lines.Count == 0) return fallback;
 
         // 优先返回真正说明原因的行；CLI 的结构化日志（I2xxx/E2xxx 前缀）里就藏着真实错误，
         // 以前被无条件跳过，导致用户只看到一句"激活失败"。
         foreach (var kw in new[]
         {
             "error", "failed", "failure", "429", "quota", "exhaust", "permission", "denied",
             "not found", "unauthor", "timed out", "timeout", "refused", "econn"
         })
         {
             var hit = lines.FirstOrDefault(l => l.Contains(kw, StringComparison.OrdinalIgnoreCase));
             if (hit != null) return Truncate(hit);
         }
         return Truncate(lines[0]);
     }
 
     static string Truncate(string s) => s.Length > 220 ? s[..220] + "…" : s;

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
