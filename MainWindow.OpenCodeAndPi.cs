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
    // ---------- OpenCode ----------

    public class OpenCodeRow
    {
        public OpenCodeProvider P { get; init; } = null!;
        public bool IsInConfig { get; init; }
        public bool IsDefault { get; init; }
        public string Name => string.IsNullOrEmpty(P.Name) ? P.Id : P.Name;
        public string Initial
        {
            get
            {
                var s = string.IsNullOrWhiteSpace(P.Name) ? P.Id : P.Name;
                return string.IsNullOrWhiteSpace(s) ? "O" : s.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }
        public string SubText => string.Join("   ·   ",
            new string?[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.Npm,
                ModelsSummary(),
            }.Where(s => !string.IsNullOrEmpty(s)));

        public Visibility PoolBadgeVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnpoolBadgeVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return I18nService.F("Common.ModelsCountFmt", P.CustomModels.Count);
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return I18nService.T("Common.NoModels");
                if (JsonNode.Parse(P.ModelsJson) is JsonObject models)
                {
                    var count = models.Count;
                    return count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", count);
                }
                return I18nService.T("Common.NoModels");
            }
            catch
            {
                return I18nService.T("Common.NoModels");
            }
        }
    }

    void RefreshOpencode()
    {
        _openCodeProviders = CliStore.LoadOpencode();
        var liveIds = new HashSet<string>(OpenCodeCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _openCodeProviders.Count(p => (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name)));
        if (OcPoolCountText != null)
            OcPoolCountText.Text = I18nService.F("Common.PoolCountFmt", inPoolCount, _openCodeProviders.Count);
        UpdateOcDefaultModelText();

        var rows = _openCodeProviders.Select(p =>
        {
            bool inCfg = (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name));
            return new OpenCodeRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).ToList();

        _ocCollection.Clear();
        var seenOc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P?.Id) ? r.P.Id : (r.P?.Name ?? "")).Trim();
            if (!string.IsNullOrEmpty(key) && seenOc.Add(key))
            {
                _ocCollection.Add(r);
            }
        }
        if (OcList.ItemsSource != _ocCollection) OcList.ItemsSource = _ocCollection;
        OcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddOpencode(object sender, RoutedEventArgs e) => EditOpencode(null);

    void OnRowAddOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.SaveProvider(row.P,
                setDefaultModel: !string.IsNullOrEmpty(row.P.DefaultModel), row.P.DefaultModel);
            RefreshOpencode();
            ShowToast(I18nService.F("Msg.OcAddedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.AddFailFmt", ex.Message), isError: true);
        }
    }

    void OnRowRemoveOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.DeleteProvider(row.P.Id);
            RefreshOpencode();
            ShowToast(I18nService.F("Msg.OcRemovedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.RemoveFailFmt", ex.Message), isError: true);
        }
    }

    void OnCardEditOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenCodeRow row) EditOpencode(row.P);
    }

    void OnCardDeleteOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteForeverFmt", row.Name, "opencode.json"))) return;
        try
        {
            _openCodeProviders.RemoveAll(x => string.Equals(x.Id, row.P.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, row.P.Name, StringComparison.OrdinalIgnoreCase));
            CliStore.SaveOpencode(_openCodeProviders);
            OpenCodeCli.DeleteProvider(row.P.Id);
            var curr = OpenCodeCli.CurrentModel();
            if (!string.IsNullOrEmpty(curr) && (string.Equals(curr, row.P.Id, StringComparison.OrdinalIgnoreCase) || curr.StartsWith(row.P.Id + "/", StringComparison.Ordinal)))
            {
                OpenCodeCli.ClearDefaultModel();
            }
            RefreshOpencode();
            ShowToast(I18nService.F("Msg.DeletedProviderFmt", row.Name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.DeleteFailFmt", ex.Message), isError: true); }
    }

    void OnCardCopyOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        var dlg = new CopyTargetsDialog(row.Name, CopyTarget.OpenCode) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(ProviderConvert.ToClaude(row.P));
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(ProviderConvert.ToClaude(row.P));
                    done.Add(I18nService.T("Common.ClaudeClient"));
                    break;
                case CopyTarget.Codex:
                    UpsertCodex(ProviderConvert.ToCodex(row.P));
                    done.Add("Codex");
                    break;
                case CopyTarget.Pi:
                    UpsertPi(ProviderConvert.ToPi(row.P));
                    done.Add("Pi");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshPi();
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", row.P.Id, string.Join(I18nService.T("Common.ListSep"), done)));
    }

    async void EditOpencode(OpenCodeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.OpenCode, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultOpenCode == null) return;
        var p = dlg.ResultOpenCode;
        try
        {
            if (existing != null && !string.Equals(existing.Id, p.Id, StringComparison.OrdinalIgnoreCase))
            {
                _openCodeProviders.RemoveAll(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                OpenCodeCli.DeleteProvider(existing.Id);
            }

            UpsertOpencode(p);

            var liveIds = new HashSet<string>(OpenCodeCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);
            if ((existing != null && liveIds.Contains(existing.Id)) || liveIds.Contains(p.Id))
            {
                OpenCodeCli.SaveProvider(p, setDefaultModel: !string.IsNullOrEmpty(p.DefaultModel), p.DefaultModel);
            // OpenCode 思考强度随 models[] options 一并写出（SaveProvider 内部处理）
            }

            RefreshOpencode();
            ShowToast(I18nService.F("Msg.SavedProviderFmt", p.Id));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.SaveFailFmt", ex.Message), isError: true); }
    }

    void OnRefreshOpencode(object sender, RoutedEventArgs e)
    {
        RefreshOpencode();
        ShowToast(I18nService.F("Msg.OcRefreshedFmt", _openCodeProviders.Count));
    }

    void OnOpenOcConfig(object sender, RoutedEventArgs e) => OpenFile(OpenCodeCli.ConfigPath);

    void UpsertOpencode(OpenCodeProvider p)
    {
        var i = _openCodeProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _openCodeProviders[i] = p; else _openCodeProviders.Add(p);
        _openCodeProviders = CliStore.DeduplicateOpenCode(_openCodeProviders);
        CliStore.SaveOpencode(_openCodeProviders);
    }

    // ---------- Pi ----------

    public class PiRow
    {
        public PiProvider P { get; init; } = null!;
        public bool IsInConfig { get; init; }
        public bool IsDefault { get; init; }
        public string Name => string.IsNullOrEmpty(P.Name) ? P.Id : P.Name;
        public string Initial
        {
            get
            {
                var s = string.IsNullOrWhiteSpace(P.Name) ? P.Id : P.Name;
                return string.IsNullOrWhiteSpace(s) ? "P" : s.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }
        public string SubText => string.Join("   ·   ",
            new string?[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.Api,
                ModelsSummary(),
            }.Where(s => !string.IsNullOrEmpty(s)));

        public Visibility PoolBadgeVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnpoolBadgeVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return I18nService.F("Common.ModelsCountFmt", P.CustomModels.Count);
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return I18nService.T("Common.NoModels");
                if (JsonNode.Parse(P.ModelsJson) is JsonArray arr)
                    return arr.Count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", arr.Count);
                if (JsonNode.Parse(P.ModelsJson) is JsonObject obj)
                    return obj.Count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", obj.Count);
                return I18nService.T("Common.NoModels");
            }
            catch
            {
                return I18nService.T("Common.NoModels");
            }
        }
    }

    void RefreshPi()
    {
        _piProviders = CliStore.LoadPiProviders();
        var liveIds = new HashSet<string>(PiCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _piProviders.Count(p => (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name)));
        if (PiPoolCountText != null)
            PiPoolCountText.Text = I18nService.F("Common.PoolCountFmt", inPoolCount, _piProviders.Count);
        UpdatePiDefaultModelText();

        var rows = _piProviders.Select(p =>
        {
            bool inCfg = (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name));
            return new PiRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).ToList();

        _piCollection.Clear();
        var seenPi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P?.Id) ? r.P.Id : (r.P?.Name ?? "")).Trim();
            if (!string.IsNullOrEmpty(key) && seenPi.Add(key))
            {
                _piCollection.Add(r);
            }
        }
        if (PiList.ItemsSource != _piCollection) PiList.ItemsSource = _piCollection;
        PiEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddPi(object sender, RoutedEventArgs e) => EditPi(null);

    void OnRowAddPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.SaveProvider(row.P,
                setDefaultModel: !string.IsNullOrEmpty(row.P.DefaultModel), row.P.DefaultModel);
            RefreshPi();
            ShowToast(I18nService.F("Msg.PiAddedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.AddFailFmt", ex.Message), isError: true);
        }
    }

    void OnRowRemovePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast(I18nService.F("Msg.PiRemovedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.RemoveFailFmt", ex.Message), isError: true);
        }
    }

    void OnCardEditPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PiRow row) EditPi(row.P);
    }

    void OnCardDeletePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteForeverFmt", row.Name, "models.json"))) return;
        try
        {
            _piProviders.RemoveAll(x => string.Equals(x.Id, row.P.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, row.P.Name, StringComparison.OrdinalIgnoreCase));
            CliStore.SavePiProviders(_piProviders);
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast(I18nService.F("Msg.DeletedProviderFmt", row.Name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.DeleteFailFmt", ex.Message), isError: true); }
    }

    void OnCardCopyPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        var dlg = new CopyTargetsDialog(row.Name, CopyTarget.Pi) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(ProviderConvert.ToClaude(row.P));
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(ProviderConvert.ToClaude(row.P));
                    done.Add(I18nService.T("Common.ClaudeClient"));
                    break;
                case CopyTarget.Codex:
                    UpsertCodex(ProviderConvert.ToCodex(row.P));
                    done.Add("Codex");
                    break;
                case CopyTarget.OpenCode:
                    UpsertOpencode(ProviderConvert.ToOpencode(row.P));
                    done.Add("OpenCode");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode();
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", row.Name, string.Join(I18nService.T("Common.ListSep"), done)));
    }

    async void EditPi(PiProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.Pi, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultPi == null) return;
        var p = dlg.ResultPi;
        try
        {
            if (existing != null && !string.Equals(existing.Id, p.Id, StringComparison.OrdinalIgnoreCase))
            {
                _piProviders.RemoveAll(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                PiCli.DeleteProvider(existing.Id);
            }

            UpsertPi(p);

            var liveIds = new HashSet<string>(PiCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);
            if ((existing != null && liveIds.Contains(existing.Id)) || liveIds.Contains(p.Id))
            {
                PiCli.SaveProvider(p, setDefaultModel: !string.IsNullOrEmpty(p.DefaultModel), p.DefaultModel);
            }

            RefreshPi();
            ShowToast(I18nService.F("Msg.SavedProviderFmt", p.Name ?? p.Id));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.SaveFailFmt", ex.Message), isError: true); }
    }

    void OnRefreshPi(object sender, RoutedEventArgs e)
    {
        RefreshPi();
        ShowToast(I18nService.F("Msg.PiRefreshedFmt", _piProviders.Count));
    }

    /// <summary>从 ~/.pi/agent/settings.json 实时读取默认 provider/model 显示在英雄卡。</summary>
    void UpdatePiDefaultModelText()
    {
        if (PiDefaultModelText == null) return;
        try
        {
            var (prov, model) = PiCli.CurrentDefaults();
            PiDefaultModelText.Text = string.IsNullOrEmpty(prov) && string.IsNullOrEmpty(model)
                ? I18nService.T("Common.NotSet")
                : string.Join(" / ", new[] { prov, model }.Where(s => !string.IsNullOrEmpty(s)));
        }
        catch
        {
            PiDefaultModelText.Text = I18nService.T("Common.NotSet");
        }
    }

    /// <summary>从 opencode.json 顶层 model ("provider/model") 实时读取默认模型显示在英雄卡。</summary>
    void UpdateOcDefaultModelText()
    {
        if (OcDefaultModelText == null) return;
        try
        {
            var cur = OpenCodeCli.CurrentModel();
            OcDefaultModelText.Text = string.IsNullOrEmpty(cur) ? I18nService.T("Common.NotSet") : cur;
        }
        catch
        {
            OcDefaultModelText.Text = I18nService.T("Common.NotSet");
        }
    }

    void OnOpenPiModels(object sender, RoutedEventArgs e) => OpenFile(PiCli.ModelsPath);

    void OnOpenPiDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(PiCli.AuthPath);
        if (dir == null || !Directory.Exists(dir)) { ShowToast(I18nService.T("Msg.PiDirNotFound"), isError: true); return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void OnCapturePi(object sender, RoutedEventArgs e)
    {
        if (!PiCli.IsInstalled) { ShowToast(I18nService.T("Msg.PiNotInstalled"), isError: true); return; }
        var (prov, _) = PiCli.CurrentDefaults();
        var name = $"pi-{prov ?? "default"}-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            var account = PiCli.Capture(name);
            var idx = _piAccounts.FindIndex(a => a.Name == name);
            if (idx >= 0) _piAccounts[idx] = account; else _piAccounts.Add(account);
            CliStore.SavePi(_piAccounts);
            ShowToast(I18nService.F("Msg.PiCapturedFmt", name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Msg.PiCaptureFailFmt", ex.Message), isError: true); }
    }

    void UpsertPi(PiProvider p)
    {
        var i = _piProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _piProviders[i] = p; else _piProviders.Add(p);
        _piProviders = CliStore.DeduplicatePi(_piProviders);
        CliStore.SavePiProviders(_piProviders);
    }

}
