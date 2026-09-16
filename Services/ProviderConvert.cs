using APISwitch.Models;

namespace APISwitch.Services;

public static class ProviderConvert
{
    public static ClaudeProvider Clone(ClaudeProvider c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Notes = c.Notes,
        WebsiteUrl = c.WebsiteUrl,
        IsOfficial = c.IsOfficial,
        BaseUrl = c.BaseUrl,
        AuthToken = c.AuthToken,
        Model = c.Model,
        SmallFastModel = c.SmallFastModel,
        CustomHeaders = new Dictionary<string, string>(c.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(c.ExtraOptions),
        CustomModels = c.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        ExtraEnv = new Dictionary<string, string>(c.ExtraEnv),
    };

    public static CodexProvider ToCodex(ClaudeProvider c) => new()
    {
        Id = string.IsNullOrWhiteSpace(c.Id) ? MakeId(c.Name) : c.Id,
        Name = c.Name,
        Notes = c.Notes,
        WebsiteUrl = c.WebsiteUrl,
        IsOfficial = c.IsOfficial,
        BaseUrl = c.BaseUrl,
        WireApi = "responses",
        Model = c.Model,
        BearerToken = c.AuthToken,
        ApiKey = c.AuthToken,
        CustomHeaders = new Dictionary<string, string>(c.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(c.ExtraOptions),
        CustomModels = c.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static ClaudeProvider ToClaude(CodexProvider x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Notes = x.Notes,
        WebsiteUrl = x.WebsiteUrl,
        IsOfficial = x.IsOfficial,
        BaseUrl = x.BaseUrl,
        Model = x.Model,
        AuthToken = string.IsNullOrWhiteSpace(x.BearerToken) ? x.ApiKey : x.BearerToken,
        CustomHeaders = new Dictionary<string, string>(x.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(x.ExtraOptions),
        CustomModels = x.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static OpenCodeProvider ToOpencode(ClaudeProvider c) => new()
    {
        Id = string.IsNullOrWhiteSpace(c.Id) ? MakeId(c.Name) : c.Id,
        Name = c.Name,
        Notes = c.Notes,
        WebsiteUrl = c.WebsiteUrl,
        Npm = (c.Model ?? "").StartsWith("claude") ? "@ai-sdk/anthropic" : "@ai-sdk/openai-compatible",
        BaseUrl = c.BaseUrl,
        ApiKey = c.AuthToken,
        CustomHeaders = new Dictionary<string, string>(c.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(c.ExtraOptions),
        CustomModels = c.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        ModelsJson = null,
    };

    public static OpenCodeProvider ToOpencode(CodexProvider x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Notes = x.Notes,
        WebsiteUrl = x.WebsiteUrl,
        Npm = "@ai-sdk/openai-compatible",
        BaseUrl = x.BaseUrl,
        ApiKey = string.IsNullOrWhiteSpace(x.ApiKey) ? x.BearerToken : x.ApiKey,
        CustomHeaders = new Dictionary<string, string>(x.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(x.ExtraOptions),
        CustomModels = x.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static ClaudeProvider ToClaude(OpenCodeProvider o) => new()
    {
        Id = o.Id,
        Name = o.Name ?? o.Id,
        Notes = o.Notes,
        WebsiteUrl = o.WebsiteUrl,
        BaseUrl = o.BaseUrl,
        AuthToken = o.ApiKey,
        CustomHeaders = new Dictionary<string, string>(o.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(o.ExtraOptions),
        CustomModels = o.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static CodexProvider ToCodex(OpenCodeProvider o) => new()
    {
        Id = o.Id,
        Name = o.Name ?? o.Id,
        Notes = o.Notes,
        WebsiteUrl = o.WebsiteUrl,
        BaseUrl = o.BaseUrl,
        WireApi = "responses",
        ApiKey = o.ApiKey,
        BearerToken = o.ApiKey,
        CustomHeaders = new Dictionary<string, string>(o.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(o.ExtraOptions),
        CustomModels = o.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
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
