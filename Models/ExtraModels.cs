using System.Text.Json.Serialization;

namespace APISwitch.Models;

public class OpenCodeProvider
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public string? WebsiteUrl { get; set; }
    public string Npm { get; set; } = "@ai-sdk/openai-compatible";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    public Dictionary<string, string> ExtraOptions { get; set; } = new();
    public List<ProviderModelEntry> CustomModels { get; set; } = new();
    public string? ModelsJson { get; set; }
}

public class PiAccount
{
    public string Name { get; set; } = "";
    public string DefaultProvider { get; set; } = "";
    public string DefaultModel { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public string AuthJson { get; set; } = "{}";
    public string SettingsJson { get; set; } = "{}";

    [JsonIgnore]
    public bool IsCurrent { get; set; }

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(Name) ? "P" : Name.Substring(0, 1).ToUpperInvariant();

    [JsonIgnore]
    public string CapturedAtLocal => CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string SubText => string.Join("   ·   ",
        new[] { DefaultProvider, DefaultModel }.Where(s => !string.IsNullOrEmpty(s)));
}

public class PiProvider
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string Api { get; set; } = "openai-completions";
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    public List<ProviderModelEntry> CustomModels { get; set; } = new();
    public string? ModelsJson { get; set; }
}
