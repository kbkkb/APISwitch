using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow
{
    // ---------- Antigravity ----------

    async Task RefreshAntigravityAsync()
    {
        var db = AgPaths.FindStateDb();
        DbPathText.Text = db ?? I18nService.T("Ag.DbNotFound");

        string? email = null, plan = null, state = null, dbError = null;
        bool running = false;
        List<Profile> loaded = new();

        // 进程枚举、SQLite 读取、账号文件加载都比较重，放到后台线程执行，避免 UI 卡顿
        await Task.Run(() =>
        {
            // 0. Prioritize syncing active OAuth session back from Antigravity 2.x Hub (Windows Credential Manager: gemini:antigravity)
            try
            {
                var sysEmail = AgToolsService.CaptureCurrentActiveCredentialsToStore();
                if (!string.IsNullOrEmpty(sysEmail))
                {
                    email = sysEmail;
                    state = "signedIn";
                }
            }
            catch { }

            // 1. Fallback to ~/.gemini/oauth_creds.json
            if (string.IsNullOrEmpty(email))
            {
                try
                {
                    if (File.Exists(AgToolsService.GeminiCredsPath))
                    {
                        var credsText = File.ReadAllText(AgToolsService.GeminiCredsPath);
                        using var doc = JsonDocument.Parse(credsText);
                        if (doc.RootElement.TryGetProperty("id_token", out var idElem))
                        {
                            var idToken = idElem.GetString();
                            if (!string.IsNullOrEmpty(idToken))
                            {
                                var parts = idToken.Split('.');
                                if (parts.Length >= 2)
                                {
                                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                                    switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                                    using var pDoc = JsonDocument.Parse(Convert.FromBase64String(payload));
                                    if (pDoc.RootElement.TryGetProperty("email", out var emElem))
                                    {
                                        email = emElem.GetString();
                                        state = "signedIn";
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 2. Fallback to state.vscdb
            if (db != null)
            {
                try
                {
                    var auth = AgDb.ReadAuth(db);
                    auth.TryGetValue(AgState.KeyUserStatus, out var us);
                    auth.TryGetValue(AgState.KeyOauthToken, out var ot);
                    var dbEmail = AgState.ExtractEmail(us);
                    if (string.IsNullOrEmpty(email))
                    {
                        email = dbEmail;
                        state = AgState.ExtractAuthState(ot, email);
                    }
                    plan = AgState.ExtractPlan(us);
                }
                catch (Exception ex)
                {
                    dbError = ex.Message;
                }
            }

            running = AgProcess.IsRunning();
            loaded = ProfileStore.Load();
        });

        if (email == null && dbError != null)
            DbPathText.Text = I18nService.F("Msg.DbReadFailFmt", db ?? "", dbError);

        CurrentAccountText.Text = email ?? I18nService.T("Ag.NoAccount");
        StateChip.Text = state switch
        {
            "signedIn" => I18nService.T("Ag.SignedIn"),
            "signedOut" => I18nService.T("Ag.SignedOut"),
            null => I18nService.T("Ag.StateUnknown"),
            _ => state,
        };
        PlanChip.Text = string.IsNullOrEmpty(plan) ? "—" : plan;

        IdeChip.Text = running ? I18nService.T("Ag.IdeRunning") : I18nService.T("Ag.IdeNotRunning");
        IdeChip.Foreground = running
            ? (System.Windows.Media.Brush)FindResource("GreenBrush")
            : (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        // If no profiles loaded yet and Antigravity Tools is available, auto import
        if (loaded.Count == 0 && AgToolsService.IsInstalled())
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    var imported = await AgToolsService.ImportAccountsAsync();
                    if (imported.Count > 0)
                    {
                        Dispatcher.Invoke(() => { _ = RefreshAntigravityAsync(); });
                    }
                });
            }
            catch { }
        }

        var existingMap = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldP in _profiles)
        {
            var oldKey = (!string.IsNullOrEmpty(oldP.Email) ? oldP.Email : oldP.Name).Trim();
            if (!string.IsNullOrEmpty(oldKey) && !existingMap.ContainsKey(oldKey))
                existingMap[oldKey] = oldP;
        }

        var nextList = new List<Profile>(loaded.Count);
        var seenAg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in loaded)
        {
            var key = (!string.IsNullOrEmpty(p.Email) ? p.Email : p.Name).Trim();
            if (!string.IsNullOrEmpty(key) && seenAg.Add(key))
            {
                p.IsCurrent = email != null && email.Equals(p.Email, StringComparison.OrdinalIgnoreCase);
                if (existingMap.TryGetValue(key, out var existing))
                {
                    existing.UpdateFrom(p);
                    nextList.Add(existing);
                }
                else
                {
                    nextList.Add(p);
                }
            }
        }

        bool sameOrder = nextList.Count == _profiles.Count &&
                         !nextList.Where((t, idx) => !ReferenceEquals(t, _profiles[idx])).Any();
        if (!sameOrder)
        {
            _profiles.Clear();
            foreach (var item in nextList)
                _profiles.Add(item);
        }

        if (ProfileList.ItemsSource != _profiles)
        {
            ProfileList.ItemsSource = _profiles;
        }
        var activeProfile = _profiles.FirstOrDefault(x => x.IsCurrent);
        if (activeProfile != null && (string.IsNullOrEmpty(plan) || plan == "—"))
        {
            PlanChip.Text = activeProfile.TierDisplay;
        }
        if (AccountCountChipText != null)
        {
            AccountCountChipText.Text = I18nService.F("Ag.AccountCountFmt", _profiles.Count);
        }
        AgEmpty.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifyFloatingWidgetProfilesUpdated();
    }

    void OnToggleProfileModels(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Profile target)
        {
            target.IsModelsExpanded = !target.IsModelsExpanded;
        }
    }

    async void OnImportFromAgTools(object sender, RoutedEventArgs e)
    {
        SetBusy(I18nService.T("Msg.AgImporting"));
        try
        {
            var imported = await AgToolsService.ImportAccountsAsync();
            ClearBusy();
            await RefreshAntigravityAsync();
            if (imported.Count > 0)
                ShowToast(I18nService.F("Msg.AgImportedFmt", imported.Count));
            else
                ShowToast(I18nService.T("Msg.AgImportNone"), isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgImportFailFmt", ex.Message), isError: true);
        }
    }

    async void OnNewAccountLogin(object sender, RoutedEventArgs e)
    {
        SetBusy(I18nService.T("Msg.AgLoginBusy"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var profile = await AgAuthFlow.StartGoogleLoginAsync(cts.Token);
            ClearBusy();
            await RefreshAntigravityAsync();
            ShowToast(I18nService.F("Msg.AgLoginOkFmt", profile.Email));
        }
        catch (OperationCanceledException)
        {
            ClearBusy();
            ShowToast(I18nService.T("Msg.AgLoginCanceled"), isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgLoginFailFmt", ex.Message), isError: true);
        }
    }

    async void OnCardActivateQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (target.IsActivating) return; // 防抖：激活中忽略重复点击
        if (!EnsureAgCliAvailable()) return;

        target.IsActivating = true;
        try
        {
            SetBusy(I18nService.F("Msg.AgActivatingFmt", target.Email));
            var (ok, latencyMs, msg, rateLimited, skippedAll) = await AgQuotaService.ActivateAccountQuotaDetailedAsync(target, forcePreRefresh: true);
            ClearBusy();
            await RefreshAntigravityAsync();
            if (skippedAll)
            {
                ShowToast(I18nService.F("Msg.AgAlreadyActiveFmt", target.Email));
            }
            else if (ok)
            {
                ShowToast(I18nService.F("Msg.AgActivatedFmt", target.Email, latencyMs));
            }
            else if (rateLimited)
            {
                // 429 用尽不是“失败”：提示重置时间，不用错误样式
                var reset = target.QuotaExhaustedResetAt.HasValue
                    ? target.QuotaExhaustedResetAt.Value.ToLocalTime().ToString("HH:mm")
                    : "—";
                ShowToast(I18nService.F("Msg.AgQuotaExhaustedFmt", target.Email, reset), isInfo: true);
            }
            else
            {
                ShowToast(I18nService.F("Msg.AgActivateFailFmt", msg), isError: true);
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgActivateErrorFmt", ex.Message), isError: true);
        }
        finally
        {
            target.IsActivating = false;
        }
    }

    /// <summary>
    /// 激活前检查本地 Antigravity CLI 是否可用。
    /// 不可用时弹出可操作提示（而非静默失败），返回 false 中止激活。
    /// </summary>
    bool EnsureAgCliAvailable()
    {
        if (Services.AgCliService.IsAvailable()) return true;
        Warn(I18nService.T("AgQ.CliMissingHint"));
        return false;
    }

    async void OnBatchActivateAntigravity(object sender, RoutedEventArgs e)
    {
        if (!EnsureAgCliAvailable()) return;
        if (_profiles.Count == 0)
        {
            ShowToast(I18nService.T("Msg.AgBatchEmpty"), isError: true);
            return;
        }

        var targets = _profiles.ToList();
        var count = targets.Count;
        int successCount = 0;
        int skippedCount = 0;
        int failCount = 0;
        bool canceled = false;

        _batchCts?.Cancel();
        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;

        // 取消入口必须放在遮罩内部：遮罩盖住整个窗口，工具栏上的按钮会被挡住点不到
        BusyCancelBtn.Visibility = Visibility.Visible;

        try
        {
            // 1. 先并行刷新超过 45s 未同步的账号配额，以便精准跳过已激活的配额池
            var staleTargets = targets.Where(p =>
                !p.QuotaUpdatedAt.HasValue || (DateTime.UtcNow - p.QuotaUpdatedAt.Value).TotalSeconds > 45).ToList();
            if (staleTargets.Count > 0)
            {
                SetBusy(I18nService.F("Msg.AgBatchPrecheckFmt", staleTargets.Count));
                using var precheckSem = new SemaphoreSlim(4);
                var precheckTasks = staleTargets.Select(async p =>
                {
                    await precheckSem.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        await AgQuotaService.RefreshQuotaAsync(p);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    finally { precheckSem.Release(); }
                });
                await Task.WhenAll(precheckTasks);
                Dispatcher.Invoke(() =>
                {
                    foreach (var x in targets) x.NotifyQuotaUiChanged();
                    NotifyFloatingWidgetProfilesUpdated();
                });
            }

            // 2. 逐号按需激活未开始计时的池；全部池均已在计时中的账号直接跳过（零 CLI 延迟）
            for (int i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var p = targets[i];
                var plan = p.EvaluatePoolActivationNeed();
                if (!plan.NeedsAnyCliCall)
                {
                    p.IsActivated = true;
                    p.ActivationError = null;
                    p.QuotaExhausted = false;
                    p.QuotaExhaustedResetAt = null;
                    ProfileStore.Save(p);
                    p.NotifyQuotaUiChanged();
                    skippedCount++;
                    continue;
                }

                bool didCliCall = false;
                p.IsActivating = true;
                SetBusy(I18nService.F("Msg.AgBatchProgressPoolsFmt", i + 1, count, p.Email, plan.DescribePendingPools()));

                try
                {
                    var (ok, latencyMs, msg, rateLimited, skippedAll) = await AgQuotaService.ActivateAccountQuotaDetailedAsync(p, ct, forcePreRefresh: false);
                    didCliCall = !skippedAll;
                    if (skippedAll) skippedCount++;
                    else if (ok) successCount++;
                    else failCount++;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failCount++;
                    p.IsActivated = false;
                    p.ActivationError = ex.Message;
                    ProfileStore.Save(p);
                }
                finally
                {
                    p.IsActivating = false;
                    p.NotifyQuotaUiChanged();
                }

                // 只有刚真实发过 CLI 请求且后面还有需要 CLI 激活的账号时，才保留 2~4s 自然间隔以防同 IP 并发关联
                bool hasPendingAfter = targets.Skip(i + 1).Any(x => x.EvaluatePoolActivationNeed().NeedsAnyCliCall);
                if (didCliCall && hasPendingAfter)
                {
                    var delay = Random.Shared.Next(2000, 4000);
                    SetBusy(I18nService.F("Msg.AgBatchCooldownFmt", i + 1, count, delay / 1000.0));
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { canceled = true; break; }
                }
            }
        }
        finally
        {
            // 关键：无论循环是否抛异常/被取消，都必须收起忙碌遮罩并恢复按钮，
            // 否则遮罩会把界面永久卡死、取消按钮也会一直挂着。
            ClearBusy();
            BusyCancelBtn.Visibility = Visibility.Collapsed;
            _batchCts?.Dispose();
            _batchCts = null;
        }

        await RefreshAntigravityAsync();

        if (canceled)
            ShowToast(I18nService.F("Msg.AgBatchCanceledFmt", successCount + skippedCount, failCount, count - successCount - skippedCount - failCount));
        else if (skippedCount > 0)
            ShowToast(I18nService.F("Msg.AgBatchDoneSkipFmt", successCount, skippedCount, failCount));
        else
            ShowToast(I18nService.F("Msg.AgBatchDoneFmt", successCount, failCount));
    }

    void OnBatchCancelAntigravity(object sender, RoutedEventArgs e)
    {
        if (_batchCts == null || _batchCts.IsCancellationRequested) return;
        _batchCts.Cancel();
        SetBusy(I18nService.T("Msg.AgBatchCanceling"));
        ShowToast(I18nService.T("Msg.AgBatchCancelRequested"));
    }

    async void OnCardRefreshQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (target.IsRefreshingQuota) return;
        target.IsRefreshingQuota = true;
        try
        {
            var ok = await AgQuotaService.RefreshQuotaAsync(target);
            Dispatcher.Invoke(() =>
            {
                target.NotifyQuotaUiChanged();
                NotifyFloatingWidgetProfilesUpdated();
            });
            if (ok)
            {
                if (target.IsProTier)
                    ShowToast(I18nService.F("Msg.AgRefreshQuotaProFmt", target.Email));
                else
                    ShowToast(I18nService.F("Msg.AgRefreshQuotaFmt", target.Email));
            }
            else
            {
                ShowToast(I18nService.F("Msg.AgRefreshQuotaFailFmt", target.Email), isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.AgRefreshQuotaErrorFmt", ex.Message), isError: true);
        }
        finally
        {
            target.IsRefreshingQuota = false;
        }
    }

    void OnRowSwitchAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Profile p) _ = SwitchToProfile(p);
    }

    async Task SwitchToProfile(Profile target)
    {
        bool autoRestart = AutoRestartCheck.IsChecked == true;
        SetBusy(I18nService.F("Msg.AgSwitchingFmt", target.Email));
        try
        {
            var stopped = await Task.Run(() => AgProcess.StopIdeAsync(LogAg));
            if (!stopped)
            {
                ClearBusy();
                ShowToast(I18nService.T("Msg.AgSwitchIdeCloseFail"), isError: true);
                return;
            }

            // 0. Capture outgoing session credentials from Credential Manager before overwriting them,
            //    and sync fresh tokens into target if it was the active session re-authenticated inside IDE.
            await Task.Run(() =>
            {
                AgToolsService.CaptureCurrentActiveCredentialsToStore();
                if (!string.IsNullOrWhiteSpace(target.Email))
                {
                    var latest = ProfileStore.Load().FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(x.Email) &&
                        x.Email.Equals(target.Email, StringComparison.OrdinalIgnoreCase));
                    if (latest != null)
                    {
                        if (!string.IsNullOrEmpty(latest.RefreshToken)) target.RefreshToken = latest.RefreshToken;
                        if (!string.IsNullOrEmpty(latest.AccessToken)) target.AccessToken = latest.AccessToken;
                        if (!string.IsNullOrEmpty(latest.IdToken)) target.IdToken = latest.IdToken;
                        if (latest.ExpiryTimestamp.HasValue) target.ExpiryTimestamp = latest.ExpiryTimestamp;
                    }
                }
            });

            // 1. Refresh & verify target credentials before writing system credentials or launching IDE
            var (credOk, authExpired, credErr) = await AgToolsService.TryApplySystemCredentialsAsync(
                target, forceTokenRefresh: true, syncIdeMeta: true);
            if (!credOk)
            {
                ClearBusy();
                ShowToast(
                    authExpired
                        ? I18nService.F("Msg.AgSwitchTokenExpiredFmt", target.Email, credErr ?? "")
                        : I18nService.F("Msg.AgSwitchFailFmt", credErr ?? ""),
                    isError: true);
                return;
            }

            // 2. Inject or restore full authentication session across all state.vscdb locations
            var dbs = AgPaths.FindAllStateDbs();
            var authValues = AgAuthHelper.BuildAuthValues(target);
            if (authValues.Count > 0)
            {
                target.Values[AgState.KeyOauthToken] = authValues[AgState.KeyOauthToken];
                target.Values[AgState.KeyUserStatus] = authValues[AgState.KeyUserStatus];
                ProfileStore.Save(target);
            }

            if (dbs.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var dbPath in dbs)
                    {
                        try
                        {
                            if (target.Values != null && target.Values.Count > 0)
                                AgDb.WriteAuth(dbPath, target.Values);
                            else
                                AgDb.ClearAuth(dbPath);
                        }
                        catch { }
                    }
                });
            }

            // 3. Avoid concurrent cloudcode-pa storm while IDE language_server is booting;
            //    only trigger immediate background handshake when not auto-restarting IDE.
            if (!autoRestart)
            {
                _ = Task.Run(async () =>
                {
                    try { await AgQuotaService.ActivateProfileAsync(target); }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgSwitchFailFmt", ex.Message), isError: true);
            return;
        }

        if (autoRestart)
        {
            // 启动后必须验证主窗口真的出现了：被单例锁挡掉会静默退出，
            // 以前不验证，白屏了也提示"已重启"。
            var started = await Task.Run(() => AgProcess.StartIdeAndWaitAsync(LogAg));
            if (!started)
            {
                ClearBusy();
                ShowToast(I18nService.T("Msg.AgIdeStartVerifyFail"), isError: true);
                return;
            }

            // Give language_server ~8s to finish initial get_auth_status uncontested, then refresh quota silently
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(8000);
                    await AgQuotaService.RefreshQuotaAsync(target);
                    Dispatcher.Invoke(() =>
                    {
                        target.NotifyQuotaUiChanged();
                        NotifyFloatingWidgetProfilesUpdated();
                    });
                }
                catch { }
            });
        }

        ClearBusy();
        await RefreshAntigravityAsync();
        ShowToast((autoRestart ? I18nService.F("Msg.AgSwitchedRestartFmt", target.Email) : I18nService.F("Msg.AgSwitchedFmt", target.Email)));
    }

    /// <summary>反重力 IDE 重启过程的日志（排查白屏用）。</summary>
    static void LogAg(string msg) => App.LogStartup("AgRestart: " + msg);

    void OnCardDeleteAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (!Confirm(I18nService.F("Common.DeleteArchiveConfirmFmt", target.Email))) return;
        try { ProfileStore.Delete(target); }
        catch (Exception ex) { Warn(I18nService.F("Common.DeleteFailFmt", ex.Message)); return; }
        _ = RefreshAntigravityAsync();
    }

    async Task RefreshAllAgQuotasAsync(bool silent, CancellationToken ct = default, bool activeOnly = false)
    {
        if (_isRefreshingAgQuotas) return;
        _isRefreshingAgQuotas = true;

        try
        {
            // 先确保账号元数据与当前活动态加载完毕，再执行配额并发查询，杜绝并发加载与存盘导致的实例竞争
            await RefreshAntigravityAsync();

            if (_profiles.Count == 0)
            {
                if (!silent) ShowToast(I18nService.T("Msg.AgRefreshNone"));
                return;
            }

            List<Profile> targets;
            if (!silent)
            {
                targets = _profiles.ToList();
            }
            else
            {
                // 后台静默轮询降频防风控：优先刷新当前活跃账号；闲置账号前台 ≥15 分钟 / 后台 ≥30 分钟同步一次，消除同频齐刷特征
                var nowUtc = DateTime.UtcNow;
                var idleInterval = activeOnly ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(15);
                targets = _profiles.Where(p =>
                {
                    if (!p.QuotaUpdatedAt.HasValue) return true;
                    var age = nowUtc - p.QuotaUpdatedAt.Value;
                    return p.IsCurrent ? age >= TimeSpan.FromSeconds(50) : age >= idleInterval;
                }).ToList();

                if (targets.Count == 0)
                {
                    return;
                }
            }

            using var semaphore = new SemaphoreSlim(silent ? 2 : 4);
            bool staggered = silent && targets.Count > 1;

            var tasks = targets.Select(async (p, idx) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (staggered && idx > 0)
                        await Task.Delay(Random.Shared.Next(150, 450), ct);
                    await AgQuotaService.RefreshQuotaAsync(p);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 单账号异常不中断整体批量刷新
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            _lastAgQuotaRefreshUtc = DateTime.UtcNow;

            await RefreshAntigravityAsync();

            Dispatcher.Invoke(() =>
            {
                foreach (var p in _profiles) p.NotifyQuotaUiChanged();
                NotifyFloatingWidgetProfilesUpdated();
                if (!silent)
                {
                    ShowToast(I18nService.F("Msg.AgRefreshAllDoneFmt", _profiles.Count));
                }
            });
        }
        catch (OperationCanceledException)
        {
            // 取消：静默结束，界面状态由调用方收尾
        }
        finally
        {
            _isRefreshingAgQuotas = false;
        }
    }

    async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAllAgQuotasAsync(silent: false);

    void OnLaunchIde(object sender, RoutedEventArgs e)
    {
        try { AgProcess.StartIde(); }
        catch (Exception ex) { Warn(I18nService.F("Common.LaunchIdeFailFmt", ex.Message)); }
    }

}
