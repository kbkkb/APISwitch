using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using APISwitch.Models;
using APISwitch.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace APISwitch.Dialogs;

public partial class AgQuotaInsightsDialog : Window
{
    private static AgQuotaInsightsDialog? _activeInstance;

    private List<Profile> _profiles = new();
    private Profile? _selectedProfile;
    private string _currentIdeEmail = "";
    private int _monthOffset = 0;

    private double _gem5hFrac, _gemWkFrac, _p35hFrac, _p3WkFrac;
    private double _allocUsedFrac, _allocWasteFrac, _allocRemainFrac;
    private List<ResetHistoryRowVM> _allAccountResetRows = new();
    private bool _autoSwitchedEmptyWeekly;
    private int _renderedAnchorDay = -1;
    private int _refreshSeq;
    internal Task ReadyTask { get; private set; } = Task.CompletedTask;

    public event Action? DataChanged;

    /// <summary>
    /// 以单实例非模态独立窗口方式打开或前置「反重力额度与损耗透视」，
    /// 绝不调用 ShowDialog() 阻塞主窗口或禁用桌面悬浮窗点击响应。
    /// </summary>
    public static AgQuotaInsightsDialog ShowOrActivate(
        string? initialEmail = null,
        Window? centerNear = null,
        Action? onDataChanged = null)
    {
        if (_activeInstance is { IsLoaded: true } existing)
        {
            if (onDataChanged != null)
            {
                existing.DataChanged -= onDataChanged;
                existing.DataChanged += onDataChanged;
            }
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            if (!string.IsNullOrWhiteSpace(initialEmail))
                existing.FocusAccount(initialEmail);
            existing.Show();
            existing.Activate();
            return existing;
        }

        var dlg = new AgQuotaInsightsDialog(initialEmail);
        _activeInstance = dlg;
        if (onDataChanged != null)
            dlg.DataChanged += onDataChanged;
        PositionNearWindow(dlg, centerNear);
        dlg.Show();
        dlg.Activate();
        return dlg;
    }

