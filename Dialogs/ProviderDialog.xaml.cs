using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using APISwitch.Models;

namespace APISwitch.Dialogs;

public enum ProviderDialogMode { Claude, ClaudeDesktop, Codex, OpenCode }

public partial class ProviderDialog : Window
{
    readonly ProviderDialogMode _mode;

    public ClaudeProvider? ResultClaude { get; private set; }
    public CodexProvider? ResultCodex { get; private set; }
    public OpenCodeProvider? ResultOpenCode { get; private set; }

    public ProviderDialog(ProviderDialogMode mode, object? existing)
    {
        InitializeComponent();
        _mode = mode;

        if (mode == ProviderDialogMode.Claude)
        {
            Title = "Claude Code (CLI) 供应商";
            CodexPanel.Visibility = Visibility.Collapsed;
            OpenCodePanel.Visibility = Visibility.Collapsed;
            KeyLabel.Content = "Auth Token（写入 ANTHROPIC_AUTH_TOKEN）";
        }
        else if (mode == ProviderDialogMode.ClaudeDesktop)
        {
            Title = "Claude 客户端供应商";
            CodexPanel.Visibility = Visibility.Collapsed;
            OpenCodePanel.Visibility = Visibility.Collapsed;
            KeyLabel.Content = "Auth Token（写入 inferenceGatewayApiKey）";
        }
        else if (mode == ProviderDialogMode.OpenCode)
        {
            Title = "OpenCode 供应商";
            ClaudePanel.Visibility = Visibility.Collapsed;
            CodexPanel.Visibility = Visibility.Collapsed;
            OfficialCheck.Visibility = Visibility.Collapsed;
            BaseUrlLabel.Content = "baseURL";
            KeyLabel.Content = "apiKey";

            if (existing is OpenCodeProvider ocExist)
            {
                OcIdBox.Text = ocExist.Id;
                OcNpmBox.Text = ocExist.Npm;
                BaseUrlBox.Text = ocExist.BaseUrl;
                KeyBox.Text = ocExist.ApiKey;
                OcModelsBox.Text = ocExist.ModelsJson;
                NameBox.Text = ocExist.Name;
            }
        }
        else
        {
            Title = "Codex 供应商";
            ClaudePanel.Visibility = Visibility.Collapsed;
            OpenCodePanel.Visibility = Visibility.Collapsed;
            KeyLabel.Content = "API Key（写入 auth.json 的 OPENAI_API_KEY）";
        }

        if (existing is ClaudeProvider c)
        {
            NameBox.Text = c.Name;
            OfficialCheck.IsChecked = c.IsOfficial;
            BaseUrlBox.Text = c.BaseUrl;
            KeyBox.Text = c.AuthToken;
            ModelBox.Text = c.Model;
            SmallFastBox.Text = c.SmallFastModel;
            if (c.ExtraEnv.Count > 0)
                ExtraEnvBox.Text = JsonSerializer.Serialize(c.ExtraEnv, new JsonSerializerOptions { WriteIndented = true });
        }
        else if (existing is CodexProvider x)
        {
            NameBox.Text = x.Name;
            OfficialCheck.IsChecked = x.IsOfficial;
            BaseUrlBox.Text = x.BaseUrl;
            KeyBox.Text = x.ApiKey;
            ModelBox.Text = x.Model;
            BearerBox.Text = x.BearerToken;
            ChatRadio.IsChecked = x.WireApi == "chat";
            ResponsesRadio.IsChecked = x.WireApi != "chat";
        }

        OfficialCheck_Changed(this, new RoutedEventArgs());
    }

    void OfficialCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (FieldsPanel != null)
            FieldsPanel.Visibility = OfficialCheck.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("请填写名称", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var official = OfficialCheck.IsChecked == true;

        if (_mode is ProviderDialogMode.Claude or ProviderDialogMode.ClaudeDesktop)
        {
            var extra = new Dictionary<string, string>();
            var extraText = ExtraEnvBox.Text.Trim();
            if (extraText.Length > 0)
            {
                try
                {
                    extra = JsonSerializer.Deserialize<Dictionary<string, string>>(extraText) ?? new();
                }
                catch
                {
                    MessageBox.Show("额外环境变量必须是合法 JSON 对象", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ResultClaude = new ClaudeProvider
            {
                Name = name,
                IsOfficial = official,
                BaseUrl = TrimOrNull(BaseUrlBox.Text),
                AuthToken = TrimOrNull(KeyBox.Text),
                Model = TrimOrNull(ModelBox.Text),
                SmallFastModel = TrimOrNull(SmallFastBox.Text),
                ExtraEnv = extra,
            };
        }
        else if (_mode == ProviderDialogMode.OpenCode)
        {
            var id = TrimOrNull(OcIdBox.Text);
            if (id == null)
            {
                MessageBox.Show("请填写 Provider ID", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var modelsText = TrimOrNull(OcModelsBox.Text);
            if (modelsText != null)
            {
                try
                {
                    if (JsonNode.Parse(modelsText) is not JsonObject)
                    {
                        MessageBox.Show("模型配置必须是 JSON 对象", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                catch
                {
                    MessageBox.Show("模型配置必须是合法 JSON 对象", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            ResultOpenCode = new OpenCodeProvider
            {
                Id = id,
                Name = TrimOrNull(NameBox.Text) ?? id,
                Npm = TrimOrNull(OcNpmBox.Text) ?? "@ai-sdk/openai-compatible",
                BaseUrl = TrimOrNull(BaseUrlBox.Text),
                ApiKey = TrimOrNull(KeyBox.Text),
                ModelsJson = modelsText,
            };
        }
        else
        {
            ResultCodex = new CodexProvider
            {
                Name = name,
                IsOfficial = official,
                BaseUrl = TrimOrNull(BaseUrlBox.Text),
                WireApi = ChatRadio.IsChecked == true ? "chat" : "responses",
                Model = TrimOrNull(ModelBox.Text),
                ApiKey = TrimOrNull(KeyBox.Text),
                BearerToken = TrimOrNull(BearerBox.Text),
            };
        }

        DialogResult = true;
    }

    static string? TrimOrNull(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? null : s;
    }
}
