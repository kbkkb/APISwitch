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

public enum ProviderDialogMode { Claude, ClaudeDesktop, Codex, OpenCode }

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

    private void ConfigureMode(ProviderDialogMode mode, object? existing)
    {
        bool isEdit = existing != null;
        switch (mode)
        {
            case ProviderDialogMode.Claude:
                DialogTitleText.Text = isEdit ? "编辑供应商" : "添加供应商";
                CategoryBadgeText.Text = "Claude CLI";
                CategoryBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));
                CategoryBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                KeyLabel.Text = "Auth Token (写入 ANTHROPIC_AUTH_TOKEN) *";
                KeyHintText.Text = "系统将写入 ~/.claude/settings.json 中的 env 配置";
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Visible;
                ClaudeConfigDescText.Text = "为 Claude Code CLI 配置模型映射与上游格式。留空的档会自动沿用 Sonnet 模型，确保子 agent 调用的 Haiku 始终可用。";
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
                DialogTitleText.Text = isEdit ? "编辑供应商" : "添加供应商";
                CategoryBadgeText.Text = "Claude 客户端";
                CategoryBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));
                CategoryBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
                KeyLabel.Text = "Auth Token (写入 inferenceGatewayApiKey) *";
                KeyHintText.Text = "系统将写入 Claude Desktop 网关凭据与模型列表";
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Visible;
                ClaudeConfigDescText.Text = "Claude Desktop 只接受 claude-sonnet-* / claude-opus-* / claude-haiku-* 三档角色 ID。选择模型映射后，CC Switch 会把这三档映射到供应商的实际模型，并在使用期间保持本地路由开启。";
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
                DialogTitleText.Text = isEdit ? "编辑供应商" : "添加供应商";
                CategoryBadgeText.Text = "Codex";
                CategoryBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7));
                CategoryBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));
                KeyLabel.Text = "API Key / 访问令牌 (Token) *";
                KeyHintText.Text = "系统将自动写入 auth.json 与 config.toml（双轨鉴权），并配置 disable_response_storage";
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
                DialogTitleText.Text = isEdit ? "编辑供应商" : "添加供应商";
                CategoryBadgeText.Text = "OpenCode";
                CategoryBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF2, 0xFF));
                CategoryBadgeText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0x46, 0xE5));
                OfficialCheck.Visibility = Visibility.Collapsed;
                KeyLabel.Text = "apiKey *";
                KeyHintText.Text = "系统将写入 ~/.config/opencode/opencode.json 的 options 配置";
                FormatPanel.Visibility = Visibility.Visible;
                ClaudeConfigPanel.Visibility = Visibility.Collapsed;
                CodexWirePanel.Visibility = Visibility.Collapsed;
                OpenCodeFormatPanel.Visibility = Visibility.Visible;
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
            IdHintText.Text = "该供应商已添加到应用配置中，供应商标识不可修改";
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
            OcNpmCombo.Text = string.IsNullOrWhiteSpace(oc.Npm) ? "@ai-sdk/openai-compatible" : oc.Npm;

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
        else
        {
            if (_mode == ProviderDialogMode.OpenCode)
            {
                OcNpmCombo.Text = "@ai-sdk/openai-compatible";
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
        OcNpmCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => UpdatePreview()));
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

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStates();
        UpdatePreview();
    }

    private void UpdateEmptyStates()
    {
        if (EmptyHeadersText != null) EmptyHeadersText.Visibility = HeadersList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyOptionsText != null) EmptyOptionsText.Visibility = OptionsList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyModelsText != null) EmptyModelsText.Visibility = ModelsList.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddHeader_Click(object sender, RoutedEventArgs e)
    {
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
            SetStatus("请先填写 Base URL", isError: true);
            BaseUrlBox.Focus();
            return;
        }

        var apiKey = GetKey().Trim();
        FetchModelsBtn.IsEnabled = false;
        FetchModelsBtnText.Text = "获取中...";
        if (ClaudeFetchModelsBtnText != null) ClaudeFetchModelsBtnText.Text = "获取中...";
        SetStatus("正在从服务器获取可用模型列表...");

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
                FetchedModelsTitleText.Text = $"已从端点暂存 {models.Count} 个可用模型";
                AddAllFetchedBtn.Content = $"一键全部添加 ({models.Count})";

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

                SetStatus($"成功获取 {models.Count} 个模型，可直接在各档模型下拉选择", isError: false);
            }
            else
            {
                SetStatus("端点未返回可用模型", isError: true);
            }

            UpdateEmptyStates();
            UpdatePreview();
        }
        catch (Exception ex)
        {
            SetStatus($"获取失败：{ex.Message}", isError: true);
        }
        finally
        {
            FetchModelsBtn.IsEnabled = true;
            FetchModelsBtnText.Text = "获取模型列表";
            if (ClaudeFetchModelsBtnText != null) ClaudeFetchModelsBtnText.Text = "获取模型列表";
        }
    }

    private void AddSelectedFetchedModel_Click(object sender, RoutedEventArgs e)
    {
        var modelId = SelectModelToAddCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            SetStatus("请先在下拉框中选择要添加的模型", isError: true);
            return;
        }

        if (ModelsList.Any(existing => string.Equals(existing.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"模型 {modelId} 已在列表中", isError: true);
            return;
        }

        var entry = new ProviderModelEntry { Id = modelId, Name = "", ContextWindow = "1m" };
        entry.PropertyChanged += (_, _) => UpdatePreview();
        ModelsList.Add(entry);
        UpdateEmptyStates();
        UpdatePreview();
        SetStatus($"已添加模型 {modelId}", isError: false);
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
        SetStatus($"已将暂存的 {added} 个新模型添加到配置列表", isError: false);
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
                    ["npm"] = string.IsNullOrWhiteSpace(OcNpmCombo.Text) ? "@ai-sdk/openai-compatible" : OcNpmCombo.Text.Trim()
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
            JsonPreviewBox.Text = $"// 预览生成异常：{ex.Message}";
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
            MessageBox.Show("请填写供应商名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var npm = TrimOrNull(OcNpmCombo.Text) ?? "@ai-sdk/openai-compatible";

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