    private static void PositionNearWindow(Window target, Window? anchor)
    {
        if (anchor is not { IsVisible: true } || anchor.WindowState == WindowState.Minimized)
            return;
        double aw = anchor.ActualWidth > 0 ? anchor.ActualWidth : anchor.Width;
        double ah = anchor.ActualHeight > 0 ? anchor.ActualHeight : anchor.Height;
        if (double.IsNaN(aw) || double.IsNaN(ah) || aw <= 0 || ah <= 0) return;

        var wa = SystemParameters.WorkArea;
        double tw = target.Width;
        double th = target.Height;
        double left = anchor.Left + (aw - tw) / 2.0;
        double top = anchor.Top + (ah - th) / 2.0;
        target.WindowStartupLocation = WindowStartupLocation.Manual;
        target.Left = Math.Clamp(left, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - tw - 8));
        target.Top = Math.Clamp(top, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - th - 8));
    }

    public void FocusAccount(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        var hit = _profiles.FirstOrDefault(p =>
            string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));
        if (hit != null && !ReferenceEquals(_selectedProfile, hit))
        {
            _selectedProfile = hit;
            _monthOffset = 0;
            RefreshReportView();
        }
    }

    public AgQuotaInsightsDialog(string? initialEmail = null)
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;
        };
        SizeChanged += (_, _) => UpdateBarWidths();
        StateChanged += (_, _) =>
        {
            bool isMax = WindowState == WindowState.Maximized;
            MaxRestoreBtn.Tag = FindResource(isMax ? "IconRestore" : "IconMax");
            RootFrame.CornerRadius = isMax ? new CornerRadius(0) : new CornerRadius(12);
            RootFrame.BorderThickness = isMax ? new Thickness(0) : new Thickness(1);
        };

        LoadAccountsFast(initialEmail);
        RenderSidebarOnly();
        if (_selectedProfile != null)
            HeaderEmailText.Text = string.IsNullOrWhiteSpace(_selectedProfile.Email) ? _selectedProfile.Name : _selectedProfile.Email;
        ReadyTask = WarmAndRefreshAsync(initialEmail);
    }

    private async Task WarmAndRefreshAsync(string? preferredEmail)
    {
        try
        {
            if (!AgQuotaAnalyticsService.IsSnapshotWarm)
            {
                var snapProfiles = _profiles.ToList();
                await Task.Run(() =>
                {
                    UsageScanner.ScanAntigravityQuick();
                    AgQuotaAnalyticsService.WarmAccountCapCache(snapProfiles);
                });
                MergeKnownHistoricalEmails(preferredEmail);
            }
            await RefreshReportViewAsync();
        }
        catch { }
    }

    private void LoadAccountsFast(string? preferredEmail)
    {
        _profiles = ProfileStore.Load()
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .ToList();

        // 零锁极速读取当前登录邮箱（0.05ms 读 JSON，不再同步打开 IDE 的巨型 state.vscdb）
        _currentIdeEmail = AgHookService.ReadCurrentAntigravityEmailFast();

        MergeKnownHistoricalEmails(preferredEmail);

        if (_profiles.Count == 0)
        {
            _profiles.Add(new Profile
            {
                Name = string.IsNullOrWhiteSpace(_currentIdeEmail) ? "Antigravity" : _currentIdeEmail,
                Email = _currentIdeEmail
            });
        }

        SelectInitialProfile(preferredEmail);
    }

    private void MergeKnownHistoricalEmails(string? preferredEmail)
    {
        foreach (var knownEmail in AgQuotaAnalyticsService.GetCachedKnownEmails())
        {
            if (string.IsNullOrWhiteSpace(knownEmail)) continue;
            if (_profiles.Any(p => string.Equals(p.Email, knownEmail, StringComparison.OrdinalIgnoreCase)))
                continue;
            _profiles.Add(new Profile { Name = knownEmail, Email = knownEmail });
        }
        if (_selectedProfile == null || !string.IsNullOrWhiteSpace(preferredEmail))
            SelectInitialProfile(preferredEmail);
    }

    private void SelectInitialProfile(string? preferredEmail)
    {
        if (!string.IsNullOrWhiteSpace(preferredEmail))
        {
            var hit = _profiles.FirstOrDefault(p =>
                string.Equals(p.Email, preferredEmail, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { _selectedProfile = hit; return; }
        }

        if (_selectedProfile == null && !string.IsNullOrWhiteSpace(_currentIdeEmail))
        {
            _selectedProfile = _profiles.FirstOrDefault(p =>
                string.Equals(p.Email, _currentIdeEmail, StringComparison.OrdinalIgnoreCase));
        }

        if (_selectedProfile == null && _profiles.Count > 0)
            _selectedProfile = _profiles[0];
    }

    private void RenderSidebarOnly()
    {
        SidebarCountText.Text = _profiles.Count.ToString(CultureInfo.InvariantCulture);
        AccountNavList.ItemsSource = _profiles.Select(p =>
        {
            bool sel = _selectedProfile != null &&
                       (ReferenceEquals(p, _selectedProfile) ||
                        string.Equals(p.Email, _selectedProfile.Email, StringComparison.OrdinalIgnoreCase));
            bool isCur = !string.IsNullOrWhiteSpace(_currentIdeEmail) &&
                         string.Equals(p.Email, _currentIdeEmail, StringComparison.OrdinalIgnoreCase);

            string email = string.IsNullOrWhiteSpace(p.Email) ? p.Name : p.Email;
            string shortName = email.Contains('@') ? email.Split('@')[0] : email;
            string initial = string.IsNullOrEmpty(shortName) ? "A" : shortName.Substring(0, 1).ToUpperInvariant();
            var (tierFg, _) = ResolveTierColors(p.TierDisplay);

            double? wk = p.QuotaWeeklyFraction;
            double wkFrac = Math.Clamp(wk ?? 0.0, 0.0, 1.0);
            string wkText = wk.HasValue ? $"{wkFrac * 100.0:F0}%" : "";
            Brush wkBrush = !wk.HasValue
                ? MakeBrush(0x94, 0xA3, 0xB8)
                : wkFrac <= 0.15 ? MakeBrush(0xDC, 0x26, 0x26)
                : wkFrac <= 0.40 ? MakeBrush(0xD9, 0x77, 0x06)
                : MakeBrush(0x05, 0x96, 0x69);

            string wkUsdShort = "";
            if (wk.HasValue && AgQuotaAnalyticsService.IsSnapshotWarm &&
                AgQuotaAnalyticsService.TryGetCardPoolUsd(
                    p, AgQuotaAnalyticsService.BucketGeminiWeekly, wk, out var remUsd, out _, out _, out _))
            {
                wkUsdShort = $"≈ {UsagePricingService.FormatUsd(remUsd)}";
            }

            return new AccountNavItemVM
            {
                ProfileRef = p,
                Initial = initial,
                ShortName = shortName,
                TierText = string.IsNullOrEmpty(p.TierDisplay) ? email : p.TierDisplay,
                TierFg = tierFg,
                IsSelected = sel,
                IsCurrent = isCur,
                CardBg = sel ? MakeBrush(0xF5, 0xF7, 0xFF) : MakeBrush(0xFF, 0xFF, 0xFF),
                CardBorder = sel ? MakeBrush(0x63, 0x66, 0xF1) : MakeBrush(0xEC, 0xF0, 0xF6),
                AvatarBg = sel ? MakeBrush(0x4F, 0x46, 0xE5) : MakeBrush(0xF1, 0xF5, 0xF9),
                AvatarBorder = sel ? MakeBrush(0x4F, 0x46, 0xE5) : MakeBrush(0xE2, 0xE8, 0xF0),
                AvatarFg = sel ? Brushes.White : MakeBrush(0x47, 0x55, 0x69),
                NameWeight = sel ? FontWeights.Bold : FontWeights.SemiBold,
                WeeklyShortText = wkText,
                WeeklyShortBrush = wkBrush,
                WeeklyUsdShortText = wkUsdShort,
                MiniBarWidth = wk.HasValue ? Math.Clamp(218.0 * wkFrac, 0.0, 218.0) : 0.0,
            };
        }).ToList();
    }

    private void RefreshReportView() => _ = RefreshReportViewAsync();

    private async Task RefreshReportViewAsync()
    {
        var target = _selectedProfile;
        if (target == null) return;

        int seq = ++_refreshSeq;
        int offset = _monthOffset;
        var allProfs = _profiles.ToList();

        // 立即更新左侧选中高亮与顶部邮箱，随后在后台线程取内存快照生成报告，主线程 0 毫秒阻塞
        RenderSidebarOnly();
        HeaderEmailText.Text = string.IsNullOrWhiteSpace(target.Email) ? target.Name : target.Email;

        var report = await Task.Run(() => AgQuotaAnalyticsService.AnalyzeAccount(target, allProfs, offset));
        if (seq != _refreshSeq || !ReferenceEquals(target, _selectedProfile)) return;

        RenderSidebarOnly();

        if (report.GlobalMassResets.Count > 0)
        {
            var latest = report.GlobalMassResets[0];
            string summaryStr = I18nService.F("AgInsight.SideMassSummaryFmt", latest.OccurredLocal.ToString("MM-dd HH:mm"), latest.AccountCount);
            if (MassRadarSideCard.Template?.FindName("SideMassSummaryText", MassRadarSideCard) is TextBlock sideTb)
                sideTb.Text = summaryStr;
            MassRadarSideCard.Loaded += (_, _) =>
            {
                if (MassRadarSideCard.Template?.FindName("SideMassSummaryText", MassRadarSideCard) is TextBlock tb)
                    tb.Text = summaryStr;
            };
        }
        else if (MassRadarSideCard.Template?.FindName("SideMassSummaryText", MassRadarSideCard) is TextBlock sideEmpty)
        {
            sideEmpty.Text = I18nService.T("AgInsight.SideMassEmpty");
        }

        TabMassHistoryRadio.Content = report.GlobalMassResets.Count > 0
            ? $"{I18nService.T("AgInsight.TabMassHistory")} ({report.GlobalMassResets.Count})"
            : I18nService.T("AgInsight.TabMassHistory");

        // 2. 顶部身份栏 + 结算日弹窗网格
        HeaderEmailText.Text = string.IsNullOrWhiteSpace(_selectedProfile.Email) ? _selectedProfile.Name : _selectedProfile.Email;
        var (hTierFg, hTierBg) = ResolveTierColors(report.TierDisplay);
        HeaderTierText.Text = string.IsNullOrEmpty(report.TierDisplay) ? "Antigravity" : report.TierDisplay;
        HeaderTierText.Foreground = hTierFg;
        HeaderTierBadge.Background = hTierBg;
        HeaderTierBadge.BorderBrush = hTierBg;

        QuotaSyncTimeText.Text = _selectedProfile.QuotaUpdatedAt.HasValue
            ? I18nService.F("AgInsight.SyncedAtFmt", _selectedProfile.QuotaUpdatedAt.Value.ToLocalTime().ToString("HH:mm"))
            : "";

        PeriodRangeText.Text = $"{report.PeriodStartLocal:yyyy.MM.dd} – {report.PeriodEndLocal:yyyy.MM.dd}";
        PeriodTagText.Text = _monthOffset == 0
            ? I18nService.T("AgInsight.CurrentCycleTag")
            : I18nService.F("AgInsight.PastCycleTagFmt", Math.Abs(_monthOffset));
        NextMonthBtn.IsEnabled = _monthOffset < 0;
        CurrentMonthBtn.Visibility = _monthOffset < 0 ? Visibility.Visible : Visibility.Collapsed;

        int anchorDay = Math.Clamp(report.BillingAnchorDay, 1, 31);
        BillingDayBtnText.Text = I18nService.F("AgInsight.BillingDayBtnFmt", anchorDay);
        if (_renderedAnchorDay != anchorDay)
        {
            _renderedAnchorDay = anchorDay;
            DayGridItems.ItemsSource = Enumerable.Range(1, 31).Select(d =>
            {
                bool active = d == anchorDay;
                return new DayCellVM
                {
                    Day = d,
                    DayText = d.ToString(CultureInfo.InvariantCulture),
                    Bg = active ? MakeBrush(0x4F, 0x46, 0xE5) : MakeBrush(0xF8, 0xFA, 0xFC),
                    Border = active ? MakeBrush(0x4F, 0x46, 0xE5) : MakeBrush(0xE2, 0xE8, 0xF0),
                    Fg = active ? Brushes.White : MakeBrush(0x33, 0x41, 0x55),
                    Weight = active ? FontWeights.Bold : FontWeights.Medium,
                };
            }).ToList();
        }

        // 3. 4 格核心指标带 + 月供给构成比例条
        KpiActualValueText.Text = UsagePricingService.FormatUsd(report.ActualConsumedUsd);
        KpiActualSubText.Text = I18nService.F(
            "AgInsight.KpiActualSubFmt",
            FormatTokens(report.ActualTokens),
            report.ActualRequests.ToString("N0", CultureInfo.CurrentCulture));

        KpiSupplyValueText.Text = "≈ " + UsagePricingService.FormatUsd(report.EstimatedMonthlySupplyUsd);
        KpiSupplySubText.Text = I18nService.F(
            "AgInsight.KpiSupplySubFmt",
            report.UtilizationRatePct.ToString("F1", CultureInfo.InvariantCulture),
            Math.Max(1, (int)Math.Ceiling(report.ElapsedDays)).ToString(CultureInfo.InvariantCulture),
            report.TotalDays.ToString("F0", CultureInfo.InvariantCulture));

        KpiWasteValueText.Text = UsagePricingService.FormatUsd(report.TotalWastedUsd);
        KpiWasteValueText.Foreground = report.TotalWastedUsd >= 1.0 ? MakeBrush(0xB4, 0x53, 0x09) : MakeBrush(0x0F, 0x17, 0x2A);
        KpiWasteSubText.Text = I18nService.F(
            "AgInsight.KpiWasteSubFmt",
            UsagePricingService.FormatUsd(report.ExpiredUnusedWasteUsd),
            UsagePricingService.FormatUsd(report.IdleStandbyWasteUsd));

        KpiBonusValueText.Text = "+" + UsagePricingService.FormatUsd(report.OfficialMassResetBonusUsd);
        KpiBonusSubText.Text = I18nService.F("AgInsight.KpiBonusSubFmt", report.OfficialMassResetCount);

        double denomSupply = Math.Max(0.01, Math.Max(report.EstimatedMonthlySupplyUsd, report.ActualConsumedUsd + report.TotalWastedUsd));
        double remainSupply = Math.Max(0.0, denomSupply - report.ActualConsumedUsd - report.TotalWastedUsd);
        _allocUsedFrac = Math.Clamp(report.ActualConsumedUsd / denomSupply, 0.0, 1.0);
        _allocWasteFrac = Math.Clamp(report.TotalWastedUsd / denomSupply, 0.0, Math.Max(0.0, 1.0 - _allocUsedFrac));
        _allocRemainFrac = Math.Max(0.0, 1.0 - _allocUsedFrac - _allocWasteFrac);

        AllocUsedLegend.Text = I18nService.F("AgInsight.AllocUsedFmt", UsagePricingService.FormatUsd(report.ActualConsumedUsd), (_allocUsedFrac * 100.0).ToString("F1", CultureInfo.InvariantCulture));
        AllocWasteLegend.Text = I18nService.F("AgInsight.AllocWasteFmt", UsagePricingService.FormatUsd(report.TotalWastedUsd), (_allocWasteFrac * 100.0).ToString("F1", CultureInfo.InvariantCulture));
        AllocRemainLegend.Text = I18nService.F("AgInsight.AllocRemainFmt", UsagePricingService.FormatUsd(remainSupply));

        // 4. 对称双模型族配额仪表卡
        var poolMap = report.Pools.ToDictionary(x => x.BucketId, StringComparer.OrdinalIgnoreCase);
        poolMap.TryGetValue(AgQuotaAnalyticsService.BucketGemini5h, out var gem5h);
        poolMap.TryGetValue(AgQuotaAnalyticsService.BucketGeminiWeekly, out var gemWk);
        poolMap.TryGetValue(AgQuotaAnalyticsService.Bucket3p5h, out var p35h);
        poolMap.TryGetValue(AgQuotaAnalyticsService.Bucket3pWeekly, out var p3Wk);

        ApplySourceBadge(GemSourceBadge, GemSourceText, (gemWk?.IsMeasuredFromHistory ?? false) || (gem5h?.IsMeasuredFromHistory ?? false));
        ApplySourceBadge(P3SourceBadge, P3SourceText, (p3Wk?.IsMeasuredFromHistory ?? false) || (p35h?.IsMeasuredFromHistory ?? false));

        _gem5hFrac = BindPoolSlot(gem5h, Gem5hResetText, Gem5hHeadlineUsdText, Gem5hCapSlashText, Gem5hPctPill, Gem5hPctText, Gem5hFill, Gem5hSpentSubText, isWeekly: false, isPurple: false);
        _gemWkFrac = BindPoolSlot(gemWk, GemWkResetText, GemWkHeadlineUsdText, GemWkCapSlashText, GemWkPctPill, GemWkPctText, GemWkFill, GemWkSpentSubText, isWeekly: true, isPurple: false);
        _p35hFrac = BindPoolSlot(p35h, P35hResetText, P35hHeadlineUsdText, P35hCapSlashText, P35hPctPill, P35hPctText, P35hFill, P35hSpentSubText, isWeekly: false, isPurple: false);
        _p3WkFrac = BindPoolSlot(p3Wk, P3WkResetText, P3WkHeadlineUsdText, P3WkCapSlashText, P3WkPctPill, P3WkPctText, P3WkFill, P3WkSpentSubText, isWeekly: true, isPurple: true);

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(UpdateBarWidths));

        // 5. 构建重置历史行并应用三段过滤（周限结算 / 全部流水 / 全局集体重置）
        _allAccountResetRows = report.AccountResetHistory.Select(r =>
        {
            bool isWeeklyPool = r.BucketId.EndsWith("weekly", StringComparison.OrdinalIgnoreCase);
            string typeText;
            Brush typeFg, typeBg;
            bool showZap = false;

            if (r.IsGlobalMassReset && r.MassResetAccountCount >= 2)
            {
                typeText = I18nService.F("AgInsight.TypeMassMultiFmt", r.MassResetAccountCount);
                typeFg = MakeBrush(0x43, 0x38, 0xCA);
                typeBg = MakeBrush(0xEE, 0xF2, 0xFF);
                showZap = true;
            }
            else if (r.IsEarlyOfficialReset)
            {
                typeText = I18nService.T("AgInsight.TypeEarlyOfficial");
                typeFg = MakeBrush(0x43, 0x38, 0xCA);
                typeBg = MakeBrush(0xEE, 0xF2, 0xFF);
                showZap = true;
            }
            else
            {
                typeText = I18nService.T("AgInsight.TypeScheduled");
                typeFg = MakeBrush(0x47, 0x55, 0x69);
                typeBg = MakeBrush(0xF1, 0xF5, 0xF9);
            }

            string typeTip = r.PreviousScheduledResetLocal.HasValue
                ? I18nService.F("AgInsight.PrevSchedTipFmt", r.PreviousScheduledResetLocal.Value.ToString("MM-dd HH:mm"))
                : typeText;

            double bonusOrWaste = r.IsEarlyOfficialReset
                ? Math.Clamp(1.0 - r.PreResetRemainingFraction, 0.0, 1.0) * r.ImpliedCapUsd
                : r.ExpiredWasteUsd;

            string impactText;
            Brush impactBrush;
            if (!isWeeklyPool)
            {
                impactText = I18nService.T("AgInsight.Impact5hNoWaste");
                impactBrush = MakeBrush(0x64, 0x74, 0x8B);
            }
            else if (r.IsEarlyOfficialReset)
            {
                impactText = I18nService.F("AgInsight.ImpactBonusFmt", UsagePricingService.FormatUsd(bonusOrWaste));
                impactBrush = MakeBrush(0x4F, 0x46, 0xE5);
            }
            else
            {
                impactText = bonusOrWaste >= 0.01
                    ? I18nService.F("AgInsight.ImpactWasteFmt", UsagePricingService.FormatUsd(bonusOrWaste))
                    : I18nService.T("AgInsight.ImpactZeroWaste");
                impactBrush = bonusOrWaste >= 0.50 ? MakeBrush(0xB4, 0x53, 0x09) : MakeBrush(0x64, 0x74, 0x8B);
            }

            return new ResetHistoryRowVM
            {
                IsWeekly = isWeeklyPool,
                TimeText = r.ResetTimeLocal.ToString("MM-dd HH:mm"),
                PoolTitle = r.PoolTitle,
                TypeBadgeText = typeText,
                TypeBadgeFg = typeFg,
                TypeBadgeBg = typeBg,
                ZapVisibility = showZap ? Visibility.Visible : Visibility.Collapsed,
                TypeTip = typeTip,
                SpentAndRemText = I18nService.F(
                    "AgInsight.SpentAndRemFmt",
                    UsagePricingService.FormatUsd(r.CycleConsumedUsd),
                    $"{r.PreResetRemainingFraction * 100.0:F0}%"),
                ImpactText = impactText,
                ImpactBrush = impactBrush,
                ImpliedCapSuffix = r.ImpliedCapUsd > 0 ? $"({I18nService.F("AgInsight.CapShortFmt", UsagePricingService.FormatUsd(r.ImpliedCapUsd))})" : ""
            };
        }).ToList();

        if (!_autoSwitchedEmptyWeekly &&
            TabWeeklyHistoryRadio.IsChecked == true &&
            _allAccountResetRows.Count > 0 &&
            !_allAccountResetRows.Any(x => x.IsWeekly))
        {
            _autoSwitchedEmptyWeekly = true;
            TabAcctHistoryRadio.IsChecked = true;
        }

        ApplyHistoryFilter();

        if (report.GlobalMassResets.Count == 0)
        {
            EmptyMassText.Visibility = Visibility.Visible;
            MassClustersList.ItemsSource = null;
        }
        else
        {
            EmptyMassText.Visibility = Visibility.Collapsed;
            MassClustersList.ItemsSource = report.GlobalMassResets.Select(c => new MassClusterRowVM
            {
                TimeText = c.OccurredLocal.ToString("yyyy-MM-dd HH:mm"),
                ScaleText = I18nService.F("AgInsight.MassScaleFmt", c.AccountCount),
                RestoredText = I18nService.F("AgInsight.MassRestoredFmt", (c.TotalRestoredFractionAvg * 100.0).ToString("F0", CultureInfo.InvariantCulture)),
                AccountsText = string.Join(" · ", c.Accounts.Select(a => a.Contains('@') ? a.Split('@')[0] : a))
            }).ToList();
        }
    }

    private void ApplyHistoryFilter()
    {
        if (ResetHistoryList == null || EmptyHistoryText == null) return;
        bool weeklyOnly = TabWeeklyHistoryRadio?.IsChecked == true;
        var rows = weeklyOnly
            ? _allAccountResetRows.Where(x => x.IsWeekly).ToList()
            : _allAccountResetRows;

        if (rows.Count == 0)
        {
            EmptyHistoryText.Visibility = Visibility.Visible;
            ResetHistoryList.ItemsSource = null;
        }
        else
        {
            EmptyHistoryText.Visibility = Visibility.Collapsed;
            ResetHistoryList.ItemsSource = rows;
        }
    }

    private static void ApplySourceBadge(Border badge, TextBlock tb, bool measured)
    {
        if (measured)
        {
            tb.Text = I18nService.T("AgInsight.BadgeMeasured");
            tb.Foreground = MakeBrush(0x15, 0x80, 0x3D);
            badge.Background = MakeBrush(0xF0, 0xFD, 0xF4);
            badge.BorderBrush = MakeBrush(0xBB, 0xF7, 0xD0);
        }
        else
        {
            tb.Text = I18nService.T("AgInsight.BadgeTierRef");
            tb.Foreground = MakeBrush(0x64, 0x74, 0x8B);
            badge.Background = MakeBrush(0xF8, 0xFA, 0xFC);
            badge.BorderBrush = MakeBrush(0xE2, 0xE8, 0xF0);
        }
    }

    private static double BindPoolSlot(
        AgQuotaAnalyticsService.PoolLiveInsight? pool,
        TextBlock resetTb,
        TextBlock headlineUsdTb,
        TextBlock capSlashTb,
        Border pctPill,
        TextBlock pctTb,
        Border fillBar,
        TextBlock spentSubTb,
        bool isWeekly,
        bool isPurple)
    {
        if (pool == null || !pool.IsSupported)
        {
            resetTb.Text = I18nService.T("Ag.No5hLimit");
            headlineUsdTb.Text = "";
            capSlashTb.Text = "";
            pctTb.Text = "—";
            pctTb.Foreground = MakeBrush(0x94, 0xA3, 0xB8);
            pctPill.Background = MakeBrush(0xF1, 0xF5, 0xF9);
            fillBar.Background = MakeBrush(0xCB, 0xD5, 0xE1);
            spentSubTb.Text = "—";
            return 0.0;
        }

        double frac = Math.Clamp(pool.RemainingFraction, 0.0, 1.0);
        double pct = frac * 100.0;
        Brush accent;
        Brush pillBg;
        if (pct <= 15.0)
        {
            accent = MakeBrush(0xDC, 0x26, 0x26);
            pillBg = MakeBrush(0xFE, 0xE2, 0xE2);
        }
        else if (pct <= 40.0)
        {
            accent = MakeBrush(0xB4, 0x53, 0x09);
            pillBg = MakeBrush(0xFE, 0xF3, 0xC7);
        }
        else if (isWeekly)
        {
            accent = isPurple ? MakeBrush(0x6D, 0x28, 0xD9) : MakeBrush(0x43, 0x38, 0xCA);
            pillBg = isPurple ? MakeBrush(0xF5, 0xF3, 0xFF) : MakeBrush(0xEE, 0xF2, 0xFF);
        }
        else
        {
            accent = MakeBrush(0x04, 0x78, 0x57);
            pillBg = MakeBrush(0xEC, 0xFD, 0xF5);
        }

        resetTb.Text = pool.ResetStatusText;
        if (pool.EstimatedCapUsd > 0)
        {
            headlineUsdTb.Text = $"≈ {UsagePricingService.FormatUsd(pool.EstimatedRemainingUsd)}";
            capSlashTb.Text = $"/ {UsagePricingService.FormatUsd(pool.EstimatedCapUsd)}";
        }
        else
        {
            headlineUsdTb.Text = "";
            capSlashTb.Text = "";
        }

        pctTb.Text = $"{pct:F0}%";
        pctTb.Foreground = accent;
        pctPill.Background = pillBg;
        fillBar.Background = pct <= 15.0
            ? MakeBrush(0xDC, 0x26, 0x26)
            : pct <= 40.0
                ? MakeBrush(0xF5, 0x9E, 0x0B)
                : isWeekly
                    ? (isPurple ? MakeBrush(0x7C, 0x3A, 0xED) : MakeBrush(0x4F, 0x46, 0xE5))
                    : MakeBrush(0x05, 0x96, 0x69);

        double usedPct = Math.Clamp((1.0 - frac) * 100.0, 0.0, 100.0);
        spentSubTb.Text = I18nService.F(
            "AgInsight.PoolSubLineFmt",
            UsagePricingService.FormatUsd(pool.CurrentCycleSpentUsd),
            $"{usedPct:F0}%",
            FormatTokens(pool.CurrentCycleTokens));

        return frac;
    }

    private void UpdateBarWidths()
    {
        SetBar(Gem5hTrack, Gem5hFill, _gem5hFrac);
        SetBar(GemWkTrack, GemWkFill, _gemWkFrac);
        SetBar(P35hTrack, P35hFill, _p35hFrac);
        SetBar(P3WkTrack, P3WkFill, _p3WkFrac);

        double allocW = AllocTrack.ActualWidth > 0 ? AllocTrack.ActualWidth : 800.0;
        AllocUsedSeg.Width = Math.Max(0, allocW * _allocUsedFrac);
        AllocWasteSeg.Width = Math.Max(0, allocW * _allocWasteFrac);
        AllocRemainSeg.Width = Math.Max(0, allocW - AllocUsedSeg.Width - AllocWasteSeg.Width);

        static void SetBar(Border track, Border fill, double frac)
        {
            double w = track.ActualWidth > 0 ? track.ActualWidth : 360.0;
            fill.Width = Math.Clamp(w * frac, 0.0, w);
        }
    }

    // ---------- 交互事件 ----------

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnToggleMaximizeClick(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnToggleMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAccountNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AccountNavItemVM vm)
        {
            _selectedProfile = vm.ProfileRef;
            RefreshReportView();
        }
    }

    private void OnJumpToMassTabClick(object sender, RoutedEventArgs e)
    {
        TabMassHistoryRadio.IsChecked = true;
    }

    private void OnHistoryTabChanged(object sender, RoutedEventArgs e)
    {
        if (AccountHistoryPanel == null || MassHistoryPanel == null) return;
        bool isMass = TabMassHistoryRadio?.IsChecked == true;
        AccountHistoryPanel.Visibility = isMass ? Visibility.Collapsed : Visibility.Visible;
        MassHistoryPanel.Visibility = isMass ? Visibility.Visible : Visibility.Collapsed;
        if (!isMass)
            ApplyHistoryFilter();
    }

    private void OnPrevMonthClick(object sender, RoutedEventArgs e)
    {
        _monthOffset--;
        RefreshReportView();
    }

    private void OnNextMonthClick(object sender, RoutedEventArgs e)
    {
        if (_monthOffset < 0)
        {
            _monthOffset++;
            RefreshReportView();
        }
    }

    private void OnBackToCurrentMonthClick(object sender, RoutedEventArgs e)
    {
        _monthOffset = 0;
        RefreshReportView();
    }

    private void OnToggleBillingPopupClick(object sender, RoutedEventArgs e)
    {
        BillingDayPopup.IsOpen = !BillingDayPopup.IsOpen;
    }

    private void OnPickBillingDayCellClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null) return;
        if (sender is FrameworkElement fe && fe.DataContext is DayCellVM cell)
        {
            _selectedProfile.BillingCycleDay = Math.Clamp(cell.Day, 1, 31);
            SaveProfileIfPersisted(_selectedProfile);
            BillingDayPopup.IsOpen = false;
            RefreshReportView();
            DataChanged?.Invoke();
        }
    }

    private async void OnRefreshLiveQuotaClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile == null) return;
        RefreshQuotaBtn.IsEnabled = false;
        string origLabel = RefreshBtnText.Text;
        RefreshBtnText.Text = I18nService.T("AgInsight.RefreshingBtn");
        try
        {
            await Task.Run(() => UsageScanner.ScanAll());
            if (!string.IsNullOrEmpty(_selectedProfile.RefreshToken) || !string.IsNullOrEmpty(_selectedProfile.AccessToken))
            {
                bool ok = await AgQuotaService.RefreshQuotaAsync(_selectedProfile);
                if (ok) SaveProfileIfPersisted(_selectedProfile);
            }
            var snap = _profiles.ToList();
            await Task.Run(() => AgQuotaAnalyticsService.WarmAccountCapCache(snap));
            await RefreshReportViewAsync();
            DataChanged?.Invoke();
        }
        catch { }
        finally
        {
            RefreshBtnText.Text = origLabel;
            RefreshQuotaBtn.IsEnabled = true;
        }
    }

    private static void SaveProfileIfPersisted(Profile p)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(p.FilePath) || !string.IsNullOrWhiteSpace(p.RefreshToken) || !string.IsNullOrWhiteSpace(p.Email))
                ProfileStore.Save(p);
        }
        catch { }
    }

    private static (Brush Fg, Brush Bg) ResolveTierColors(string? tier)
    {
        var t = (tier ?? "").Trim().ToLowerInvariant();
        if (t.Contains("ultra", StringComparison.Ordinal))
            return (MakeBrush(0x7C, 0x3A, 0xED), MakeBrush(0xF5, 0xF3, 0xFF));
        if (t.Contains("pro", StringComparison.Ordinal))
            return (MakeBrush(0x4F, 0x46, 0xE5), MakeBrush(0xEE, 0xF2, 0xFF));
        if (t.Contains("plus", StringComparison.Ordinal))
            return (MakeBrush(0x02, 0x84, 0xC7), MakeBrush(0xE0, 0xF2, 0xFE));
        return (MakeBrush(0x64, 0x74, 0x8B), MakeBrush(0xF1, 0xF5, 0xF9));
    }

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static string FormatTokens(long total)
    {
        if (total >= 1_000_000_000) return $"{total / 1_000_000_000.0:F2}B";
        if (total >= 1_000_000) return $"{total / 1_000_000.0:F2}M";
        if (total >= 1_000) return $"{total / 1_000.0:F1}K";
        return total.ToString("N0", CultureInfo.CurrentCulture);
    }

    // ---------- ViewModels ----------

    private sealed class AccountNavItemVM
    {
        public required Profile ProfileRef { get; init; }
        public string Initial { get; init; } = "A";
        public string ShortName { get; init; } = "";
        public string TierText { get; init; } = "";
        public Brush TierFg { get; init; } = Brushes.SlateGray;
        public bool IsSelected { get; init; }
        public bool IsCurrent { get; init; }
        public Brush CardBg { get; init; } = Brushes.White;
        public Brush CardBorder { get; init; } = Brushes.LightGray;
        public Brush AvatarBg { get; init; } = Brushes.WhiteSmoke;
        public Brush AvatarBorder { get; init; } = Brushes.LightGray;
        public Brush AvatarFg { get; init; } = Brushes.DimGray;
        public FontWeight NameWeight { get; init; } = FontWeights.SemiBold;
        public string WeeklyShortText { get; init; } = "";
        public Brush WeeklyShortBrush { get; init; } = Brushes.Gray;
        public string WeeklyUsdShortText { get; init; } = "";
        public double MiniBarWidth { get; init; }

        public Visibility SelectedBarVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ActiveVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class DayCellVM
    {
        public int Day { get; init; }
        public string DayText { get; init; } = "";
        public Brush Bg { get; init; } = Brushes.White;
        public Brush Border { get; init; } = Brushes.LightGray;
        public Brush Fg { get; init; } = Brushes.Black;
        public FontWeight Weight { get; init; } = FontWeights.Normal;
    }

    private sealed class ResetHistoryRowVM
    {
        public bool IsWeekly { get; init; }
        public string TimeText { get; init; } = "";
        public string PoolTitle { get; init; } = "";
        public string TypeBadgeText { get; init; } = "";
        public Brush TypeBadgeFg { get; init; } = Brushes.Black;
        public Brush TypeBadgeBg { get; init; } = Brushes.WhiteSmoke;
        public Visibility ZapVisibility { get; init; } = Visibility.Collapsed;
        public string TypeTip { get; init; } = "";
        public string SpentAndRemText { get; init; } = "";
        public string ImpactText { get; init; } = "";
        public Brush ImpactBrush { get; init; } = Brushes.DimGray;
        public string ImpliedCapSuffix { get; init; } = "";
    }

    private sealed class MassClusterRowVM
    {
        public string TimeText { get; init; } = "";
        public string ScaleText { get; init; } = "";
        public string RestoredText { get; init; } = "";
        public string AccountsText { get; init; } = "";
    }
}
