using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using APISwitch.Services;
using Color = System.Windows.Media.Color;

namespace APISwitch.Dialogs;

public partial class PricingCenterDialog : Window
{
    private PricingOverridesConfig _config = new();
    private bool _initializing;

    public bool HasChanges { get; private set; }

    public PricingCenterDialog()
    {
        InitializeComponent();
        LoadInitialData();
    }

    private void LoadInitialData()
    {
        _initializing = true;
        try
        {
            _config = UsagePricingService.GetOverridesSnapshot();
            AutoSyncCheckBox.IsChecked = _config.AutoSyncCloudPrices;

            var (providers, models) = UsageStorage.GetDistinctProvidersAndModels();

            MultProviderCombo.Items.Clear();
            foreach (var p in providers)
                MultProviderCombo.Items.Add(p);
            if (MultProviderCombo.Items.Count > 0)
                MultProviderCombo.SelectedIndex = 0;

            OvProviderCombo.Items.Clear();
            OvProviderCombo.Items.Add("*");
            foreach (var p in providers)
                OvProviderCombo.Items.Add(p);
            OvProviderCombo.SelectedIndex = 0;

            OvModelCombo.Items.Clear();
            foreach (var m in models)
                OvModelCombo.Items.Add(m);

            RefreshSyncStatusUi();
            RefreshListsUi();
        }
        finally
        {
            _initializing = false;
        }
    }

    private void RefreshSyncStatusUi()
    {
        var st = UsagePricingService.GetCacheStatus();
        SyncCountBadgeText.Text = I18nService.F("Pricing.ModelsCountFmt", st.TotalCount);
        string timePart = st.UpdatedAtUtc > DateTime.MinValue
            ? st.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : I18nService.T("Pricing.NeverSynced");
        SyncDetailText.Text = I18nService.F(
            "Pricing.SyncSourcesFmt",
            st.ModelsDevCount,
            st.OpenRouterCount,
            st.AgLedgerCount,
            timePart);
    }

