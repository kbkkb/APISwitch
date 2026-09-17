using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace APISwitch.Models;

public class ProviderModelEntry : INotifyPropertyChanged
{
    private string _id = "";
    private string _name = "";

    public string Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    private string _contextWindow = "1m";
    public string ContextWindow
    {
        get => string.IsNullOrWhiteSpace(_contextWindow) ? "1m" : _contextWindow;
        set { if (_contextWindow != value) { _contextWindow = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class KeyValueItem : INotifyPropertyChanged
{
    private string _key = "";
    private string _value = "";

    public string Key
    {
        get => _key;
        set { if (_key != value) { _key = value; OnPropertyChanged(); } }
    }

    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ClaudeModelMapping : INotifyPropertyChanged
{
    private string _role = "";
    private string _displayName = "";
    private string _model = "";
    private bool _supports1m = true;

    public string Role
    {
        get => _role;
        set { if (_role != value) { _role = value; OnPropertyChanged(); } }
    }

    public string DisplayName
    {
        get => _displayName;
        set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public string Model
    {
        get => _model;
        set
        {
            if (_model != value)
            {
                var oldModel = _model;
                _model = value;
                OnPropertyChanged();
                if (string.IsNullOrWhiteSpace(_displayName) || _displayName == oldModel)
                {
                    DisplayName = _model;
                }
            }
        }
    }

    public bool Supports1m
    {
        get => _supports1m;
        set { if (_supports1m != value) { _supports1m = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class ClaudeProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string? WebsiteUrl { get; set; }
    public bool IsOfficial { get; set; }
    public string? BaseUrl { get; set; }
    public string? AuthToken { get; set; }
    public string WireApi { get; set; } = "anthropic";
    public string AccessMode { get; set; } = "mapping";
    public List<ClaudeModelMapping> ModelMappings { get; set; } = new();
    public string? Model { get; set; }
    public string? SmallFastModel { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    public Dictionary<string, string> ExtraOptions { get; set; } = new();
    public List<ProviderModelEntry> CustomModels { get; set; } = new();
    public Dictionary<string, string> ExtraEnv { get; set; } = new();
}

public class CodexProvider
{
    private string? _id;

    public string Id
    {
        get => !string.IsNullOrWhiteSpace(_id) ? _id : MakeId(Name);
        set => _id = value;
    }

    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string? WebsiteUrl { get; set; }
    public bool IsOfficial { get; set; }
    public string? BaseUrl { get; set; }
    public string WireApi { get; set; } = "responses";
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    public Dictionary<string, string> ExtraOptions { get; set; } = new();
    public List<ProviderModelEntry> CustomModels { get; set; } = new();

    static string MakeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var id = sb.ToString().Trim('-');
        return id.Length > 0 ? id : "p" + Math.Abs(name.GetHashCode()).ToString("x8");
    }
}

