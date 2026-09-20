using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public enum ProviderDialogMode { Claude, ClaudeDesktop, Codex, OpenCode, Pi }

public partial class ProviderDialog : Window
{
    private readonly ProviderDialogMode _mode;
    private readonly object? _existing;
    private readonly TaskCompletionSource<bool> _tcs = new();
    private bool _isKeyVisible = false;
    private bool _isLoaded = false;

    public bool Saved { get; private set; }
    public ClaudeProvider? ResultClaude { get; private set; }
    public CodexProvider? ResultCodex { get; private set; }
    public OpenCodeProvider? ResultOpenCode { get; private set; }
    public PiProvider? ResultPi { get; private set; }

    public ObservableCollection<KeyValueItem> HeadersList { get; } = new();
    public ObservableCollection<KeyValueItem> OptionsList { get; } = new();
    public ObservableCollection<ProviderModelEntry> ModelsList { get; } = new();
    public ObservableCollection<ClaudeModelMapping> ClaudeMappings { get; } = new();
    public ObservableCollection<string> FetchedModels { get; } = new();

    public Task<bool> WaitForResultAsync() => _tcs.Task;

    public ProviderDialog(ProviderDialogMode mode, object? existing)
    {
        InitializeComponent();
        _mode = mode;
        _existing = existing;

        ApplyDialogTheme(mode);

        HeadersItemsControl.ItemsSource = HeadersList;
        OptionsItemsControl.ItemsSource = OptionsList;
        ModelsItemsControl.ItemsSource = ModelsList;
        ClaudeMappingItemsControl.ItemsSource = ClaudeMappings;

        HeadersList.CollectionChanged += OnListChanged;
        OptionsList.CollectionChanged += OnListChanged;
        ModelsList.CollectionChanged += OnListChanged;
        ClaudeMappings.CollectionChanged += OnListChanged;

        InitDefaultClaudeMappings();
        ConfigureMode(mode, existing);
        LoadExisting(existing);

        _isLoaded = true;
        UpdateEmptyStates();
        UpdatePreview();
    }

    private void InitDefaultClaudeMappings()
    {
        ClaudeMappings.Clear();
        var roles = new[] { "Sonnet", "Opus", "Fable", "Haiku" };
        foreach (var r in roles)
        {
            var item = new ClaudeModelMapping { Role = r, DisplayName = "", Model = "", Supports1m = true };
            item.PropertyChanged += (s, e) =>
            {
                UpdatePreview();
            };
            ClaudeMappings.Add(item);
        }
    }

