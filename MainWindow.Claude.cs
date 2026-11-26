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
    // ---------- Shared row types ----------

    public class ClaudeRow
    {
        public ClaudeProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public bool IsDesktop { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? I18nService.T("Common.OfficialNoEndpoint") : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string Model => string.IsNullOrEmpty(P.Model) ? "—" : P.Model!;

        public bool RequiresRouter => !P.IsOfficial && (
            string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase) ||
            (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)) ||
            (!IsDesktop && P.ModelMappings != null && P.ModelMappings.Any(m => m.Supports1m)) ||
            (P.ExtraOptions != null && P.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

        public Visibility RouterBadgeVisibility => RequiresRouter ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRouterActive => IsDesktop ? LocalProxyServer.IsClaudeDesktopEnabled : LocalProxyServer.IsClaudeCliEnabled;

        public string RouterBadgeText => IsRouterActive ? I18nService.T("Common.NeedRouterOn") : I18nService.T("Common.NeedRouterOff");

        public string RouterBadgeTooltip => IsRouterActive
            ? (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)
                ? I18nService.T("Row.TipChatActive")
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? I18nService.T("Row.TipResponsesActive")
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? I18nService.T("Row.TipMappingActive")
                        : I18nService.T("Row.Tip1mActive"))))
            : (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)                ? I18nService.T("Row.TipChatInactive")
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? I18nService.T("Row.TipResponsesInactive")
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? I18nService.T("Row.TipMappingInactive")
                        : I18nService.T("Row.Tip1mInactive"))));

        public System.Windows.Media.Brush RouterBadgeBackground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF2, 0xF2));

        public System.Windows.Media.Brush RouterBadgeBorderBrush => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0xF3, 0xD0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5));

        public System.Windows.Media.Brush RouterBadgeForeground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x77, 0x58))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));

        public string SubText
        {
            get
            {
                if (P.IsOfficial) return I18nService.T("Common.OfficialNoEndpoint");

                var parts = new List<string?> { string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl };

                if (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(I18nService.T("Row.SubChatNeedsRouter"));
                }
                else if (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(I18nService.T("Row.SubResponsesNeedsRouter"));
                }

                if (P.ModelMappings != null && P.ModelMappings.Count > 0)
                {
                    var mapParts = P.ModelMappings
                        .Where(m => !string.IsNullOrWhiteSpace(m.Model))
                        .Select(m => $"{m.Role}: {m.Model}{(m.Supports1m ? "[1M]" : "")}");
                    var mapStr = string.Join(" | ", mapParts);
                    if (!string.IsNullOrEmpty(mapStr))
                    {
                        parts.Add(mapStr);
                    }
                    else if (!string.IsNullOrEmpty(P.Model))
                    {
                        parts.Add(I18nService.F("Row.SubModelFmt", P.Model));
                    }
                }
                else if (!string.IsNullOrEmpty(P.Model))
                {
                    parts.Add(I18nService.F("Row.SubModelFmt", P.Model));
                }

                return string.Join("   ·   ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        public string Status => IsCurrent ? I18nService.T("Row.StatusCurrent") : "";
    }

    // ---------- Claude CLI ----------

    void RefreshClaude()
    {
        _claudeProviders = CliStore.LoadClaude();
        var currentName = ClaudeCli.CurrentProviderName();
        var currentUrl = ClaudeCli.CurrentBaseUrl();

        var rows = _claudeProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(currentName) && string.IsNullOrEmpty(currentUrl)
                : (!string.IsNullOrEmpty(currentName) && (string.Equals(currentName, p.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(currentName, p.Id, StringComparison.OrdinalIgnoreCase))) ||
                  (!string.IsNullOrEmpty(currentUrl) && string.Equals(currentUrl.TrimEnd('/'), p.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
        }).ToList();

        _claudeCollection.Clear();
        var seenClaude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P.Id) ? r.P.Id : r.P.Name).Trim();
            if (seenClaude.Add(key))
            {
                _claudeCollection.Add(r);
            }
        }
        if (ClaudeList.ItemsSource != _claudeCollection) ClaudeList.ItemsSource = _claudeCollection;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        ClaudeCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? I18nService.T("Msg.OfficialCurrent") : I18nService.F("Msg.UnlistedEndpointFmt", currentUrl)));

        ClaudeEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
    }

    void OnAddClaude(object sender, RoutedEventArgs e) => EditClaude(null);

    void OnRowApplyClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) ApplyClaudeRow(row);
    }

    void OnCardEditClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) EditClaude(row.P);
    }

    void OnCardDeleteClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteProviderConfirmFmt", row.Name))) return;
        _claudeProviders.RemoveAll(x => x.Name == row.P.Name);
        CliStore.SaveClaude(_claudeProviders);
        RefreshClaude();
    }

    void OnCardCopyClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) CopyClaudeProvider(row.P, CopyTarget.ClaudeCli);
    }

    void ApplyClaudeRow(ClaudeRow row)
    {
        bool autoEnabled = false;
        if (row.RequiresRouter && !LocalProxyServer.IsClaudeCliEnabled)
        {
            LocalProxyServer.SetClaudeCliEnabled(true);
            autoEnabled = true;
        }

        try { ClaudeCli.Apply(row.P); }
        catch (Exception ex) { ShowToast(I18nService.F("Common.ApplyFailFmt", ex.Message), isError: true); return; }
        RefreshClaude();
        if (LocalProxyServer.IsClaudeCliEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? I18nService.F("Msg.SwitchedRouterAutoFmt", "Claude CLI", row.Name)
                : I18nService.F("Msg.SwitchedRouterFmt", "Claude CLI", row.Name));
        }
        else
        {
            ShowToast(I18nService.F("Msg.SwitchedPlainFmt", "Claude CLI", row.Name));
        }
    }

    async void EditClaude(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.Claude, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        int idx = -1;
        if (existing != null)
        {
            idx = _claudeProviders.IndexOf(existing);
            if (idx < 0 && !string.IsNullOrEmpty(existing.Id))
                idx = _claudeProviders.FindIndex(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !string.IsNullOrEmpty(existing.Name))
                idx = _claudeProviders.FindIndex(x => string.Equals(x.Name, existing.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0 && !string.IsNullOrEmpty(p.Id))
            idx = _claudeProviders.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && !string.IsNullOrEmpty(p.Name))
            idx = _claudeProviders.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0) _claudeProviders[idx] = p;
        else
        {
            _claudeProviders.Add(p);
            idx = _claudeProviders.Count - 1;
        }

        for (int i = _claudeProviders.Count - 1; i >= 0; i--)
        {
            if (i != idx)
            {
                var other = _claudeProviders[i];
                bool sameId = !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(other.Id) && string.Equals(p.Id, other.Id, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(p.Name, other.Name, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameName)
                {
                    _claudeProviders.RemoveAt(i);
                    if (i < idx) idx--;
                }
            }
        }
        CliStore.SaveClaude(_claudeProviders);

        var currentName = ClaudeCli.CurrentProviderName();
        var currentUrl = ClaudeCli.CurrentBaseUrl()?.TrimEnd('/');
        var isCurrent = (existing != null && (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(existing.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(existing.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(existing.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        )) || (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(p.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        );

        if (isCurrent)
        {
            var dummyRow = new ClaudeRow { P = p, IsDesktop = false };
            if (dummyRow.RequiresRouter && !LocalProxyServer.IsClaudeCliEnabled)
            {
                LocalProxyServer.SetClaudeCliEnabled(true);
            }
            try { ClaudeCli.Apply(p); } catch { }
        }

        RefreshClaude();
        ShowToast(isCurrent ? I18nService.F("Msg.SavedCurrentFmt", p.Name) : I18nService.F("Msg.SavedProviderFmt", p.Name));
    }

    void OnRefreshClaude(object sender, RoutedEventArgs e) => RefreshClaude();

    void OnOpenClaudeSettings(object sender, RoutedEventArgs e) => OpenFile(ClaudeCli.SettingsPath);

    async void OnStopClaudeCli(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, count) = await ClaudeCliProcess.StopClaudeCliAsync();
            if (count > 0)
            {
                ShowToast(I18nService.F("Msg.ClaudeCliStoppedFmt", count));
            }
            else
            {
                ShowToast(I18nService.T("Msg.ClaudeCliNotRunning"));
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    // ---------- Claude Desktop ----------

    void RefreshDesktop()
    {
        _desktopProviders = CliStore.LoadClaudeDesktop();
        var currentName = ClaudeDesktopCli.CurrentProviderName();
        var currentUrl = ClaudeDesktopCli.CurrentGatewayUrl();

        var rows = _desktopProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsDesktop = true,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(currentName) && string.IsNullOrEmpty(currentUrl)
                : (!string.IsNullOrEmpty(currentName) && (string.Equals(currentName, p.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(currentName, p.Id, StringComparison.OrdinalIgnoreCase))) ||
                  (!string.IsNullOrEmpty(currentUrl) && string.Equals(currentUrl.TrimEnd('/'), p.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
        }).ToList();

        _desktopCollection.Clear();
        var seenDesk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P.Id) ? r.P.Id : r.P.Name).Trim();
            if (seenDesk.Add(key))
            {
                _desktopCollection.Add(r);
            }
        }
        if (DesktopList.ItemsSource != _desktopCollection) DesktopList.ItemsSource = _desktopCollection;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        DesktopCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? I18nService.T("Msg.OfficialGatewayCurrent") : I18nService.F("Msg.UnlistedGatewayFmt", currentUrl)));

        DesktopEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
    }

    void OnAddDesktop(object sender, RoutedEventArgs e) => EditDesktop(null);

    void OnRowApplyDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) ApplyDesktopRow(row);
    }

    void OnCardEditDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) EditDesktop(row.P);
    }

    void OnCardDeleteDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteProviderConfirmFmt", row.Name))) return;
        _desktopProviders.RemoveAll(x => x.Name == row.P.Name);
        CliStore.SaveClaudeDesktop(_desktopProviders);
        RefreshDesktop();
    }

    void OnCardCopyDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) CopyClaudeProvider(row.P, CopyTarget.ClaudeDesktop);
    }

    void ApplyDesktopRow(ClaudeRow row)
    {
        bool autoEnabled = false;
        if (row.RequiresRouter && !LocalProxyServer.IsClaudeDesktopEnabled)
        {
            LocalProxyServer.SetClaudeDesktopEnabled(true);
            autoEnabled = true;
        }

        try { ClaudeDesktopCli.Apply(row.P); }
        catch (Exception ex) { ShowToast(I18nService.F("Common.ApplyFailFmt", ex.Message), isError: true); return; }
        RefreshDesktop();
        if (LocalProxyServer.IsClaudeDesktopEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? I18nService.F("Msg.SwitchedRouterAutoFmt", I18nService.T("Common.ClaudeClient"), row.Name)
                : I18nService.F("Msg.SwitchedRouterFmt", I18nService.T("Common.ClaudeClient"), row.Name));
        }
        else
        {
            ShowToast(I18nService.F("Msg.DesktopSwitchedNeedRestartFmt", row.Name));
        }
    }

    async void EditDesktop(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.ClaudeDesktop, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        int idx = -1;
        if (existing != null)
        {
            idx = _desktopProviders.IndexOf(existing);
            if (idx < 0 && !string.IsNullOrEmpty(existing.Id))
                idx = _desktopProviders.FindIndex(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !string.IsNullOrEmpty(existing.Name))
                idx = _desktopProviders.FindIndex(x => string.Equals(x.Name, existing.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0 && !string.IsNullOrEmpty(p.Id))
            idx = _desktopProviders.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && !string.IsNullOrEmpty(p.Name))
            idx = _desktopProviders.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0) _desktopProviders[idx] = p;
        else
        {
            _desktopProviders.Add(p);
            idx = _desktopProviders.Count - 1;
        }

        for (int i = _desktopProviders.Count - 1; i >= 0; i--)
        {
            if (i != idx)
            {
                var other = _desktopProviders[i];
                bool sameId = !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(other.Id) && string.Equals(p.Id, other.Id, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(p.Name, other.Name, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameName)
                {
                    _desktopProviders.RemoveAt(i);
                    if (i < idx) idx--;
                }
            }
        }
        CliStore.SaveClaudeDesktop(_desktopProviders);

        var currentName = ClaudeDesktopCli.CurrentProviderName();
        var currentUrl = ClaudeDesktopCli.CurrentGatewayUrl()?.TrimEnd('/');
        var isCurrent = (existing != null && (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(existing.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(existing.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(existing.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        )) || (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(p.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        );

        if (isCurrent)
        {
            var dummyRow = new ClaudeRow { P = p, IsDesktop = true };
            if (dummyRow.RequiresRouter && !LocalProxyServer.IsClaudeDesktopEnabled)
            {
                LocalProxyServer.SetClaudeDesktopEnabled(true);
            }
            try { ClaudeDesktopCli.Apply(p); } catch { }
        }

        RefreshDesktop();
        ShowToast(isCurrent ? I18nService.F("Msg.SavedDesktopCurrentFmt", p.Name) : I18nService.F("Msg.SavedProviderFmt", p.Name));
    }

    async void OnRestartClaudeDesktop(object sender, RoutedEventArgs e)
    {
        ShowToast(I18nService.F("Msg.RestartingFmt", "Claude"));
        try
        {
            var r = await ClaudeProcess.RestartClaudeAsync();
            if (r.Ok)
                ShowToast(I18nService.F("Msg.RestartedFmt", "Claude"));
            else
                ShowToast(I18nService.F("Msg.RestartNoProcessFmt", "Claude"), isError: true);
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.RestartFailFmt", "Claude", ex.Message), isError: true);
        }
    }

    async void OnStopClaudeDesktop(object sender, RoutedEventArgs e)
    {
        try
        {
            var (ok, count) = await ClaudeProcess.StopClaudeDesktopAsync();
            if (count > 0)
            {
                ShowToast(I18nService.F("Msg.ClaudeDesktopStoppedFmt", count));
            }
            else
            {
                ShowToast(I18nService.T("Msg.ClaudeDesktopNotRunning"));
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, isError: true);
        }
    }

    void OnRefreshDesktop(object sender, RoutedEventArgs e) => RefreshDesktop();

    void OnOpenDesktopConfig(object sender, RoutedEventArgs e) => OpenFile(ClaudeDesktopCli.ConfigPath);

}
