using APISwitch.Models;

namespace APISwitch.Services;

public static class ProviderConvert
{
    public static ClaudeProvider Clone(ClaudeProvider c) => new()
    {
        Name = c.Name,
        IsOfficial = c.IsOfficial,
        BaseUrl = c.BaseUrl,
        AuthToken = c.AuthToken,
        Model = c.Model,
        SmallFastModel = c.SmallFastModel,
        ExtraEnv = new Dictionary<string, string>(c.ExtraEnv),
    };

    public static CodexProvider ToCodex(ClaudeProvider c) => new()
    {
        Name = c.Name,
        IsOfficial = c.IsOfficial,
        BaseUrl = c.BaseUrl,
        WireApi = "responses",
        Model = c.Model,
        BearerToken = c.AuthToken,
        ApiKey = c.AuthToken,
    };

    public static ClaudeProvider ToClaude(CodexProvider x) => new()
    {
        Name = x.Name,
        IsOfficial = x.IsOfficial,
        BaseUrl = x.BaseUrl,
        Model = x.Model,
        AuthToken = string.IsNullOrWhiteSpace(x.BearerToken) ? x.ApiKey : x.BearerToken,
    };

    public static OpenCodeProvider ToOpencode(ClaudeProvider c) => new()
    {
        Id = MakeId(c.Name),
        Name = c.Name,
        Npm = (c.Model ?? "").StartsWith("claude") ? "@ai-sdk/anthropic" : "@ai-sdk/openai-compatible",
        BaseUrl = c.BaseUrl,
        ApiKey = c.AuthToken,
        ModelsJson = null,
    };

    public static OpenCodeProvider ToOpencode(CodexProvider x) => new()
    {
        Id = MakeId(x.Name),
        Name = x.Name,
        Npm = "@ai-sdk/openai-compatible",
        BaseUrl = x.BaseUrl,
        ApiKey = string.IsNullOrWhiteSpace(x.ApiKey) ? x.BearerToken : x.ApiKey,
    };

    public static ClaudeProvider ToClaude(OpenCodeProvider o) => new()
    {
        Name = o.Name ?? o.Id,
        BaseUrl = o.BaseUrl,
        AuthToken = o.ApiKey,
    };

    public static CodexProvider ToCodex(OpenCodeProvider o) => new()
    {
        Name = o.Name ?? o.Id,
        BaseUrl = o.BaseUrl,
        WireApi = "responses",
        ApiKey = o.ApiKey,
        BearerToken = o.ApiKey,
    };

    static string MakeId(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var id = sb.ToString().Trim('-');
        return id.Length > 0 ? id : "p" + Math.Abs(name.GetHashCode()).ToString("x8");
    }
}
