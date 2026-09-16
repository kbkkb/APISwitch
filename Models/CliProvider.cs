using System.Text;
using System.Text.Json.Serialization;

namespace APISwitch.Models;

public class ClaudeProvider
{
    public string Name { get; set; } = "";
    public bool IsOfficial { get; set; }
    public string? BaseUrl { get; set; }
    public string? AuthToken { get; set; }
    public string? Model { get; set; }
    public string? SmallFastModel { get; set; }
    public Dictionary<string, string> ExtraEnv { get; set; } = new();
}

public class CodexProvider
{
    public string Name { get; set; } = "";
    public bool IsOfficial { get; set; }
    public string? BaseUrl { get; set; }
    public string WireApi { get; set; } = "responses";
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }

    [JsonIgnore]
    public string Id => MakeId(Name);

    static string MakeId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var id = sb.ToString().Trim('-');
        return id.Length > 0 ? id : "p" + Math.Abs(name.GetHashCode()).ToString("x8");
    }
}
