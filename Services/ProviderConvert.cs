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
        WireApi = c.WireApi,
        AccessMode = c.AccessMode,
        ModelMappings = c.ModelMappings.Select(m => new ClaudeModelMapping
        {
            Role = m.Role,
            DisplayName = m.DisplayName,
            Model = m.Model,
            Supports1m = m.Supports1m
        }).ToList(),
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
        WireApi = c.WireApi == "chat" ? "chat" : "responses",
        Model = c.Model,
        BearerToken = c.AuthToken,
        ApiKey = c.AuthToken,
        CustomHeaders = new Dictionary<string, string>(c.CustomHeaders),
        ExtraOptions = new Dictionary<string, string>(c.ExtraOptions),
        CustomModels = c.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static ClaudeProvider ToClaude(CodexProvider x)
    {
        var p = new ClaudeProvider
        {
            Id = x.Id,
            Name = x.Name,
            Notes = x.Notes,
            WebsiteUrl = x.WebsiteUrl,
            IsOfficial = x.IsOfficial,
            BaseUrl = x.BaseUrl,
            Model = x.Model,
            WireApi = x.WireApi == "chat" ? "chat" : "anthropic",
            AccessMode = "mapping",
            AuthToken = string.IsNullOrWhiteSpace(x.BearerToken) ? x.ApiKey : x.BearerToken,
            CustomHeaders = new Dictionary<string, string>(x.CustomHeaders),
            ExtraOptions = new Dictionary<string, string>(x.ExtraOptions),
            CustomModels = x.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        };

        var mName = x.Model ?? (x.CustomModels.FirstOrDefault()?.Id ?? "");
        p.ModelMappings = new List<ClaudeModelMapping>
        {
            new() { Role = "Sonnet", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Opus", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Fable", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Haiku", DisplayName = mName, Model = mName, Supports1m = true },
        };
        return p;
    }

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

    public static ClaudeProvider ToClaude(OpenCodeProvider o)
    {
        var p = new ClaudeProvider
        {
            Id = o.Id,
            Name = o.Name ?? o.Id,
            Notes = o.Notes,
            WebsiteUrl = o.WebsiteUrl,
            BaseUrl = o.BaseUrl,
            AuthToken = o.ApiKey,
            WireApi = "anthropic",
            AccessMode = "mapping",
            CustomHeaders = new Dictionary<string, string>(o.CustomHeaders),
            ExtraOptions = new Dictionary<string, string>(o.ExtraOptions),
            CustomModels = o.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        };

        var mName = o.CustomModels.FirstOrDefault()?.Id ?? "";
        p.ModelMappings = new List<ClaudeModelMapping>
        {
            new() { Role = "Sonnet", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Opus", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Fable", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Haiku", DisplayName = mName, Model = mName, Supports1m = true },
        };
        return p;
    }

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

    public static PiProvider Clone(PiProvider p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Notes = p.Notes,
        WebsiteUrl = p.WebsiteUrl,
        BaseUrl = p.BaseUrl,
        ApiKey = p.ApiKey,
        Api = p.Api,
        CustomHeaders = new Dictionary<string, string>(p.CustomHeaders),
        CustomModels = p.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        ModelsJson = p.ModelsJson,
    };

    public static PiProvider ToPi(ClaudeProvider c) => new()
    {
        Id = string.IsNullOrWhiteSpace(c.Id) ? MakeId(c.Name) : c.Id,
        Name = c.Name,
        Notes = c.Notes,
        WebsiteUrl = c.WebsiteUrl,
        BaseUrl = c.BaseUrl,
        ApiKey = c.AuthToken,
        Api = "openai-completions",
        CustomHeaders = new Dictionary<string, string>(c.CustomHeaders),
        CustomModels = c.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static PiProvider ToPi(CodexProvider x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Notes = x.Notes,
        WebsiteUrl = x.WebsiteUrl,
        BaseUrl = x.BaseUrl,
        ApiKey = string.IsNullOrWhiteSpace(x.BearerToken) ? x.ApiKey : x.BearerToken,
        Api = "openai-completions",
        CustomHeaders = new Dictionary<string, string>(x.CustomHeaders),
        CustomModels = x.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
    };

    public static PiProvider ToPi(OpenCodeProvider o) => new()
    {
        Id = o.Id,
        Name = o.Name ?? o.Id,
        Notes = o.Notes,
        WebsiteUrl = o.WebsiteUrl,
        BaseUrl = o.BaseUrl,
        ApiKey = o.ApiKey,
        Api = "openai-completions",
        CustomHeaders = new Dictionary<string, string>(o.CustomHeaders),
        CustomModels = o.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        ModelsJson = o.ModelsJson,
    };

    public static OpenCodeProvider ToOpencode(PiProvider p) => new()
    {
        Id = p.Id,
        Name = p.Name ?? p.Id,
        Notes = p.Notes,
        WebsiteUrl = p.WebsiteUrl,
        Npm = "@ai-sdk/openai-compatible",
        BaseUrl = p.BaseUrl,
        ApiKey = p.ApiKey,
        CustomHeaders = new Dictionary<string, string>(p.CustomHeaders),
        CustomModels = p.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        ModelsJson = p.ModelsJson,
    };

    public static ClaudeProvider ToClaude(PiProvider p)
    {
        var c = new ClaudeProvider
        {
            Id = p.Id,
            Name = p.Name ?? p.Id,
            Notes = p.Notes,
            WebsiteUrl = p.WebsiteUrl,
            BaseUrl = p.BaseUrl,
            AuthToken = p.ApiKey,
            WireApi = "chat",
            AccessMode = "mapping",
            CustomHeaders = new Dictionary<string, string>(p.CustomHeaders),
            CustomModels = p.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
        };
        var mName = p.CustomModels.FirstOrDefault()?.Id ?? "";
        c.ModelMappings = new List<ClaudeModelMapping>
        {
            new() { Role = "Sonnet", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Opus", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Fable", DisplayName = mName, Model = mName, Supports1m = true },
            new() { Role = "Haiku", DisplayName = mName, Model = mName, Supports1m = true },
        };
        return c;
    }

    public static CodexProvider ToCodex(PiProvider p) => new()
    {
        Id = p.Id,
        Name = p.Name ?? p.Id,
        Notes = p.Notes,
        WebsiteUrl = p.WebsiteUrl,
        BaseUrl = p.BaseUrl,
        WireApi = "responses",
        ApiKey = p.ApiKey,
        BearerToken = p.ApiKey,
        CustomHeaders = new Dictionary<string, string>(p.CustomHeaders),
        CustomModels = p.CustomModels.Select(m => new ProviderModelEntry { Id = m.Id, Name = m.Name, ContextWindow = m.ContextWindow }).ToList(),
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