    private void ClaudeModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (sender is System.Windows.Controls.ComboBox cb && cb.DataContext is ClaudeModelMapping mapping)
        {
            if (cb.SelectedItem is string selectedModel && !string.IsNullOrWhiteSpace(selectedModel))
            {
                mapping.Model = selectedModel;
                mapping.DisplayName = selectedModel;
            }
        }
    }

    private void ClaudeAccessMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || ClaudeAccessModeCombo == null) return;
        var mode = (ClaudeAccessModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (mode == "direct")
        {
            DirectModelPanel.Visibility = Visibility.Visible;
            ClaudeModelMappingSection.Visibility = Visibility.Collapsed;
            CodexModelsSection.Visibility = Visibility.Visible;
        }
        else
        {
            DirectModelPanel.Visibility = Visibility.Collapsed;
            ClaudeModelMappingSection.Visibility = Visibility.Visible;
            CodexModelsSection.Visibility = Visibility.Collapsed;
        }
        UpdatePreview();
    }

    private void ClaudeWireCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        UpdatePreview();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ApplyDialogTheme(ProviderDialogMode mode)
    {
        System.Windows.Media.Color primaryColor;
        System.Windows.Media.Color gradientEndColor;
        System.Windows.Media.Color hoverColor;
        System.Windows.Media.Color titleBarBgColor;
        System.Windows.Media.Color titleBarBorderColor;
        System.Windows.Media.Color sidebarBorderColor;
        System.Windows.Media.Color windowBgColor;
        System.Windows.Media.Color activeBgColor;
        System.Windows.Media.Color activeBorderColor;

        switch (mode)
        {
            case ProviderDialogMode.Codex:
                // OpenAI Emerald Green
                primaryColor = System.Windows.Media.Color.FromRgb(0x10, 0xA3, 0x7F);
                gradientEndColor = System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69);
                hoverColor = System.Windows.Media.Color.FromRgb(0x04, 0x78, 0x57);
                activeBgColor = System.Windows.Media.Color.FromRgb(0xC4, 0xF3, 0xDE);
                activeBorderColor = System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99);
                titleBarBgColor = System.Windows.Media.Color.FromRgb(0xD3, 0xEF, 0xE3);
                titleBarBorderColor = System.Windows.Media.Color.FromRgb(0xB9, 0xE5, 0xD2);
                sidebarBorderColor = System.Windows.Media.Color.FromRgb(0xBF, 0xE7, 0xD6);
                windowBgColor = System.Windows.Media.Color.FromRgb(0xDC, 0xF4, 0xEB);
                break;

            case ProviderDialogMode.Claude:
            case ProviderDialogMode.ClaudeDesktop:
                // Anthropic Terracotta / Warm Sand
                primaryColor = System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06);
                gradientEndColor = System.Windows.Media.Color.FromRgb(0xEA, 0x58, 0x0C);
                hoverColor = System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09);
                activeBgColor = System.Windows.Media.Color.FromRgb(0xFC, 0xE3, 0xCB);
                activeBorderColor = System.Windows.Media.Color.FromRgb(0xFB, 0x92, 0x3C);
                titleBarBgColor = System.Windows.Media.Color.FromRgb(0xF0, 0xDF, 0xCD);
                titleBarBorderColor = System.Windows.Media.Color.FromRgb(0xE4, 0xCD, 0xAF);
                sidebarBorderColor = System.Windows.Media.Color.FromRgb(0xE8, 0xD4, 0xBE);
                windowBgColor = System.Windows.Media.Color.FromRgb(0xF7, 0xE8, 0xD8);
                break;

            case ProviderDialogMode.OpenCode:
                // Cyber Sky Cyan / Tech Blue
                primaryColor = System.Windows.Media.Color.FromRgb(0x02, 0x84, 0xC7);
                gradientEndColor = System.Windows.Media.Color.FromRgb(0x0E, 0xA5, 0xE9);
                hoverColor = System.Windows.Media.Color.FromRgb(0x03, 0x69, 0xA1);
                activeBgColor = System.Windows.Media.Color.FromRgb(0xBD, 0xE3, 0xFB);
                activeBorderColor = System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8);
                titleBarBgColor = System.Windows.Media.Color.FromRgb(0xCC, 0xE6, 0xFA);
                titleBarBorderColor = System.Windows.Media.Color.FromRgb(0xB0, 0xD7, 0xF6);
                sidebarBorderColor = System.Windows.Media.Color.FromRgb(0xB9, 0xDC, 0xF7);
                windowBgColor = System.Windows.Media.Color.FromRgb(0xD8, 0xED, 0xFC);
                break;

            case ProviderDialogMode.Pi:
                // Math Geek Violet / Purple
                primaryColor = System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED);
                gradientEndColor = System.Windows.Media.Color.FromRgb(0x93, 0x33, 0xEA);
                hoverColor = System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9);
                activeBgColor = System.Windows.Media.Color.FromRgb(0xD9, 0xC8, 0xFB);
                activeBorderColor = System.Windows.Media.Color.FromRgb(0xA7, 0x8B, 0xFA);
                titleBarBgColor = System.Windows.Media.Color.FromRgb(0xDE, 0xD3, 0xFA);
                titleBarBorderColor = System.Windows.Media.Color.FromRgb(0xC8, 0xB6, 0xF5);
                sidebarBorderColor = System.Windows.Media.Color.FromRgb(0xD0, 0xC1, 0xF7);
                windowBgColor = System.Windows.Media.Color.FromRgb(0xE7, 0xDC, 0xFD);
                break;

            default:
                // Google Indigo / Tech Blue (Default Antigravity style)
                primaryColor = System.Windows.Media.Color.FromRgb(0x4F, 0x46, 0xE5);
                gradientEndColor = System.Windows.Media.Color.FromRgb(0x63, 0x66, 0xF1);
                hoverColor = System.Windows.Media.Color.FromRgb(0x43, 0x38, 0xCA);
                activeBgColor = System.Windows.Media.Color.FromRgb(0xDB, 0xE5, 0xFE);
                activeBorderColor = System.Windows.Media.Color.FromRgb(0x81, 0x8C, 0xF8);
                titleBarBgColor = System.Windows.Media.Color.FromRgb(0xE0, 0xE7, 0xF8);
                titleBarBorderColor = System.Windows.Media.Color.FromRgb(0xCB, 0xD7, 0xEE);
                sidebarBorderColor = System.Windows.Media.Color.FromRgb(0xCF, 0xDB, 0xEE);
                windowBgColor = System.Windows.Media.Color.FromRgb(0xE8, 0xEE, 0xFB);
                break;
        }

        Resources["WindowBgBrush"] = new SolidColorBrush(windowBgColor);
        Resources["TitleBarBgBrush"] = new SolidColorBrush(titleBarBgColor);
        Resources["TitleBarBorderBrush"] = new SolidColorBrush(titleBarBorderColor);
        Resources["SidebarBorderBrush"] = new SolidColorBrush(sidebarBorderColor);
        Resources["TabActiveBgBrush"] = new SolidColorBrush(activeBgColor);
        Resources["TabActiveBorderBrush"] = new SolidColorBrush(activeBorderColor);
        Resources["AccentBrush"] = new SolidColorBrush(primaryColor);
        Resources["AccentHoverBrush"] = new SolidColorBrush(hoverColor);

        var grad = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        grad.GradientStops.Add(new GradientStop(primaryColor, 0));
        grad.GradientStops.Add(new GradientStop(gradientEndColor, 1));
        Resources["AccentGradient"] = grad;
    }

    private static void SelectComboByContent(System.Windows.Controls.ComboBox cb, string? targetText, int defaultIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(targetText))
        {
            if (cb.Items.Count > defaultIndex) cb.SelectedIndex = defaultIndex;
            return;
        }
        for (int i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i] is ComboBoxItem item)
            {
                var content = item.Content?.ToString();
                if (string.Equals(content, targetText, StringComparison.OrdinalIgnoreCase))
                {
                    cb.SelectedIndex = i;
                    return;
                }
            }
        }
        cb.SelectedIndex = defaultIndex;
    }

    private static string GetComboSelectedText(System.Windows.Controls.ComboBox cb, string fallback)
    {
        if (cb.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            var str = item.Content.ToString();
            if (!string.IsNullOrWhiteSpace(str)) return str.Trim();
        }
        return fallback;
    }

    private void ConfigureMode(ProviderDialogMode mode, object? existing)
    {
        bool isEdit = existing != null;
        switch (mode)
        {
            case ProviderDialogMode.Claude:
                DialogTitleText.Text = I18nService.T(isEdit ? "PD.EditTitle" : "PD.NewTitle");
                CategoryBadgeText.Text = I18nService.T("PD.CategoryClaude");
                KeyLabel.Text = I18nService.T("PD.KeyLabelClaude");
                KeyHintText.Text = I18nService.T("PD.KeyHintClaude");
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Visible;
                ClaudeConfigDescText.Text = I18nService.T("PD.ClaudeDescCli");
                CodexWirePanel.Visibility = Visibility.Collapsed;
                OpenCodeFormatPanel.Visibility = Visibility.Collapsed;
                DirectModelPanel.Visibility = Visibility.Collapsed;
                ClaudeModelMappingSection.Visibility = Visibility.Visible;
                CodexModelsSection.Visibility = Visibility.Collapsed;
                SmallFastPanel.Visibility = Visibility.Visible;
                SmallFastCol.Width = new GridLength(1, GridUnitType.Star);
                SmallFastColSpace.Width = new GridLength(16);
                break;

            case ProviderDialogMode.ClaudeDesktop:
                DialogTitleText.Text = I18nService.T(isEdit ? "PD.EditTitle" : "PD.NewTitle");
                CategoryBadgeText.Text = I18nService.T("Common.ClaudeClient");
                KeyLabel.Text = I18nService.T("PD.KeyLabelDesktop");
                KeyHintText.Text = I18nService.T("PD.KeyHintDesktop");
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Visible;
                ClaudeConfigDescText.Text = I18nService.T("PD.ClaudeDescDesktop");
                CodexWirePanel.Visibility = Visibility.Collapsed;
                OpenCodeFormatPanel.Visibility = Visibility.Collapsed;
                DirectModelPanel.Visibility = Visibility.Collapsed;
                ClaudeModelMappingSection.Visibility = Visibility.Visible;
                CodexModelsSection.Visibility = Visibility.Collapsed;
                SmallFastPanel.Visibility = Visibility.Visible;
                SmallFastCol.Width = new GridLength(1, GridUnitType.Star);
                SmallFastColSpace.Width = new GridLength(16);
                break;

            case ProviderDialogMode.Codex:
                DialogTitleText.Text = I18nService.T(isEdit ? "PD.EditTitle" : "PD.NewTitle");
                CategoryBadgeText.Text = "Codex";
                KeyLabel.Text = I18nService.T("PD.KeyLabelCodex");
                KeyHintText.Text = I18nService.T("PD.KeyHintCodex");
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Collapsed;
                CodexWirePanel.Visibility = Visibility.Visible;
                OpenCodeFormatPanel.Visibility = Visibility.Collapsed;
                DirectModelPanel.Visibility = Visibility.Visible;
                ClaudeModelMappingSection.Visibility = Visibility.Collapsed;
                CodexModelsSection.Visibility = Visibility.Visible;
                SmallFastPanel.Visibility = Visibility.Collapsed;
                SmallFastCol.Width = new GridLength(0);
                SmallFastColSpace.Width = new GridLength(0);
                break;

            case ProviderDialogMode.OpenCode:
                DialogTitleText.Text = I18nService.T(isEdit ? "PD.EditTitle" : "PD.NewTitle");
                CategoryBadgeText.Text = "OpenCode";
                OfficialCheck.Visibility = Visibility.Collapsed;
                KeyLabel.Text = I18nService.T("PD.KeyLabelPlain");
                KeyHintText.Text = I18nService.T("PD.KeyHintOc");
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Collapsed;
                CodexWirePanel.Visibility = Visibility.Collapsed;
                OpenCodeFormatPanel.Visibility = Visibility.Visible;
                PiFormatPanel.Visibility = Visibility.Collapsed;
                DirectModelPanel.Visibility = Visibility.Visible;
                ClaudeModelMappingSection.Visibility = Visibility.Collapsed;
                CodexModelsSection.Visibility = Visibility.Visible;
                SmallFastPanel.Visibility = Visibility.Collapsed;
                SmallFastCol.Width = new GridLength(0);
                SmallFastColSpace.Width = new GridLength(0);
                break;

            case ProviderDialogMode.Pi:
                DialogTitleText.Text = I18nService.T(isEdit ? "PD.EditTitle" : "PD.NewTitle");
                CategoryBadgeText.Text = "Pi";
                OfficialCheck.Visibility = Visibility.Collapsed;
                KeyLabel.Text = I18nService.T("PD.KeyLabelPlain");
                KeyHintText.Text = I18nService.T("PD.KeyHintPi");
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Collapsed;
                CodexWirePanel.Visibility = Visibility.Collapsed;
                OpenCodeFormatPanel.Visibility = Visibility.Collapsed;
                PiFormatPanel.Visibility = Visibility.Visible;
                DirectModelPanel.Visibility = Visibility.Visible;
                ClaudeModelMappingSection.Visibility = Visibility.Collapsed;
                CodexModelsSection.Visibility = Visibility.Visible;
                SmallFastPanel.Visibility = Visibility.Collapsed;
                SmallFastCol.Width = new GridLength(0);
                SmallFastColSpace.Width = new GridLength(0);
                break;
        }

        if (isEdit)
        {
            IdBox.IsEnabled = false;
            IdHintText.Text = I18nService.T("PD.IdLocked");
        }
    }

    private void LoadExisting(object? existing)
    {
        if (existing is ClaudeProvider c)
        {
            IdBox.Text = !string.IsNullOrWhiteSpace(c.Id) ? c.Id : c.Name.ToLowerInvariant().Replace(' ', '-');
            NameBox.Text = c.Name;
            NotesBox.Text = c.Notes ?? "";
            WebsiteUrlBox.Text = c.WebsiteUrl ?? "";
            OfficialCheck.IsChecked = c.IsOfficial;
            BaseUrlBox.Text = c.BaseUrl ?? "";
            SetKey(c.AuthToken);
            ModelCombo.Text = c.Model ?? "";
            SmallFastCombo.Text = c.SmallFastModel ?? "";

            SetClaudeAccessMode(c.AccessMode);
            SetClaudeWireApi(c.WireApi);

            if (c.ModelMappings != null && c.ModelMappings.Count > 0)
            {
                foreach (var mapping in ClaudeMappings)
                {
                    var found = c.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, mapping.Role, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        mapping.Model = found.Model ?? "";
                        mapping.DisplayName = string.IsNullOrWhiteSpace(found.DisplayName) ? mapping.Model : found.DisplayName;
                        mapping.Supports1m = found.Supports1m;
                    }
                }
            }
            else
            {
                PopulateMappingsFromLegacy(c);
            }

            if (c.CustomHeaders != null)
                foreach (var kv in c.CustomHeaders)
                    HeadersList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (c.ExtraOptions != null)
                foreach (var kv in c.ExtraOptions)
                    OptionsList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (c.ExtraEnv != null)
                foreach (var kv in c.ExtraEnv)
                    if (!OptionsList.Any(o => o.Key == kv.Key) && !kv.Key.StartsWith("ANTHROPIC_DEFAULT_") && kv.Key != "ANTHROPIC_MODEL" && kv.Key != "ANTHROPIC_SMALL_FAST_MODEL")
                        OptionsList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (c.CustomModels != null)
                foreach (var m in c.CustomModels)
                {
                    var entry = new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow };
                    entry.PropertyChanged += (_, _) => UpdatePreview();
                    ModelsList.Add(entry);
                }
        }
        else if (existing is CodexProvider x)
        {
            IdBox.Text = x.Id;
            NameBox.Text = x.Name;
            NotesBox.Text = x.Notes ?? "";
            WebsiteUrlBox.Text = x.WebsiteUrl ?? "";
            OfficialCheck.IsChecked = x.IsOfficial;
            BaseUrlBox.Text = x.BaseUrl ?? "";
            SetKey(!string.IsNullOrWhiteSpace(x.ApiKey) ? x.ApiKey : x.BearerToken);
            ModelCombo.Text = x.Model ?? "";
            SetWireApi(x.WireApi);

            if (x.CustomHeaders != null)
                foreach (var kv in x.CustomHeaders)
                    HeadersList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (x.ExtraOptions != null)
                foreach (var kv in x.ExtraOptions)
                    OptionsList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (x.CustomModels != null)
                foreach (var m in x.CustomModels)
                {
                    var entry = new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow };
                    entry.PropertyChanged += (_, _) => UpdatePreview();
                    ModelsList.Add(entry);
                }
        }
        else if (existing is OpenCodeProvider oc)
        {
            IdBox.Text = oc.Id;
            NameBox.Text = oc.Name ?? oc.Id;
            NotesBox.Text = oc.Notes ?? "";
            WebsiteUrlBox.Text = oc.WebsiteUrl ?? "";
            BaseUrlBox.Text = oc.BaseUrl ?? "";
            SetKey(oc.ApiKey);
            SelectComboByContent(OcNpmCombo, string.IsNullOrWhiteSpace(oc.Npm) ? "@ai-sdk/openai-compatible" : oc.Npm);

            if (oc.CustomHeaders != null)
                foreach (var kv in oc.CustomHeaders)
                    HeadersList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (oc.ExtraOptions != null)
                foreach (var kv in oc.ExtraOptions)
                    OptionsList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (oc.CustomModels != null && oc.CustomModels.Count > 0)
            {
                foreach (var m in oc.CustomModels)
                {
                    var entry = new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow };
                    entry.PropertyChanged += (_, _) => UpdatePreview();
                    ModelsList.Add(entry);
                }
            }
            else if (!string.IsNullOrWhiteSpace(oc.ModelsJson))
            {
                try
                {
                    if (JsonNode.Parse(oc.ModelsJson) is JsonObject modelsObj)
                    {
                        foreach (var kv in modelsObj)
                        {
                            var dName = kv.Value?["name"]?.GetValue<string>() ?? "";
                            var entry = new ProviderModelEntry { Id = kv.Key, Name = dName, ContextWindow = "1m" };
                            entry.PropertyChanged += (_, _) => UpdatePreview();
                            ModelsList.Add(entry);
                        }
                    }
                }
                catch { }
            }
        }
        else if (existing is PiProvider pi)
        {
            IdBox.Text = pi.Id;
            NameBox.Text = pi.Name ?? pi.Id;
            NotesBox.Text = pi.Notes ?? "";
            WebsiteUrlBox.Text = pi.WebsiteUrl ?? "";
            BaseUrlBox.Text = pi.BaseUrl ?? "";
            SetKey(pi.ApiKey);
            SelectComboByContent(PiApiCombo, string.IsNullOrWhiteSpace(pi.Api) ? "openai-completions" : pi.Api);

            if (pi.CustomHeaders != null)
                foreach (var kv in pi.CustomHeaders)
                    HeadersList.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            if (pi.CustomModels != null && pi.CustomModels.Count > 0)
            {
                foreach (var m in pi.CustomModels)
                {
                    var entry = new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow };
                    entry.PropertyChanged += (_, _) => UpdatePreview();
                    ModelsList.Add(entry);
                }
            }
            else if (!string.IsNullOrWhiteSpace(pi.ModelsJson))
            {
                try
                {
                    if (JsonNode.Parse(pi.ModelsJson) is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            if (item is JsonObject mObj)
                            {
                                var mId = mObj["id"]?.GetValue<string>() ?? "";
                                var mName = mObj["name"]?.GetValue<string>() ?? mId;
                                if (!string.IsNullOrEmpty(mId))
                                {
                                    var entry = new ProviderModelEntry { Id = mId, Name = mName };
                                    entry.PropertyChanged += (_, _) => UpdatePreview();
                                    ModelsList.Add(entry);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        else
        {
            if (_mode == ProviderDialogMode.OpenCode)
            {
                SelectComboByContent(OcNpmCombo, "@ai-sdk/openai-compatible");
            }
            else if (_mode == ProviderDialogMode.Pi)
            {
                SelectComboByContent(PiApiCombo, "openai-completions");
            }
        }

        IdBox.TextChanged += (_, _) => UpdatePreview();
        NameBox.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(IdBox.Text) || !IdBox.IsEnabled)
            {
                if (IdBox.IsEnabled) IdBox.Text = MakeSlug(NameBox.Text);
            }
            UpdatePreview();
        };
        NotesBox.TextChanged += (_, _) => UpdatePreview();
        WebsiteUrlBox.TextChanged += (_, _) => UpdatePreview();
        BaseUrlBox.TextChanged += (_, _) => UpdatePreview();
        ModelCombo.SelectionChanged += (_, _) => UpdatePreview();
        ModelCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => UpdatePreview()));
        SmallFastCombo.SelectionChanged += (_, _) => UpdatePreview();
        SmallFastCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => UpdatePreview()));
        KeyTextBox.TextChanged += (_, _) => UpdatePreview();
        KeyPasswordBox.PasswordChanged += (_, _) => UpdatePreview();
        CodexWireCombo.SelectionChanged += (_, _) => UpdatePreview();
        OcNpmCombo.SelectionChanged += (_, _) => UpdatePreview();
        PiApiCombo.SelectionChanged += (_, _) => UpdatePreview();
    }

    private void SetKey(string? key)
    {
        key ??= "";
        KeyPasswordBox.Password = key;
        KeyTextBox.Text = key;
    }

    private string GetKey() => _isKeyVisible ? KeyTextBox.Text : KeyPasswordBox.Password;

    private void SetWireApi(string? wireApi)
    {
        var target = (wireApi ?? "").Trim().ToLowerInvariant();
        int idx = target switch
        {
            "chat" => 1,
            "anthropic" => 2,
            _ => 0
        };
        CodexWireCombo.SelectedIndex = idx;
    }

    private string GetWireApi()
    {
        return (CodexWireCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "responses";
    }

    private void SetClaudeAccessMode(string? mode)
    {
        if (ClaudeAccessModeCombo == null) return;
        ClaudeAccessModeCombo.SelectedIndex = string.Equals(mode, "direct", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private void SetClaudeWireApi(string? wireApi)
    {
        if (ClaudeWireCombo == null) return;
        var target = (wireApi ?? "").Trim().ToLowerInvariant();
        int idx = target switch
        {
            "chat" => 1,
            "responses" => 2,
            _ => 0
        };
        ClaudeWireCombo.SelectedIndex = idx;
    }

    private void PopulateMappingsFromLegacy(ClaudeProvider c)
    {
        var sonnet = ClaudeMappings.FirstOrDefault(m => m.Role == "Sonnet");
        var opus = ClaudeMappings.FirstOrDefault(m => m.Role == "Opus");
        var fable = ClaudeMappings.FirstOrDefault(m => m.Role == "Fable");
        var haiku = ClaudeMappings.FirstOrDefault(m => m.Role == "Haiku");

        void SetRole(ClaudeModelMapping? target, string? modelVal, string? nameVal, string? fallback)
        {
            if (target == null) return;
            var raw = !string.IsNullOrWhiteSpace(modelVal) ? modelVal : fallback;
            if (string.IsNullOrWhiteSpace(raw)) return;
            raw = raw.Trim();
            if (raw.EndsWith("[1M]", StringComparison.OrdinalIgnoreCase))
            {
                target.Model = raw.Substring(0, raw.Length - 4).Trim();
                target.Supports1m = true;
            }
            else
            {
                target.Model = raw;
                target.Supports1m = true;
            }
            target.DisplayName = !string.IsNullOrWhiteSpace(nameVal) ? nameVal.Trim() : target.Model;
        }

        string? GetEnv(string key) => c.ExtraEnv != null && c.ExtraEnv.TryGetValue(key, out var v) ? v : null;

        SetRole(sonnet, GetEnv("ANTHROPIC_DEFAULT_SONNET_MODEL"), GetEnv("ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"), c.Model);
        SetRole(opus, GetEnv("ANTHROPIC_DEFAULT_OPUS_MODEL"), GetEnv("ANTHROPIC_DEFAULT_OPUS_MODEL_NAME"), c.Model);
        SetRole(fable, GetEnv("ANTHROPIC_DEFAULT_FABLE_MODEL"), GetEnv("ANTHROPIC_DEFAULT_FABLE_MODEL_NAME"), c.Model);
        SetRole(haiku, GetEnv("ANTHROPIC_DEFAULT_HAIKU_MODEL"), GetEnv("ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME"), c.SmallFastModel ?? c.Model);
    }

    private void ToggleKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        _isKeyVisible = !_isKeyVisible;
        if (_isKeyVisible)
        {
            KeyTextBox.Text = KeyPasswordBox.Password;
            KeyPasswordBox.Visibility = Visibility.Collapsed;
            KeyTextBox.Visibility = Visibility.Visible;
            EyeIconPath.Data = (Geometry)FindResource("IconEyeOff");
            KeyTextBox.Focus();
            KeyTextBox.CaretIndex = KeyTextBox.Text.Length;
        }
        else
        {
            KeyPasswordBox.Password = KeyTextBox.Text;
            KeyTextBox.Visibility = Visibility.Collapsed;
            KeyPasswordBox.Visibility = Visibility.Visible;
            EyeIconPath.Data = (Geometry)FindResource("IconEye");
            KeyPasswordBox.Focus();
        }
    }

    private void OfficialCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (FieldsPanel != null)
            FieldsPanel.Visibility = OfficialCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        UpdatePreview();
    }

    private void ToggleAdvanced_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedSettingsPanel == null || AdvancedArrowRotate == null) return;
        bool isExpanded = AdvancedSettingsPanel.Visibility == Visibility.Visible;
        if (isExpanded)
        {
            AdvancedSettingsPanel.Visibility = Visibility.Collapsed;
            AdvancedArrowRotate.Angle = 0;
        }
        else
        {
            AdvancedSettingsPanel.Visibility = Visibility.Visible;
            AdvancedArrowRotate.Angle = 90;
        }
    }

    private void EnsureAdvancedExpanded()
    {
        if (AdvancedSettingsPanel != null && AdvancedArrowRotate != null && AdvancedSettingsPanel.Visibility != Visibility.Visible)
        {
            AdvancedSettingsPanel.Visibility = Visibility.Visible;
            AdvancedArrowRotate.Angle = 90;
        }
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<System.ComponentModel.INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<System.ComponentModel.INotifyPropertyChanged>())
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
        UpdateEmptyStates();
        UpdatePreview();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        UpdateEmptyStates();
        UpdatePreview();
    }

    private void UpdateEmptyStates()
    {
        if (EmptyHeadersText != null) EmptyHeadersText.Visibility = HeadersList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyOptionsText != null) EmptyOptionsText.Visibility = OptionsList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyModelsText != null) EmptyModelsText.Visibility = ModelsList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int advancedCount = HeadersList.Count(h => !string.IsNullOrWhiteSpace(h.Key)) +
                            OptionsList.Count(o => !string.IsNullOrWhiteSpace(o.Key));
        if (AdvancedCountBadge != null && AdvancedCountText != null)
        {
            if (advancedCount > 0)
            {
                AdvancedCountBadge.Visibility = Visibility.Visible;
                AdvancedCountText.Text = I18nService.F("PD.AdvancedCountFmt", advancedCount);
            }
            else
            {
                AdvancedCountBadge.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void AddHeader_Click(object sender, RoutedEventArgs e)
    {
        EnsureAdvancedExpanded();
        var item = new KeyValueItem();
        item.PropertyChanged += (_, _) => UpdatePreview();
        HeadersList.Add(item);
    }

    private void DeleteHeader_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyValueItem item)
            HeadersList.Remove(item);
    }

    private void AddOption_Click(object sender, RoutedEventArgs e)
    {
        EnsureAdvancedExpanded();
        var item = new KeyValueItem();
        item.PropertyChanged += (_, _) => UpdatePreview();
        OptionsList.Add(item);
    }

    private void DeleteOption_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyValueItem item)
            OptionsList.Remove(item);
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var item = new ProviderModelEntry { ContextWindow = "1m" };
        item.PropertyChanged += (_, _) => UpdatePreview();
        ModelsList.Add(item);
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProviderModelEntry item)
            ModelsList.Remove(item);
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            SetStatus(I18nService.T("PD.StatusNeedBaseUrl"), isError: true);
            BaseUrlBox.Focus();
            return;
        }

        var apiKey = GetKey().Trim();
        FetchModelsBtn.IsEnabled = false;
        FetchModelsBtnText.Text = I18nService.T("PD.FetchingBtn");
        if (DirectFetchModelsBtn != null) DirectFetchModelsBtn.IsEnabled = false;
        if (DirectFetchBtnText != null) DirectFetchBtnText.Text = I18nService.T("PD.FetchingBtn");
        if (ClaudeFetchModelsBtnText != null) ClaudeFetchModelsBtnText.Text = I18nService.T("PD.FetchingBtn");
        SetStatus(I18nService.T("PD.StatusFetching"));

        try
        {
            var headers = HeadersList
                .Where(h => !string.IsNullOrWhiteSpace(h.Key))
                .ToDictionary(h => h.Key.Trim(), h => h.Value ?? "");

            var models = await ModelFetchService.FetchModelsAsync(baseUrl, apiKey, headers);

            FetchedModels.Clear();
            foreach (var m in models)
            {
                FetchedModels.Add(m);
            }

            if (models.Count > 0)
            {
                bool isClaudeMapping = _mode is ProviderDialogMode.Claude or ProviderDialogMode.ClaudeDesktop &&
                                       (ClaudeAccessModeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "direct";

                FetchedModelsBar.Visibility = isClaudeMapping ? Visibility.Collapsed : Visibility.Visible;
                FetchedModelsTitleText.Text = I18nService.F("PD.FetchedTitleFmt", models.Count);
                AddAllFetchedBtn.Content = I18nService.F("PD.AddAllCountFmt", models.Count);

                if (SelectModelToAddCombo.SelectedItem == null && FetchedModels.Count > 0)
                {
                    SelectModelToAddCombo.SelectedIndex = 0;
                }

                if (string.IsNullOrWhiteSpace(ModelCombo.Text))
                {
                    ModelCombo.Text = models[0];
                }

                if (isClaudeMapping)
                {
                    var sonnet = ClaudeMappings.FirstOrDefault(m => m.Role == "Sonnet");
                    if (sonnet != null && string.IsNullOrWhiteSpace(sonnet.Model))
                    {
                        sonnet.Model = models[0];
                        if (string.IsNullOrWhiteSpace(sonnet.DisplayName)) sonnet.DisplayName = models[0];
                    }
                }

                SetStatus(I18nService.F("PD.StatusFetchOkFmt", models.Count), isError: false);
            }
            else
            {
                SetStatus(I18nService.T("PD.StatusFetchEmpty"), isError: true);
            }

            UpdateEmptyStates();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            SetStatus(I18nService.F("PD.StatusFetchFailFmt", ex.Message), isError: true);
        }
        finally
        {
            FetchModelsBtn.IsEnabled = true;
            FetchModelsBtnText.Text = I18nService.T("PD.FetchListBtn");
            if (DirectFetchModelsBtn != null) DirectFetchModelsBtn.IsEnabled = true;
            if (DirectFetchBtnText != null) DirectFetchBtnText.Text = I18nService.T("PD.FetchModelsBtn");
            if (ClaudeFetchModelsBtnText != null) ClaudeFetchModelsBtnText.Text = I18nService.T("PD.FetchListBtn");
        }
    }

    private void AddSelectedFetchedModel_Click(object sender, RoutedEventArgs e)
    {
        var modelId = SelectModelToAddCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            SetStatus(I18nService.T("PD.StatusSelectFirst"), isError: true);
            return;
        }

        if (ModelsList.Any(existing => string.Equals(existing.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus(I18nService.F("PD.StatusModelExistsFmt", modelId), isError: true);
            return;
        }

        var entry = new ProviderModelEntry { Id = modelId, Name = "", ContextWindow = "1m" };
        entry.PropertyChanged += (_, _) => UpdatePreview();
        ModelsList.Add(entry);
        UpdateEmptyStates();
        UpdatePreview();
        SetStatus(I18nService.F("PD.StatusModelAddedFmt", modelId), isError: false);
    }

    private void AddAllFetchedModels_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        foreach (var m in FetchedModels)
        {
            if (!ModelsList.Any(existing => string.Equals(existing.Id, m, StringComparison.OrdinalIgnoreCase)))
            {
                var entry = new ProviderModelEntry { Id = m, Name = "", ContextWindow = "1m" };
                entry.PropertyChanged += (_, _) => UpdatePreview();
                ModelsList.Add(entry);
                added++;
            }
        }
        UpdateEmptyStates();
        UpdatePreview();
        SetStatus(I18nService.F("PD.StatusBatchAddedFmt", added), isError: false);
    }

    private void SetStatus(string msg, bool isError = false)
    {
        StatusMessageText.Text = msg;
        StatusMessageText.Foreground = (SolidColorBrush)FindResource(isError ? "DangerBrush" : "GreenBrush");
    }

    private void UpdatePreview()
    {
        if (!_isLoaded || JsonPreviewBox == null) return;

        try
        {
            var key = GetKey().Trim();
            var baseUrl = BaseUrlBox.Text.Trim();
            var name = NameBox.Text.Trim();
            var id = IdBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(id)) id = MakeSlug(name);

            if (_mode == ProviderDialogMode.OpenCode)
            {
                var root = new JsonObject
                {
                    ["npm"] = GetComboSelectedText(OcNpmCombo, "@ai-sdk/openai-compatible")
                };

                var options = new JsonObject();
                if (!string.IsNullOrWhiteSpace(baseUrl)) options["baseURL"] = baseUrl;
                if (!string.IsNullOrWhiteSpace(key)) options["apiKey"] = key;

                if (HeadersList.Any(h => !string.IsNullOrWhiteSpace(h.Key)))
                {
                    var hObj = new JsonObject();
                    foreach (var h in HeadersList.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
                        hObj[h.Key.Trim()] = h.Value ?? "";
                    options["headers"] = hObj;
                }

                if (OptionsList.Any(o => !string.IsNullOrWhiteSpace(o.Key)))
                {
                    foreach (var o in OptionsList.Where(o => !string.IsNullOrWhiteSpace(o.Key)))
                    {
                        var valStr = o.Value ?? "";
                        if (bool.TryParse(valStr, out var bVal)) options[o.Key.Trim()] = bVal;
                        else if (long.TryParse(valStr, out var lVal)) options[o.Key.Trim()] = lVal;
                        else if (double.TryParse(valStr, out var dVal)) options[o.Key.Trim()] = dVal;
                        else options[o.Key.Trim()] = valStr;
                    }
                }

                root["options"] = options;

                var models = new JsonObject();
                foreach (var m in ModelsList.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
                {
                    models[m.Id.Trim()] = new JsonObject { 
                        ["name"] = m.Name ?? "",
                        ["context_window"] = string.IsNullOrWhiteSpace(m.ContextWindow) ? "1m" : m.ContextWindow.Trim()
                    };
                }
                if (models.Count > 0) root["models"] = models;

                JsonPreviewBox.Text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            else if (_mode == ProviderDialogMode.Pi)
            {
                var root = new JsonObject
                {
                    ["name"] = name,
                    ["baseUrl"] = baseUrl,
                    ["api"] = GetComboSelectedText(PiApiCombo, "openai-completions"),
                };
                if (!string.IsNullOrWhiteSpace(key)) root["apiKey"] = key;

                if (HeadersList.Any(h => !string.IsNullOrWhiteSpace(h.Key)))
                {
                    var hObj = new JsonObject();
                    foreach (var h in HeadersList.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
                        hObj[h.Key.Trim()] = h.Value ?? "";
                    root["headers"] = hObj;
                }

                var mArr = new JsonArray();
                foreach (var m in ModelsList.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
                {
                    mArr.Add(new JsonObject
                    {
                        ["id"] = m.Id.Trim(),
                        ["name"] = string.IsNullOrWhiteSpace(m.Name) ? m.Id.Trim() : m.Name.Trim(),
                    });
                }
                if (mArr.Count > 0) root["models"] = mArr;

                JsonPreviewBox.Text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            else if (_mode == ProviderDialogMode.Codex)
            {
                var root = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["wire_api"] = GetWireApi()
                };

                if (!string.IsNullOrWhiteSpace(baseUrl)) root["base_url"] = baseUrl;
                if (!string.IsNullOrWhiteSpace(key)) root["api_key"] = key;
                if (!string.IsNullOrWhiteSpace(ModelCombo.Text)) root["model"] = ModelCombo.Text.Trim();

                if (HeadersList.Any(h => !string.IsNullOrWhiteSpace(h.Key)))
                {
                    var hObj = new JsonObject();
                    foreach (var h in HeadersList.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
                        hObj[h.Key.Trim()] = h.Value ?? "";
                    root["headers"] = hObj;
                }

                if (OptionsList.Any(o => !string.IsNullOrWhiteSpace(o.Key)))
                {
                    var oObj = new JsonObject();
                    foreach (var o in OptionsList.Where(o => !string.IsNullOrWhiteSpace(o.Key)))
                        oObj[o.Key.Trim()] = o.Value ?? "";
                    root["extra_options"] = oObj;
                }

                if (ModelsList.Any(m => !string.IsNullOrWhiteSpace(m.Id)))
                {
                    var mArr = new JsonArray();
                    foreach (var m in ModelsList.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
                        mArr.Add(new JsonObject { 
                            ["id"] = m.Id.Trim(), 
                            ["name"] = m.Name ?? "",
                            ["context_window"] = string.IsNullOrWhiteSpace(m.ContextWindow) ? "1m" : m.ContextWindow.Trim()
                        });
                    root["models"] = mArr;
                }

                JsonPreviewBox.Text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                var wireApi = (ClaudeWireCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "anthropic";
                var accessMode = (ClaudeAccessModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mapping";

                var root = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["wire_api"] = wireApi,
                    ["access_mode"] = accessMode,
                };

                if (!string.IsNullOrWhiteSpace(baseUrl)) root["base_url"] = baseUrl;
                if (!string.IsNullOrWhiteSpace(key)) root["auth_token"] = key;

                if (accessMode == "mapping")
                {
                    var mappingsObj = new JsonObject();
                    foreach (var m in ClaudeMappings)
                    {
                        var rKey = m.Role.ToLowerInvariant();
                        mappingsObj[rKey] = new JsonObject
                        {
                            ["model"] = m.Model ?? "",
                            ["display_name"] = string.IsNullOrWhiteSpace(m.DisplayName) ? (m.Model ?? "") : m.DisplayName,
                            ["supports_1m"] = m.Supports1m
                        };
                    }
                    root["model_mappings"] = mappingsObj;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(ModelCombo.Text)) root["model"] = ModelCombo.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(SmallFastCombo.Text)) root["small_fast_model"] = SmallFastCombo.Text.Trim();

                    if (ModelsList.Any(m => !string.IsNullOrWhiteSpace(m.Id)))
                    {
                        var mArr = new JsonArray();
                        foreach (var m in ModelsList.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
                            mArr.Add(new JsonObject
                            {
                                ["id"] = m.Id.Trim(),
                                ["name"] = m.Name ?? "",
                                ["context_window"] = string.IsNullOrWhiteSpace(m.ContextWindow) ? "1m" : m.ContextWindow.Trim()
                            });
                        root["models"] = mArr;
                    }
                }

                if (HeadersList.Any(h => !string.IsNullOrWhiteSpace(h.Key)))
                {
                    var hObj = new JsonObject();
                    foreach (var h in HeadersList.Where(h => !string.IsNullOrWhiteSpace(h.Key)))
                        hObj[h.Key.Trim()] = h.Value ?? "";
                    root["headers"] = hObj;
                }

                if (OptionsList.Any(o => !string.IsNullOrWhiteSpace(o.Key)))
                {
                    var oObj = new JsonObject();
                    foreach (var o in OptionsList.Where(o => !string.IsNullOrWhiteSpace(o.Key)))
                        oObj[o.Key.Trim()] = o.Value ?? "";
                    root["extra_env"] = oObj;
                }

                JsonPreviewBox.Text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch (Exception ex)
        {
            JsonPreviewBox.Text = I18nService.F("PD.PreviewErrorFmt", ex.Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Saved = false;
        _tcs.TrySetResult(false);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _tcs.TrySetResult(Saved);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(I18nService.T("PD.NameRequired"), I18nService.T("PD.MsgPrompt"), MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        var id = IdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id))
            id = MakeSlug(name);

        var isOfficial = OfficialCheck.IsChecked == true;
        var baseUrl = TrimOrNull(BaseUrlBox.Text);
        var key = TrimOrNull(GetKey());
        var notes = TrimOrNull(NotesBox.Text);
        var websiteUrl = TrimOrNull(WebsiteUrlBox.Text);
        var model = TrimOrNull(ModelCombo.Text);
        var smallFast = TrimOrNull(SmallFastCombo.Text);

        var headersDict = HeadersList
            .Where(h => !string.IsNullOrWhiteSpace(h.Key))
            .ToDictionary(h => h.Key.Trim(), h => h.Value ?? "");

        var optionsDict = OptionsList
            .Where(o => !string.IsNullOrWhiteSpace(o.Key))
            .ToDictionary(o => o.Key.Trim(), o => o.Value ?? "");

        var modelsList = ModelsList
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new ProviderModelEntry
            {
                Id = m.Id.Trim(),
                Name = m.Name?.Trim() ?? "",
                ContextWindow = string.IsNullOrWhiteSpace(m.ContextWindow) ? "1m" : m.ContextWindow.Trim()
            })
            .ToList();

        if (_mode is ProviderDialogMode.Claude or ProviderDialogMode.ClaudeDesktop)
        {
            var wireApi = (ClaudeWireCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "anthropic";
            var accessMode = (ClaudeAccessModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mapping";

            var claudeMappings = ClaudeMappings.Select(m => new ClaudeModelMapping
            {
                Role = m.Role,
                DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? (m.Model ?? "") : m.DisplayName.Trim(),
                Model = m.Model?.Trim() ?? "",
                Supports1m = m.Supports1m
            }).ToList();

            var sonnetModel = claudeMappings.FirstOrDefault(m => m.Role == "Sonnet")?.Model;
            var haikuModel = claudeMappings.FirstOrDefault(m => m.Role == "Haiku")?.Model;

            var effectiveModel = accessMode == "mapping"
                ? (TrimOrNull(sonnetModel) ?? model)
                : model;

            var effectiveSmallFast = accessMode == "mapping"
                ? (TrimOrNull(haikuModel) ?? smallFast)
                : smallFast;

            ResultClaude = new ClaudeProvider
            {
                Id = id,
                Name = name,
                Notes = notes,
                WebsiteUrl = websiteUrl,
                IsOfficial = isOfficial,
                BaseUrl = baseUrl,
                AuthToken = key,
                WireApi = wireApi,
                AccessMode = accessMode,
                ModelMappings = claudeMappings,
                Model = effectiveModel,
                SmallFastModel = effectiveSmallFast,
                CustomHeaders = headersDict,
                ExtraOptions = optionsDict,
                CustomModels = modelsList,
                ExtraEnv = optionsDict,
            };

            if (wireApi == "chat")
            {
                if (_mode == ProviderDialogMode.ClaudeDesktop && !LocalProxyServer.IsClaudeDesktopEnabled)
                    LocalProxyServer.SetClaudeDesktopEnabled(true);
                else if (_mode == ProviderDialogMode.Claude && !LocalProxyServer.IsClaudeCliEnabled)
                    LocalProxyServer.SetClaudeCliEnabled(true);
            }
            else if (_mode == ProviderDialogMode.ClaudeDesktop && accessMode == "mapping" && !LocalProxyServer.IsClaudeDesktopEnabled)
            {
                LocalProxyServer.SetClaudeDesktopEnabled(true);
            }
        }
        else if (_mode == ProviderDialogMode.Codex)
        {
            ResultCodex = new CodexProvider
            {
                Id = id,
                Name = name,
                Notes = notes,
                WebsiteUrl = websiteUrl,
                IsOfficial = isOfficial,
                BaseUrl = baseUrl,
                WireApi = GetWireApi(),
                Model = model,
                ApiKey = key,
                BearerToken = key,
                CustomHeaders = headersDict,
                ExtraOptions = optionsDict,
                CustomModels = modelsList,
            };

            if (ResultCodex.WireApi == "chat" && !LocalProxyServer.IsCodexEnabled)
            {
                LocalProxyServer.SetCodexEnabled(true);
            }
        }
        else if (_mode == ProviderDialogMode.OpenCode)
        {
            var npm = GetComboSelectedText(OcNpmCombo, "@ai-sdk/openai-compatible");

            string? modelsJson = null;
            if (modelsList.Count > 0)
            {
                var mObj = new JsonObject();
                foreach (var m in modelsList)
                    mObj[m.Id] = new JsonObject { ["name"] = m.Name };
                modelsJson = mObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            ResultOpenCode = new OpenCodeProvider
            {
                Id = id,
                Name = name,
                Notes = notes,
                WebsiteUrl = websiteUrl,
                Npm = npm,
                BaseUrl = baseUrl,
                ApiKey = key,
                CustomHeaders = headersDict,
                ExtraOptions = optionsDict,
                CustomModels = modelsList,
                ModelsJson = modelsJson,
            };
        }
        else if (_mode == ProviderDialogMode.Pi)
        {
            var api = GetComboSelectedText(PiApiCombo, "openai-completions");

            string? modelsJson = null;
            if (modelsList.Count > 0)
            {
                var mArr = new JsonArray();
                foreach (var m in modelsList)
                {
                    mArr.Add(new JsonObject
                    {
                        ["id"] = m.Id,
                        ["name"] = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name,
                    });
                }
                modelsJson = mArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            ResultPi = new PiProvider
            {
                Id = id,
                Name = name,
                Notes = notes,
                WebsiteUrl = websiteUrl,
                BaseUrl = baseUrl,
                ApiKey = key,
                Api = api,
                CustomHeaders = headersDict,
                CustomModels = modelsList,
                ModelsJson = modelsJson,
            };
        }

        Saved = true;
        _tcs.TrySetResult(true);
        Close();
    }

    private static string? TrimOrNull(string? s)
    {
        if (s == null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }

    private static string MakeSlug(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var id = sb.ToString().Trim('-');
        return id.Length > 0 ? id : "p" + Math.Abs(name.GetHashCode()).ToString("x8");
    }
}