    private void RefreshListsUi()
    {
        var multRows = _config.ProviderMultipliers
            .OrderBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(x => new MultiplierRowVM
            {
                Rule = x,
                Provider = x.Provider,
                MultiplierBadge = x.Multiplier <= 0.000001
                    ? I18nService.T("Pricing.FreeBadge")
                    : $"{x.Multiplier:0.###}x",
                NoteText = string.IsNullOrWhiteSpace(x.Note) ? "" : x.Note
            })
            .ToList();

        MultipliersList.ItemsSource = multRows;
        MultEmptyText.Visibility = multRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var ovRows = _config.ModelOverrides
            .OrderBy(x => x.Provider == "*" ? 1 : 0)
            .ThenBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                bool isWild = string.IsNullOrWhiteSpace(x.Provider) || x.Provider.Trim() == "*";
                double cr = x.CacheReadPerMillion ?? Math.Round(x.InputPerMillion * 0.10, 4);
                return new OverrideRowVM
                {
                    Rule = x,
                    ProviderBadge = isWild ? I18nService.T("Pricing.AllProvidersBadge") : x.Provider,
                    Model = x.Model,
                    RateSummary = I18nService.F(
                        "Pricing.RateSummaryFmt",
                        x.InputPerMillion.ToString("0.####", CultureInfo.InvariantCulture),
                        x.OutputPerMillion.ToString("0.####", CultureInfo.InvariantCulture),
                        cr.ToString("0.####", CultureInfo.InvariantCulture))
                };
            })
            .ToList();

        OverridesList.ItemsSource = ovRows;
        OvEmptyText.Visibility = ovRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveAndMarkChanged(string? feedback = null, bool isError = false)
    {
        UsagePricingService.SaveOverrides(_config);
        HasChanges = true;
        RefreshListsUi();
        if (!string.IsNullOrEmpty(feedback))
            ShowFooterMessage(feedback, isError);
    }

    private void ShowFooterMessage(string msg, bool isError = false)
    {
        StatusToastText.Text = msg;
        StatusToastText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))
            : new SolidColorBrush(Color.FromRgb(0x0F, 0x76, 0x6E));
    }

    private void OnAutoSyncChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _config.AutoSyncCloudPrices = AutoSyncCheckBox.IsChecked == true;
        SaveAndMarkChanged();
    }

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
    {
        SyncNowBtn.IsEnabled = false;
        ShowFooterMessage(I18nService.T("Pricing.SyncingMsg"));
        try
        {
            var res = await Task.Run(() => UsagePricingService.SyncCloudPricesAsync());
            RefreshSyncStatusUi();
            if (res.Success)
            {
                HasChanges = true;
                ShowFooterMessage(I18nService.F("Pricing.SyncOkFmt", res.TotalModels));
            }
            else
            {
                ShowFooterMessage(I18nService.F("Pricing.SyncFailFmt", res.ErrorMessage ?? "Unknown"), isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowFooterMessage(I18nService.F("Pricing.SyncFailFmt", ex.Message), isError: true);
        }
        finally
        {
            SyncNowBtn.IsEnabled = true;
        }
    }

    private void OnSaveMultiplierClick(object sender, RoutedEventArgs e)
    {
        string prov = (MultProviderCombo.Text ?? "").Trim();
        if (string.IsNullOrEmpty(prov) || prov == "*")
        {
            ShowFooterMessage(I18nService.T("Pricing.ErrNeedProvider"), isError: true);
            return;
        }

        string rawMult = (MultValueBox.Text ?? "").Trim().TrimEnd('x', 'X');
        if (!double.TryParse(rawMult, NumberStyles.Float, CultureInfo.InvariantCulture, out double mult) || mult < 0 || mult > 100)
        {
            ShowFooterMessage(I18nService.T("Pricing.ErrInvalidMultiplier"), isError: true);
            return;
        }

        string note = (MultNoteBox.Text ?? "").Trim();
        var existing = _config.ProviderMultipliers.FirstOrDefault(x =>
            string.Equals(x.Provider, prov, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Multiplier = mult;
            existing.Note = note;
            existing.Enabled = true;
        }
        else
        {
            _config.ProviderMultipliers.Add(new ProviderMultiplierRule
            {
                Provider = prov,
                Multiplier = mult,
                Note = note,
                Enabled = true
            });
        }

        MultValueBox.Text = "";
        MultNoteBox.Text = "";
        SaveAndMarkChanged(I18nService.F("Pricing.SavedMultFmt", prov, mult));
    }

    private void OnPickMultiplierRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MultiplierRowVM row)
        {
            MultProviderCombo.Text = row.Rule.Provider;
            MultValueBox.Text = row.Rule.Multiplier.ToString("0.####", CultureInfo.InvariantCulture);
            MultNoteBox.Text = row.Rule.Note ?? "";
        }
    }

    private void OnDeleteMultiplierClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MultiplierRowVM row)
        {
            _config.ProviderMultipliers.Remove(row.Rule);
            SaveAndMarkChanged(I18nService.F("Pricing.DeletedRuleFmt", row.Provider));
        }
    }

    private void OnSaveOverrideClick(object sender, RoutedEventArgs e)
    {
        string prov = (OvProviderCombo.Text ?? "").Trim();
        if (string.IsNullOrEmpty(prov)) prov = "*";

        string model = (OvModelCombo.Text ?? "").Trim();
        if (string.IsNullOrEmpty(model))
        {
            ShowFooterMessage(I18nService.T("Pricing.ErrNeedModel"), isError: true);
            return;
        }

        if (!TryParsePriceBox(OvInputBox.Text, out double pIn) ||
            !TryParsePriceBox(OvOutputBox.Text, out double pOut))
        {
            ShowFooterMessage(I18nService.T("Pricing.ErrInvalidPrice"), isError: true);
            return;
        }

        double? pCr = null;
        if (!string.IsNullOrWhiteSpace(OvCacheReadBox.Text))
        {
            if (!TryParsePriceBox(OvCacheReadBox.Text, out double crVal))
            {
                ShowFooterMessage(I18nService.T("Pricing.ErrInvalidPrice"), isError: true);
                return;
            }
            pCr = crVal;
        }

        double? pCw = null;
        if (!string.IsNullOrWhiteSpace(OvCacheWriteBox.Text))
        {
            if (!TryParsePriceBox(OvCacheWriteBox.Text, out double cwVal))
            {
                ShowFooterMessage(I18nService.T("Pricing.ErrInvalidPrice"), isError: true);
                return;
            }
            pCw = cwVal;
        }

        var existing = _config.ModelOverrides.FirstOrDefault(x =>
            string.Equals(x.Provider, prov, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Model, model, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.InputPerMillion = pIn;
            existing.OutputPerMillion = pOut;
            existing.CacheReadPerMillion = pCr;
            existing.CacheWritePerMillion = pCw;
            existing.Enabled = true;
        }
        else
        {
            _config.ModelOverrides.Add(new ModelPriceOverrideRule
            {
                Provider = prov,
                Model = model,
                InputPerMillion = pIn,
                OutputPerMillion = pOut,
                CacheReadPerMillion = pCr,
                CacheWritePerMillion = pCw,
                Enabled = true
            });
        }

        OvInputBox.Text = "";
        OvOutputBox.Text = "";
        OvCacheReadBox.Text = "";
        OvCacheWriteBox.Text = "";
        SaveAndMarkChanged(I18nService.F("Pricing.SavedOvFmt", prov, model));
    }

    private static bool TryParsePriceBox(string? raw, out double val)
    {
        val = 0;
        var clean = (raw ?? "").Trim().TrimStart('$');
        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out val) &&
               val >= 0 && val <= 10000;
    }

    private void OnPickOverrideRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverrideRowVM row)
        {
            OvProviderCombo.Text = row.Rule.Provider;
            OvModelCombo.Text = row.Rule.Model;
            OvInputBox.Text = row.Rule.InputPerMillion.ToString("0.####", CultureInfo.InvariantCulture);
            OvOutputBox.Text = row.Rule.OutputPerMillion.ToString("0.####", CultureInfo.InvariantCulture);
            OvCacheReadBox.Text = row.Rule.CacheReadPerMillion?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
            OvCacheWriteBox.Text = row.Rule.CacheWritePerMillion?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
        }
    }

    private void OnDeleteOverrideClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is OverrideRowVM row)
        {
            _config.ModelOverrides.Remove(row.Rule);
            SaveAndMarkChanged(I18nService.F("Pricing.DeletedRuleFmt", $"{row.ProviderBadge} · {row.Model}"));
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed class MultiplierRowVM
    {
        public ProviderMultiplierRule Rule { get; init; } = null!;
        public string Provider { get; init; } = "";
        public string MultiplierBadge { get; init; } = "";
        public string NoteText { get; init; } = "";
    }

    private sealed class OverrideRowVM
    {
        public ModelPriceOverrideRule Rule { get; init; } = null!;
        public string ProviderBadge { get; init; } = "";
        public string Model { get; init; } = "";
        public string RateSummary { get; init; } = "";
    }
}
