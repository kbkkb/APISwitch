namespace APISwitch.Services;

/// <summary>
/// 统一的「思考强度」档位模型。
/// 存储值约定：null / "" = 跟随客户端默认（不写任何配置）；
/// 其余取值 low / medium / high / xhigh（xhigh 仅 Codex / Responses 系支持）。
/// </summary>
public static class ThinkingEffort
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";

    /// <summary>Codex（config.toml model_reasoning_effort：minimal|low|medium|high|xhigh）</summary>
    public static readonly string[] CodexLevels = { Low, Medium, High, XHigh };

    /// <summary>其余客户端（Claude 预算制 / Pi / OpenCode）</summary>
    public static readonly string[] StandardLevels = { Low, Medium, High };

    public static bool IsValid(string? v) =>
        string.IsNullOrEmpty(v) || CodexLevels.Contains(v);

    /// <summary>档位强度序（用于取集合中的最高档）。</summary>
    public static int Rank(string? v) => v switch
    {
        Low => 1,
        Medium => 2,
        High => 3,
        XHigh => 4,
        _ => 0,
    };

    /// <summary>取集合中的最高档；全部为空返回 null。</summary>
    public static string? MaxOf(IEnumerable<string?> efforts)
    {
        string? best = null;
        foreach (var e in efforts)
        {
            if (string.IsNullOrEmpty(e)) continue;
            if (best == null || Rank(e) > Rank(best)) best = e;
        }
        return best;
    }

    /// <summary>Codex 直接透传档位；null 表示不写</summary>
    public static string? ToCodex(string? v) =>
        v is Low or Medium or High or XHigh ? v : null;

    /// <summary>Pi defaultThinkingLevel（off/minimal/low/medium/high/xhigh/max）</summary>
    public static string? ToPi(string? v) =>
        v is Low or Medium or High or XHigh ? v : null;

    /// <summary>
    /// Anthropic 预算制档位 → thinking.budget_tokens / MAX_THINKING_TOKENS。
    /// 预算需 ≥1024 且小于输出上限；xhigh 在 Claude 侧不可用（调用方不应传入）。
    /// </summary>
    public static long? ToClaudeBudgetTokens(string? v) => v switch
    {
        Low => 4096,
        Medium => 10240,
        High => 32768,
        _ => null,
    };

    /// <summary>OpenCode Anthropic 适配器（thinking.budgetTokens）</summary>
    public static long? ToOpenCodeAnthropicBudget(string? v) => ToClaudeBudgetTokens(v);

    /// <summary>OpenAI 兼容适配器（reasoning_effort 枚举，xhigh 仅部分模型支持，由上游决定）</summary>
    public static string? ToOpenAiReasoningEffort(string? v) =>
        v is Low or Medium or High or XHigh ? v : null;
}
