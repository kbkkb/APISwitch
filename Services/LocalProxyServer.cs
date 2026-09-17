using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

/// <summary>
/// 本地轻量级透明代理与协议转译路由服务。
/// 支持：
/// 1. 协议转译：将 Codex 客户端的 Responses API 请求实时重写为 Chat Completions 协议并发往上游中转，并将 SSE 流实时包装回 Responses SSE 流（支持 reasoning 深度思考流与工具调用）；
/// 2. 直通转发：当上游支持原生 Responses API 时透明转发；
/// 3. 零重启热切换：Codex 客户端常驻指向 127.0.0.1，切换供应商时无需重启客户端即可无缝生效。
/// </summary>
public static class LocalProxyServer
{
    private static HttpListener? _listener;
    private static bool _isRunning;
    private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static bool IsCodexEnabled { get; private set; } = true;
    public static bool IsClaudeCliEnabled { get; private set; } = false;
    public static bool IsClaudeDesktopEnabled { get; private set; } = false;
    public static bool IsEnabled => IsCodexEnabled || IsClaudeCliEnabled || IsClaudeDesktopEnabled;

    public static int Port { get; private set; } = 15725;
    public static string ProxyCodexUrl => $"http://127.0.0.1:{Port}/codex/v1";
    public static string ProxyClaudeCliUrl => $"http://127.0.0.1:{Port}/claude-cli";
    public static string ProxyClaudeDesktopUrl => $"http://127.0.0.1:{Port}/claude-desktop";
    public static string ProxyUrl => ProxyCodexUrl; // Legacy alias for Codex

    public static CodexProvider? ActiveCodexProvider { get; set; }
    public static ClaudeProvider? ActiveClaudeCliProvider { get; set; }
    public static ClaudeProvider? ActiveClaudeDesktopProvider { get; set; }

    public static event Action? StateChanged;

    public static void Initialize()
    {
        try
        {
            var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
            Directory.CreateDirectory(logDir);
            var settings = CliStore.LoadProxySettings();
            IsCodexEnabled = settings.CodexEnabled;
            IsClaudeCliEnabled = settings.ClaudeCliEnabled;
            IsClaudeDesktopEnabled = settings.ClaudeDesktopEnabled;
            Port = settings.Port > 0 ? settings.Port : 15725;

            ActiveCodexProvider = CodexCli.GetActiveProvider();
            ActiveClaudeCliProvider = ClaudeCli.GetActiveProvider();
            ActiveClaudeDesktopProvider = ClaudeDesktopCli.GetActiveProvider();

            File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] Initialize: CodexEnabled={IsCodexEnabled}, ClaudeCliEnabled={IsClaudeCliEnabled}, ClaudeDesktopEnabled={IsClaudeDesktopEnabled}, Port={Port}\n");

            if (IsEnabled)
            {
                Start(Port);
                if (IsCodexEnabled && ActiveCodexProvider != null && !ActiveCodexProvider.IsOfficial)
                {
                    try { CodexCli.Apply(ActiveCodexProvider); } catch { }
                }
                if (IsClaudeCliEnabled && ActiveClaudeCliProvider != null && !ActiveClaudeCliProvider.IsOfficial)
                {
                    try { ClaudeCli.Apply(ActiveClaudeCliProvider); } catch { }
                }
                if (IsClaudeDesktopEnabled && ActiveClaudeDesktopProvider != null && !ActiveClaudeDesktopProvider.IsOfficial)
                {
                    try { ClaudeDesktopCli.Apply(ActiveClaudeDesktopProvider); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] Initialize exception: {ex}\n");
            }
            catch { }
        }
    }

    public static void Start(int? preferredPort = null)
    {
        if (_isRunning) return;

        int port = preferredPort ?? Port;
        bool started = false;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                _isRunning = true;
                started = true;
                try
                {
                    var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] Started successfully on port {Port}\n");
                }
                catch { }
                break;
            }
            catch (Exception ex)
            {
                try
                {
                    var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] Port {port} failed: {ex}\n");
                }
                catch { }
                try { _listener?.Close(); } catch { }
                port++;
            }
        }

        if (!started)
        {
            _isRunning = false;
            return;
        }

        SaveSettings();

        _ = Task.Run(AcceptLoopAsync);
        StateChanged?.Invoke();
    }

    public static void SetCodexEnabled(bool enabled)
    {
        IsCodexEnabled = enabled;
        SaveSettings();
        if (enabled)
        {
            Start(Port);
            try { CodexCli.ReapplyCurrent(); } catch { }
        }
        else
        {
            try { CodexCli.ReapplyCurrent(); } catch { }
            if (!IsEnabled) StopServerOnly();
        }
        StateChanged?.Invoke();
    }

    public static void SetClaudeCliEnabled(bool enabled)
    {
        IsClaudeCliEnabled = enabled;
        SaveSettings();
        if (enabled)
        {
            Start(Port);
            try { ClaudeCli.ReapplyCurrent(); } catch { }
        }
        else
        {
            try { ClaudeCli.ReapplyCurrent(); } catch { }
            if (!IsEnabled) StopServerOnly();
        }
        StateChanged?.Invoke();
    }

    public static void SetClaudeDesktopEnabled(bool enabled)
    {
        IsClaudeDesktopEnabled = enabled;
        SaveSettings();
        if (enabled)
        {
            Start(Port);
            try { ClaudeDesktopCli.ReapplyCurrent(); } catch { }
        }
        else
        {
            try { ClaudeDesktopCli.ReapplyCurrent(); } catch { }
            if (!IsEnabled) StopServerOnly();
        }
        StateChanged?.Invoke();
    }

    public static void SetEnabled(bool enabled)
    {
        IsCodexEnabled = enabled;
        IsClaudeCliEnabled = enabled;
        IsClaudeDesktopEnabled = enabled;
        SaveSettings();

        if (enabled)
        {
            Start(Port);
            try { CodexCli.ReapplyCurrent(); } catch { }
            try { ClaudeCli.ReapplyCurrent(); } catch { }
            try { ClaudeDesktopCli.ReapplyCurrent(); } catch { }
        }
        else
        {
            try { CodexCli.ReapplyCurrent(); } catch { }
            try { ClaudeCli.ReapplyCurrent(); } catch { }
            try { ClaudeDesktopCli.ReapplyCurrent(); } catch { }
            StopServerOnly();
        }

        StateChanged?.Invoke();
    }

    public static void Stop(bool restoreDirectConfig = true)
    {
        StopServerOnly();
        if (restoreDirectConfig)
        {
            IsCodexEnabled = false;
            IsClaudeCliEnabled = false;
            IsClaudeDesktopEnabled = false;
            SaveSettings();
            try { CodexCli.ReapplyCurrent(); } catch { }
            try { ClaudeCli.ReapplyCurrent(); } catch { }
            try { ClaudeDesktopCli.ReapplyCurrent(); } catch { }
        }
        StateChanged?.Invoke();
    }

    private static void StopServerOnly()
    {
        _isRunning = false;
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
    }

    private static void SaveSettings()
    {
        try
        {
            var settings = CliStore.LoadProxySettings();
            settings.CodexEnabled = IsCodexEnabled;
            settings.ClaudeCliEnabled = IsClaudeCliEnabled;
            settings.ClaudeDesktopEnabled = IsClaudeDesktopEnabled;
            settings.Enabled = IsEnabled;
            settings.Port = Port;
            CliStore.SaveProxySettings(settings);
        }
        catch { }
    }

    private static async Task AcceptLoopAsync()
    {
        while (_isRunning && _listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleContextSafeAsync(ctx));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalProxyServer] Accept loop error: {ex.Message}");
            }
        }
    }

    private static async Task HandleContextSafeAsync(HttpListenerContext ctx)
    {
        try
        {
            // Global CORS headers
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS, PUT, DELETE, PATCH";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";

            if (ctx.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var normalizedPath = path.TrimEnd('/');

            // 1. Health check & status
            if (normalizedPath == "" || normalizedPath.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, 200, new JsonObject
                {
                    ["status"] = "ok",
                    ["server"] = "APISwitch Local Proxy Router",
                    ["port"] = Port,
                    ["codex"] = new JsonObject
                    {
                        ["enabled"] = IsCodexEnabled,
                        ["active_provider"] = (ActiveCodexProvider ?? CodexCli.GetActiveProvider())?.Name ?? "none",
                        ["wire_api"] = (ActiveCodexProvider ?? CodexCli.GetActiveProvider())?.WireApi ?? "responses",
                    },
                    ["claude_cli"] = new JsonObject
                    {
                        ["enabled"] = IsClaudeCliEnabled,
                        ["active_provider"] = (ActiveClaudeCliProvider ?? ClaudeCli.GetActiveProvider())?.Name ?? "none",
                    },
                    ["claude_desktop"] = new JsonObject
                    {
                        ["enabled"] = IsClaudeDesktopEnabled,
                        ["active_provider"] = (ActiveClaudeDesktopProvider ?? ClaudeDesktopCli.GetActiveProvider())?.Name ?? "none",
                    }
                });
                return;
            }

            string reqBody = "";
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
                reqBody = await reader.ReadToEndAsync();
            }

            // 2. Claude CLI route (/claude-cli/...)
            if (path.StartsWith("/claude-cli", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsClaudeCliEnabled)
                {
                    await WriteErrorAsync(ctx, 503, "Claude CLI 本地路由未开启。请在 APISwitch 中开启 Claude CLI 本地路由。");
                    return;
                }
                var provider = ActiveClaudeCliProvider ?? ClaudeCli.GetActiveProvider();
                if (provider == null || provider.IsOfficial)
                {
                    await WriteErrorAsync(ctx, 503, "Claude CLI 本地路由已开启，但尚未配置或选择第三方供应商。请在 APISwitch 中点击「一键应用」。");
                    return;
                }
                var subpath = path.Length > 11 ? path.Substring(11) : "/";
                await HandleClaudeForwardAsync(ctx, provider, subpath, reqBody);
                return;
            }

            // 3. Claude Desktop route (/claude-desktop/...)
            if (path.StartsWith("/claude-desktop", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsClaudeDesktopEnabled)
                {
                    await WriteErrorAsync(ctx, 503, "Claude 客户端本地路由未开启。请在 APISwitch 中开启 Claude 客户端本地路由。");
                    return;
                }
                var provider = ActiveClaudeDesktopProvider ?? ClaudeDesktopCli.GetActiveProvider();
                if (provider == null || provider.IsOfficial)
                {
                    await WriteErrorAsync(ctx, 503, "Claude 客户端本地路由已开启，但尚未配置或选择第三方供应商。请在 APISwitch 中点击「一键应用」。");
                    return;
                }
                var subpath = path.Length > 15 ? path.Substring(15) : "/";
                await HandleClaudeForwardAsync(ctx, provider, subpath, reqBody);
                return;
            }

            // 4. Codex route (/codex/... or direct /v1/...)
            if (!IsCodexEnabled)
            {
                await WriteErrorAsync(ctx, 503, "Codex 本地路由未开启。请在 APISwitch 中开启 Codex 本地路由。");
                return;
            }

            var active = ActiveCodexProvider ?? CodexCli.GetActiveProvider();
            if (active == null || active.IsOfficial)
            {
                await WriteErrorAsync(ctx, 503, "APISwitch Codex 本地路由运行中，但尚未选择激活的第三方 Codex 供应商。请在 APISwitch 中点击「一键应用」启用供应商。");
                return;
            }

            var codexPath = path.StartsWith("/codex", StringComparison.OrdinalIgnoreCase)
                ? (path.Length > 6 ? path.Substring(6) : "/")
                : path;
            var normalizedCodexPath = codexPath.TrimEnd('/');

            // /v1/responses or /responses (Codex Core API)
            if (normalizedCodexPath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            {
                var wireApi = (active.WireApi ?? "").Trim().ToLowerInvariant();
                if (wireApi == "chat")
                {
                    await HandleResponsesToChatAsync(ctx, reqBody, active);
                    return;
                }
                else
                {
                    var upstreamUrl = GetUpstreamEndpoint(active.BaseUrl, "responses");
                    await ForwardRequestAsync(ctx, upstreamUrl, reqBody, active);
                    return;
                }
            }

            // /v1/chat/completions or other standard endpoints
            if (normalizedCodexPath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                var upstreamUrl = GetUpstreamEndpoint(active.BaseUrl, "chat/completions");
                await ForwardRequestAsync(ctx, upstreamUrl, reqBody, active);
                return;
            }

            // /v1/models
            if (normalizedCodexPath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                var upstreamUrl = GetUpstreamEndpoint(active.BaseUrl, "models");
                await ForwardRequestAsync(ctx, upstreamUrl, reqBody, active);
                return;
            }

            // Fallback transparent forward for Codex
            var relativePath = codexPath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) ? codexPath.Substring(4) : codexPath.TrimStart('/');
            var fallbackUrl = GetUpstreamEndpoint(active.BaseUrl, relativePath);
            await ForwardRequestAsync(ctx, fallbackUrl, reqBody, active);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteErrorAsync(ctx, 500, "Local proxy error: " + ex.Message);
            }
            catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static async Task HandleResponsesToChatAsync(HttpListenerContext ctx, string reqBody, CodexProvider provider)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(reqBody);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 400, "Invalid JSON in request body: " + ex.Message);
            return;
        }

        if (root is not JsonObject reqObj)
        {
            await WriteErrorAsync(ctx, 400, "Request body must be a JSON object");
            return;
        }

        var model = reqObj["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(provider.Model))
        {
            model = provider.Model;
        }

        // Build chat messages
        var messages = new JsonArray();

        // 1. Instructions -> system message
        var instructions = reqObj["instructions"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = instructions
            });
        }

        // 2. Input items -> messages
        if (reqObj["input"] is JsonArray inputArray)
        {
            foreach (var item in inputArray)
            {
                if (item is not JsonObject itemObj) continue;

                var type = itemObj["type"]?.GetValue<string>();
                var role = itemObj["role"]?.GetValue<string>();

                if (type == "function_call")
                {
                    var callId = itemObj["call_id"]?.GetValue<string>() ?? ("call_" + Guid.NewGuid().ToString("N"));
                    var fName = itemObj["name"]?.GetValue<string>() ?? "";
                    var fArgs = itemObj["arguments"]?.GetValue<string>() ?? "{}";

                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["tool_calls"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = callId,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = fName,
                                    ["arguments"] = fArgs
                                }
                            }
                        }
                    });
                }
                else if (type == "function_call_output")
                {
                    var callId = itemObj["call_id"]?.GetValue<string>() ?? "";
                    var output = itemObj["output"]?.GetValue<string>() ?? "";

                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = callId,
                        ["content"] = output
                    });
                }
                else if (!string.IsNullOrEmpty(role))
                {
                    var contentNode = itemObj["content"];
                    string contentText = "";

                    if (contentNode is JsonArray cArr)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in cArr)
                        {
                            if (part is JsonObject partObj)
                            {
                                var text = partObj["text"]?.GetValue<string>() ?? partObj["input_text"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(text)) sb.Append(text);
                            }
                            else if (part != null)
                            {
                                sb.Append(part.ToString());
                            }
                        }
                        contentText = sb.ToString();
                    }
                    else if (contentNode != null)
                    {
                        contentText = contentNode.GetValue<string>() ?? contentNode.ToString();
                    }

                    messages.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["content"] = contentText
                    });
                }
            }
        }

        // Build Chat payload
        var chatPayload = new JsonObject
        {
            ["model"] = model ?? "gpt-4o",
            ["messages"] = messages,
            ["stream"] = true
        };

        if (reqObj.ContainsKey("temperature") && reqObj["temperature"] != null)
            chatPayload["temperature"] = reqObj["temperature"]!.DeepClone();
        if (reqObj.ContainsKey("top_p") && reqObj["top_p"] != null)
            chatPayload["top_p"] = reqObj["top_p"]!.DeepClone();
        if (reqObj.ContainsKey("max_output_tokens") && reqObj["max_output_tokens"] != null)
            chatPayload["max_tokens"] = reqObj["max_output_tokens"]!.DeepClone();

        // Tools mapping
        if (reqObj["tools"] is JsonArray toolsArray && toolsArray.Count > 0)
        {
            var chatTools = new JsonArray();
            foreach (var t in toolsArray)
            {
                if (t is not JsonObject tObj) continue;
                if (tObj.ContainsKey("function"))
                {
                    chatTools.Add(tObj.DeepClone());
                }
                else if (tObj["type"]?.GetValue<string>() == "function" || tObj.ContainsKey("name"))
                {
                    var fObj = new JsonObject
                    {
                        ["name"] = tObj["name"]?.GetValue<string>() ?? "",
                        ["description"] = tObj["description"]?.GetValue<string>() ?? ""
                    };
                    if (tObj.ContainsKey("parameters") && tObj["parameters"] != null)
                    {
                        fObj["parameters"] = tObj["parameters"]!.DeepClone();
                    }
                    chatTools.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = fObj
                    });
                }
            }
            if (chatTools.Count > 0)
            {
                chatPayload["tools"] = chatTools;
            }
        }

        var upstreamUrl = GetUpstreamEndpoint(provider.BaseUrl, "chat/completions");
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(chatPayload.ToJsonString()))
        };
        reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var ua = ctx.Request.Headers["User-Agent"];
        if (string.IsNullOrWhiteSpace(ua) || ua.Contains("Python", StringComparison.OrdinalIgnoreCase) || ua.Contains("urllib", StringComparison.OrdinalIgnoreCase))
        {
            ua = "curl/8.4.0";
        }
        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);

        var token = !string.IsNullOrWhiteSpace(provider.ApiKey) ? provider.ApiKey : provider.BearerToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (provider.CustomHeaders != null)
        {
            foreach (var (k, v) in provider.CustomHeaders)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    reqMsg.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var allHeaders = string.Join("; ", reqMsg.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        try
        {
            var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
            File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] [Chat Translation] POST {upstreamUrl} Headers: {allHeaders} payload={chatPayload.ToJsonString()}\n");
        }
        catch { }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await s_httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 502, $"无法连接上游中转站 {provider.Name} ({upstreamUrl}): {ex.Message}");
            return;
        }

        try
        {
            var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
            File.AppendAllText(Path.Combine(logDir, "proxy.log"), $"[{DateTime.Now:O}] [Chat Translation] Upstream status: {(int)upstreamResp.StatusCode}\n");
        }
        catch { }

        if (!upstreamResp.IsSuccessStatusCode)
        {
            var errBody = await upstreamResp.Content.ReadAsStringAsync();
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var errOut = ctx.Response.OutputStream;
            var b = Encoding.UTF8.GetBytes(errBody);
            await errOut.WriteAsync(b);
            await errOut.FlushAsync();
            return;
        }

        // Downstream SSE setup
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.SendChunked = true;

        await using var outStream = ctx.Response.OutputStream;
        await using var writer = new StreamWriter(outStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var respId = "resp_" + Guid.NewGuid().ToString("N");
        var msgId = "msg_" + Guid.NewGuid().ToString("N");
        var reasoningId = "rsn_" + Guid.NewGuid().ToString("N");
        int seq = 0;

        // 1. response.created
        await WriteSseEventAsync(writer, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["sequence_number"] = seq++,
            ["response"] = new JsonObject
            {
                ["id"] = respId,
                ["object"] = "response",
                ["status"] = "in_progress",
                ["model"] = model ?? "gpt-4o",
                ["output"] = new JsonArray()
            }
        });

        // 2. response.in_progress
        await WriteSseEventAsync(writer, "response.in_progress", new JsonObject
        {
            ["type"] = "response.in_progress",
            ["sequence_number"] = seq++,
            ["response"] = new JsonObject
            {
                ["id"] = respId,
                ["status"] = "in_progress"
            }
        });

        var hasAddedMsgItem = false;
        var hasAddedReasoningItem = false;
        var accumulatedText = new StringBuilder();
        var accumulatedReasoning = new StringBuilder();

        using var stream = await upstreamResp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var dataStr = line.Substring(5).Trim();
            if (dataStr == "[DONE]") break;

            try
            {
                var chunk = JsonNode.Parse(dataStr);
                if (chunk is not JsonObject chunkObj) continue;

                var choices = chunkObj["choices"] as JsonArray;
                if (choices == null || choices.Count == 0) continue;

                var firstChoice = choices[0] as JsonObject;
                if (firstChoice == null) continue;

                var delta = firstChoice["delta"] as JsonObject;
                if (delta == null) continue;

                // A. Check reasoning / thinking
                string? reasoningChunk = null;
                if (delta.ContainsKey("reasoning_content") && delta["reasoning_content"] != null)
                    reasoningChunk = delta["reasoning_content"]!.GetValue<string>();
                else if (delta.ContainsKey("reasoning") && delta["reasoning"] != null)
                    reasoningChunk = delta["reasoning"]!.GetValue<string>();
                else if (delta.ContainsKey("thinking") && delta["thinking"] != null)
                    reasoningChunk = delta["thinking"]!.GetValue<string>();

                if (!string.IsNullOrEmpty(reasoningChunk))
                {
                    if (!hasAddedReasoningItem)
                    {
                        hasAddedReasoningItem = true;
                        await WriteSseEventAsync(writer, "response.output_item.added", new JsonObject
                        {
                            ["type"] = "response.output_item.added",
                            ["sequence_number"] = seq++,
                            ["output_index"] = 0,
                            ["item"] = new JsonObject
                            {
                                ["type"] = "reasoning",
                                ["id"] = reasoningId,
                                ["status"] = "in_progress"
                            }
                        });
                    }

                    accumulatedReasoning.Append(reasoningChunk);
                    await WriteSseEventAsync(writer, "response.reasoning_text.delta", new JsonObject
                    {
                        ["type"] = "response.reasoning_text.delta",
                        ["sequence_number"] = seq++,
                        ["item_id"] = reasoningId,
                        ["delta"] = reasoningChunk
                    });
                }

                // B. Check message content
                var contentChunk = delta["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(contentChunk))
                {
                    if (!hasAddedMsgItem)
                    {
                        hasAddedMsgItem = true;
                        await WriteSseEventAsync(writer, "response.output_item.added", new JsonObject
                        {
                            ["type"] = "response.output_item.added",
                            ["sequence_number"] = seq++,
                            ["output_index"] = hasAddedReasoningItem ? 1 : 0,
                            ["item"] = new JsonObject
                            {
                                ["type"] = "message",
                                ["id"] = msgId,
                                ["role"] = "assistant",
                                ["content"] = new JsonArray(),
                                ["status"] = "in_progress"
                            }
                        });

                        await WriteSseEventAsync(writer, "response.content_part.added", new JsonObject
                        {
                            ["type"] = "response.content_part.added",
                            ["sequence_number"] = seq++,
                            ["item_id"] = msgId,
                            ["output_index"] = hasAddedReasoningItem ? 1 : 0,
                            ["content_index"] = 0,
                            ["part"] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = ""
                            }
                        });
                    }

                    accumulatedText.Append(contentChunk);
                    await WriteSseEventAsync(writer, "response.text.delta", new JsonObject
                    {
                        ["type"] = "response.text.delta",
                        ["sequence_number"] = seq++,
                        ["item_id"] = msgId,
                        ["output_index"] = hasAddedReasoningItem ? 1 : 0,
                        ["content_index"] = 0,
                        ["delta"] = contentChunk
                    });
                }

                // C. Check tool calls
                if (delta["tool_calls"] is JsonArray toolCallsArr)
                {
                    foreach (var tc in toolCallsArr)
                    {
                        if (tc is not JsonObject tcObj) continue;
                        var tcId = tcObj["id"]?.GetValue<string>() ?? ("call_" + Guid.NewGuid().ToString("N"));
                        var funcObj = tcObj["function"] as JsonObject;
                        var fName = funcObj?["name"]?.GetValue<string>();
                        var fArgs = funcObj?["arguments"]?.GetValue<string>();

                        if (!string.IsNullOrEmpty(fName))
                        {
                            await WriteSseEventAsync(writer, "response.output_item.added", new JsonObject
                            {
                                ["type"] = "response.output_item.added",
                                ["sequence_number"] = seq++,
                                ["output_index"] = 2,
                                ["item"] = new JsonObject
                                {
                                    ["type"] = "function_call",
                                    ["id"] = "fc_" + tcId,
                                    ["call_id"] = tcId,
                                    ["name"] = fName,
                                    ["arguments"] = ""
                                }
                            });
                        }

                        if (!string.IsNullOrEmpty(fArgs))
                        {
                            await WriteSseEventAsync(writer, "response.function_call_arguments.delta", new JsonObject
                            {
                                ["type"] = "response.function_call_arguments.delta",
                                ["sequence_number"] = seq++,
                                ["item_id"] = "fc_" + tcId,
                                ["delta"] = fArgs
                            });
                        }
                    }
                }
            }
            catch { }
        }

        // Finish message item
        if (hasAddedMsgItem)
        {
            await WriteSseEventAsync(writer, "response.content_part.done", new JsonObject
            {
                ["type"] = "response.content_part.done",
                ["sequence_number"] = seq++,
                ["item_id"] = msgId,
                ["output_index"] = hasAddedReasoningItem ? 1 : 0,
                ["content_index"] = 0,
                ["part"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = accumulatedText.ToString()
                }
            });

            await WriteSseEventAsync(writer, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["sequence_number"] = seq++,
                ["output_index"] = hasAddedReasoningItem ? 1 : 0,
                ["item"] = new JsonObject
                {
                    ["type"] = "message",
                    ["id"] = msgId,
                    ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = accumulatedText.ToString()
                        }
                    }
                }
            });
        }

        // Final response.completed
        var finalOutput = new JsonArray();
        if (hasAddedMsgItem)
        {
            finalOutput.Add(new JsonObject
            {
                ["type"] = "message",
                ["id"] = msgId,
                ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = accumulatedText.ToString()
                    }
                }
            });
        }

        await WriteSseEventAsync(writer, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["sequence_number"] = seq++,
            ["response"] = new JsonObject
            {
                ["id"] = respId,
                ["object"] = "response",
                ["status"] = "completed",
                ["model"] = model ?? "gpt-4o",
                ["output"] = finalOutput
            }
        });
    }

    private static async Task WriteSseEventAsync(StreamWriter writer, string eventType, JsonObject data)
    {
        await writer.WriteAsync($"event: {eventType}\ndata: {data.ToJsonString()}\n\n");
        await writer.FlushAsync();
    }

    private static async Task ForwardRequestAsync(HttpListenerContext ctx, string targetUrl, string? reqBody, CodexProvider provider)
    {
        var method = new HttpMethod(ctx.Request.HttpMethod);
        var reqMsg = new HttpRequestMessage(method, targetUrl);

        if (!string.IsNullOrEmpty(reqBody) && (method == HttpMethod.Post || method == HttpMethod.Put || method.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
        {
            reqMsg.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(reqBody));
            reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        var ua = ctx.Request.Headers["User-Agent"];
        if (string.IsNullOrWhiteSpace(ua) || ua.Contains("Python", StringComparison.OrdinalIgnoreCase) || ua.Contains("urllib", StringComparison.OrdinalIgnoreCase))
        {
            ua = "curl/8.4.0";
        }
        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);

        var token = !string.IsNullOrWhiteSpace(provider.ApiKey) ? provider.ApiKey : provider.BearerToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (!string.IsNullOrEmpty(ctx.Request.Headers["Authorization"]))
        {
            reqMsg.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers["Authorization"]);
        }

        if (provider.CustomHeaders != null)
        {
            foreach (var (k, v) in provider.CustomHeaders)
            {
                if (!string.IsNullOrWhiteSpace(k) && !string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    reqMsg.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await s_httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 502, $"无法连接上游中转站 {provider.Name} ({targetUrl}): {ex.Message}");
            return;
        }

        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
        foreach (var h in upstreamResp.Headers)
        {
            if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            try { ctx.Response.Headers[h.Key] = string.Join(", ", h.Value); } catch { }
        }
        foreach (var h in upstreamResp.Content.Headers)
        {
            if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            try { ctx.Response.Headers[h.Key] = string.Join(", ", h.Value); } catch { }
        }
        if (upstreamResp.Content.Headers.ContentType != null)
        {
            ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType.ToString();
        }

        await using var outStream = ctx.Response.OutputStream;
        await upstreamResp.Content.CopyToAsync(outStream);
        await outStream.FlushAsync();
    }

    private static string GetUpstreamEndpoint(string? baseUrl, string endpoint)
    {
        var baseStr = (baseUrl ?? "").Trim().TrimEnd('/');
        if (endpoint.StartsWith("/")) endpoint = endpoint.Substring(1);

        if (endpoint.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) &&
            baseStr.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = endpoint.Substring(3);
        }
        return $"{baseStr}/{endpoint}";
    }

    private static string RewriteClaudeModel(string originalModel, ClaudeProvider provider)
    {
        if (provider.ModelMappings == null || provider.ModelMappings.Count == 0)
            return ClaudeCli.Strip1m(originalModel);

        var clean = ClaudeCli.Strip1m(originalModel).ToLowerInvariant();

        ClaudeModelMapping? matched = null;
        if (clean.Contains("sonnet") || clean == "claude-sonnet-5" || clean == "claude-sonnet-4-5")
            matched = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, "Sonnet", StringComparison.OrdinalIgnoreCase));
        else if (clean.Contains("opus") || clean == "claude-opus-5" || clean == "claude-opus-4-8")
            matched = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, "Opus", StringComparison.OrdinalIgnoreCase));
        else if (clean.Contains("fable") || clean == "claude-fable-5")
            matched = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, "Fable", StringComparison.OrdinalIgnoreCase));
        else if (clean.Contains("haiku") || clean == "claude-haiku-4-5" || clean == "claude-3-5-haiku")
            matched = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, "Haiku", StringComparison.OrdinalIgnoreCase));
        else
            matched = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, clean, StringComparison.OrdinalIgnoreCase) ||
                                                                string.Equals(ClaudeCli.Strip1m(m.Model), clean, StringComparison.OrdinalIgnoreCase) ||
                                                                string.Equals(m.DisplayName, clean, StringComparison.OrdinalIgnoreCase));

        if (matched != null && !string.IsNullOrWhiteSpace(matched.Model))
            return ClaudeCli.Strip1m(matched.Model);

        var fallback = provider.ModelMappings.FirstOrDefault(m => string.Equals(m.Role, "Sonnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Model))
                    ?? provider.ModelMappings.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Model));

        if (fallback != null && !string.IsNullOrWhiteSpace(fallback.Model))
            return ClaudeCli.Strip1m(fallback.Model);

        return ClaudeCli.Strip1m(originalModel);
    }

    private static async Task HandleClaudeForwardAsync(HttpListenerContext ctx, ClaudeProvider provider, string subpath, string? reqBody)
    {
        // 1. Rewrite model in JSON request body if mapping exists
        if (!string.IsNullOrEmpty(reqBody))
        {
            try
            {
                var jsonNode = JsonNode.Parse(reqBody);
                if (jsonNode is JsonObject reqObj && reqObj.ContainsKey("model"))
                {
                    var origModel = reqObj["model"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(origModel))
                    {
                        var rewritten = RewriteClaudeModel(origModel, provider);
                        if (!string.IsNullOrEmpty(rewritten) && rewritten != origModel)
                        {
                            reqObj["model"] = rewritten;
                            reqBody = reqObj.ToJsonString();
                        }
                    }
                }
            }
            catch { }
        }

        // 2. If provider.WireApi == "chat" or "responses" and subpath indicates messages endpoint
        if (string.Equals(provider.WireApi, "chat", StringComparison.OrdinalIgnoreCase) &&
            (subpath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || subpath.EndsWith("/messages/", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleClaudeMessagesToChatAsync(ctx, provider, reqBody);
            return;
        }

        if (string.Equals(provider.WireApi, "responses", StringComparison.OrdinalIgnoreCase) &&
            (subpath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || subpath.EndsWith("/messages/", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleClaudeMessagesToResponsesAsync(ctx, provider, reqBody);
            return;
        }

        var query = ctx.Request.Url?.Query;
        var targetUrl = BuildUpstreamUrl(provider.BaseUrl, subpath, query);

        var method = new HttpMethod(ctx.Request.HttpMethod);
        var reqMsg = new HttpRequestMessage(method, targetUrl);

        if (!string.IsNullOrEmpty(reqBody) && (method == HttpMethod.Post || method == HttpMethod.Put || method.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
        {
            reqMsg.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(reqBody));
            var rawContentType = ctx.Request.ContentType;
            if (!string.IsNullOrEmpty(rawContentType))
            {
                try
                {
                    if (rawContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    }
                    else
                    {
                        reqMsg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(rawContentType);
                    }
                }
                catch
                {
                    reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
            }
            else
            {
                reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
        }

        foreach (string headerName in ctx.Request.Headers)
        {
            if (string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var headerValues = ctx.Request.Headers.GetValues(headerName);
            if (headerValues != null)
            {
                reqMsg.Headers.TryAddWithoutValidation(headerName, headerValues);
            }
        }

        var ua = ctx.Request.Headers["User-Agent"];
        if (string.IsNullOrWhiteSpace(ua) || ua.Contains("Python", StringComparison.OrdinalIgnoreCase) || ua.Contains("urllib", StringComparison.OrdinalIgnoreCase))
        {
            ua = "curl/8.4.0";
        }
        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);

        var token = provider.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            reqMsg.Headers.Remove("x-api-key");
            reqMsg.Headers.TryAddWithoutValidation("x-api-key", token);
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (provider.CustomHeaders != null)
        {
            foreach (var (k, v) in provider.CustomHeaders)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    reqMsg.Headers.Remove(k);
                    reqMsg.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await s_httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 502, $"无法连接 Claude 上游网关 {provider.Name} ({targetUrl}): {ex.Message}");
            return;
        }

        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;

        foreach (var h in upstreamResp.Headers)
        {
            if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Connection", StringComparison.OrdinalIgnoreCase))
                continue;
            try { ctx.Response.Headers[h.Key] = string.Join(", ", h.Value); } catch { }
        }

        foreach (var h in upstreamResp.Content.Headers)
        {
            if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            try { ctx.Response.Headers[h.Key] = string.Join(", ", h.Value); } catch { }
        }

        if (upstreamResp.Content.Headers.ContentType != null)
        {
            ctx.Response.ContentType = upstreamResp.Content.Headers.ContentType.ToString();
        }

        await using var outStream = ctx.Response.OutputStream;
        await upstreamResp.Content.CopyToAsync(outStream);
        await outStream.FlushAsync();
    }

    private static string BuildUpstreamUrl(string? baseUrl, string relativePathWithLeadingSlash, string? query)
    {
        var baseStr = (baseUrl ?? "").Trim().TrimEnd('/');
        var rel = (relativePathWithLeadingSlash ?? "").TrimStart('/');

        if (rel.StartsWith("v1/", StringComparison.OrdinalIgnoreCase) && baseStr.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel.Substring(3);
        }
        else if (string.Equals(rel, "v1", StringComparison.OrdinalIgnoreCase) && baseStr.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            rel = "";
        }

        var url = string.IsNullOrEmpty(rel) ? baseStr : $"{baseStr}/{rel}";
        if (!string.IsNullOrEmpty(query))
        {
            url += query;
        }
        return url;
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int statusCode, JsonNode data)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(data.ToJsonString());
        await using var outStream = ctx.Response.OutputStream;
        await outStream.WriteAsync(bytes);
        await outStream.FlushAsync();
    }

    private static async Task WriteErrorAsync(HttpListenerContext ctx, int statusCode, string message)
    {
        var errObj = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "apiswitch_router_error",
                ["code"] = statusCode
            }
        };
        await WriteJsonAsync(ctx, statusCode, errObj);
    }

    private static async Task HandleClaudeMessagesToChatAsync(HttpListenerContext ctx, ClaudeProvider provider, string? reqBody)
    {
        if (string.IsNullOrEmpty(reqBody))
        {
            await WriteErrorAsync(ctx, 400, "Request body is empty");
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(reqBody);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 400, "Invalid JSON in request: " + ex.Message);
            return;
        }

        if (root is not JsonObject reqObj)
        {
            await WriteErrorAsync(ctx, 400, "Request body must be a JSON object");
            return;
        }

        var origModel = reqObj["model"]?.GetValue<string>() ?? provider.Model ?? "gpt-4o";
        var model = RewriteClaudeModel(origModel, provider);

        var messages = new JsonArray();

        // Extract system prompt
        if (reqObj.ContainsKey("system") && reqObj["system"] != null)
        {
            var sysNode = reqObj["system"];
            string sysContent = "";
            if (sysNode is JsonValue)
            {
                sysContent = sysNode.GetValue<string>() ?? "";
            }
            else if (sysNode is JsonArray sysArr)
            {
                var sb = new StringBuilder();
                foreach (var part in sysArr)
                {
                    if (part is JsonObject partObj && partObj.ContainsKey("text"))
                        sb.Append(partObj["text"]?.GetValue<string>());
                    else if (part != null)
                        sb.Append(part.ToString());
                }
                sysContent = sb.ToString();
            }
            if (!string.IsNullOrWhiteSpace(sysContent))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = sysContent
                });
            }
        }

        // Extract messages
        if (reqObj["messages"] is JsonArray msgsArr)
        {
            foreach (var mNode in msgsArr)
            {
                if (mNode is not JsonObject mObj) continue;
                var role = mObj["role"]?.GetValue<string>() ?? "user";
                var contentNode = mObj["content"];
                string contentText = "";

                if (contentNode is JsonValue)
                {
                    contentText = contentNode.GetValue<string>() ?? "";
                }
                else if (contentNode is JsonArray cArr)
                {
                    var sb = new StringBuilder();
                    foreach (var part in cArr)
                    {
                        if (part is JsonObject partObj && partObj.ContainsKey("text"))
                            sb.Append(partObj["text"]?.GetValue<string>());
                        else if (part != null)
                            sb.Append(part.ToString());
                    }
                    contentText = sb.ToString();
                }

                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = contentText
                });
            }
        }

        bool stream = reqObj["stream"]?.GetValue<bool>() ?? false;

        var chatPayload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream
        };

        if (reqObj.ContainsKey("temperature") && reqObj["temperature"] != null)
            chatPayload["temperature"] = reqObj["temperature"]!.DeepClone();
        if (reqObj.ContainsKey("max_tokens") && reqObj["max_tokens"] != null)
            chatPayload["max_tokens"] = reqObj["max_tokens"]!.DeepClone();

        var upstreamUrl = GetUpstreamEndpoint(provider.BaseUrl, "chat/completions");
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(chatPayload.ToJsonString()))
        };
        reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var ua = ctx.Request.Headers["User-Agent"] ?? "curl/8.4.0";
        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);

        var token = provider.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (provider.CustomHeaders != null)
        {
            foreach (var (k, v) in provider.CustomHeaders)
                if (!string.IsNullOrWhiteSpace(k) && !string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                    reqMsg.Headers.TryAddWithoutValidation(k, v);
        }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await s_httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 502, $"无法连接上游中转站 {provider.Name} ({upstreamUrl}): {ex.Message}");
            return;
        }

        if (!upstreamResp.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            var errBytes = await upstreamResp.Content.ReadAsByteArrayAsync();
            await using var outS = ctx.Response.OutputStream;
            await outS.WriteAsync(errBytes);
            await outS.FlushAsync();
            return;
        }

        if (stream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            await using var outStream = ctx.Response.OutputStream;
            using var inStream = await upstreamResp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(inStream, Encoding.UTF8);

            var msgId = "msg_" + Guid.NewGuid().ToString("N");

            // 1. message_start
            var msgStartJson = new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = msgId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
                }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_start", msgStartJson);

            // 2. content_block_start
            var blockStartJson = new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "content_block_start", blockStartJson);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ")) continue;

                var dataStr = line.Substring(6).Trim();
                if (dataStr == "[DONE]") break;

                try
                {
                    var chunkNode = JsonNode.Parse(dataStr);
                    var deltaNode = chunkNode?["choices"]?[0]?["delta"];
                    var textChunk = deltaNode?["content"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(textChunk))
                    {
                        textChunk = deltaNode?["reasoning_content"]?.GetValue<string>();
                    }

                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        var deltaJson = new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = 0,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "text_delta",
                                ["text"] = textChunk
                            }
                        }.ToJsonString();
                        await WriteRawSseAsync(outStream, "content_block_delta", deltaJson);
                    }
                }
                catch { }
            }

            // 3. content_block_stop
            var blockStopJson = new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = 0
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "content_block_stop", blockStopJson);

            // 4. message_delta
            var msgDeltaJson = new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null },
                ["usage"] = new JsonObject { ["output_tokens"] = 10 }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_delta", msgDeltaJson);

            // 5. message_stop
            var msgStopJson = new JsonObject { ["type"] = "message_stop" }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_stop", msgStopJson);
            await outStream.FlushAsync();
        }
        else
        {
            var respStr = await upstreamResp.Content.ReadAsStringAsync();
            var respNode = JsonNode.Parse(respStr);
            var content = respNode?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";

            var anthropicResp = new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = content
                    }
                },
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
            };
            await WriteJsonAsync(ctx, 200, anthropicResp);
        }
    }

    private static async Task HandleClaudeMessagesToResponsesAsync(HttpListenerContext ctx, ClaudeProvider provider, string? reqBody)
    {
        if (string.IsNullOrEmpty(reqBody))
        {
            await WriteErrorAsync(ctx, 400, "Request body is empty");
            return;
        }

        JsonNode? root;
        try { root = JsonNode.Parse(reqBody); }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 400, "Invalid JSON in request: " + ex.Message);
            return;
        }

        if (root is not JsonObject reqObj)
        {
            await WriteErrorAsync(ctx, 400, "Request body must be a JSON object");
            return;
        }

        var origModel = reqObj["model"]?.GetValue<string>() ?? provider.Model ?? "gpt-4o";
        var model = RewriteClaudeModel(origModel, provider);

        var inputArr = new JsonArray();

        // Extract system prompt
        if (reqObj.ContainsKey("system") && reqObj["system"] != null)
        {
            var sysNode = reqObj["system"];
            string sysContent = "";
            if (sysNode is JsonValue)
                sysContent = sysNode.GetValue<string>() ?? "";
            else if (sysNode is JsonArray sysArr)
            {
                var sb = new StringBuilder();
                foreach (var part in sysArr)
                {
                    if (part is JsonObject partObj && partObj.ContainsKey("text"))
                        sb.Append(partObj["text"]?.GetValue<string>());
                    else if (part != null)
                        sb.Append(part.ToString());
                }
                sysContent = sb.ToString();
            }
            if (!string.IsNullOrWhiteSpace(sysContent))
            {
                inputArr.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = sysContent
                });
            }
        }

        // Extract messages
        if (reqObj["messages"] is JsonArray msgsArr)
        {
            foreach (var mNode in msgsArr)
            {
                if (mNode is not JsonObject mObj) continue;
                var role = mObj["role"]?.GetValue<string>() ?? "user";
                var contentNode = mObj["content"];
                string contentText = "";

                if (contentNode is JsonValue)
                    contentText = contentNode.GetValue<string>() ?? "";
                else if (contentNode is JsonArray cArr)
                {
                    var sb = new StringBuilder();
                    foreach (var part in cArr)
                    {
                        if (part is JsonObject partObj && partObj.ContainsKey("text"))
                            sb.Append(partObj["text"]?.GetValue<string>());
                        else if (part != null)
                            sb.Append(part.ToString());
                    }
                    contentText = sb.ToString();
                }

                inputArr.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = contentText
                });
            }
        }

        bool stream = reqObj["stream"]?.GetValue<bool>() ?? false;

        var responsesPayload = new JsonObject
        {
            ["model"] = model,
            ["input"] = inputArr,
            ["stream"] = stream
        };

        var upstreamUrl = GetUpstreamEndpoint(provider.BaseUrl, "responses");
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(responsesPayload.ToJsonString()))
        };
        reqMsg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var ua = ctx.Request.Headers["User-Agent"] ?? "curl/8.4.0";
        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);

        var token = provider.AuthToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (provider.CustomHeaders != null)
        {
            foreach (var (k, v) in provider.CustomHeaders)
                if (!string.IsNullOrWhiteSpace(k) && !string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
                    reqMsg.Headers.TryAddWithoutValidation(k, v);
        }

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await s_httpClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx, 502, $"无法连接上游 Responses 端点 {provider.Name} ({upstreamUrl}): {ex.Message}");
            return;
        }

        if (!upstreamResp.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
            var errBytes = await upstreamResp.Content.ReadAsByteArrayAsync();
            await using var outS = ctx.Response.OutputStream;
            await outS.WriteAsync(errBytes);
            await outS.FlushAsync();
            return;
        }

        if (stream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            await using var outStream = ctx.Response.OutputStream;
            using var inStream = await upstreamResp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(inStream, Encoding.UTF8);

            var msgId = "msg_" + Guid.NewGuid().ToString("N");

            // message_start
            var msgStartJson = new JsonObject
            {
                ["type"] = "message_start",
                ["message"] = new JsonObject
                {
                    ["id"] = msgId,
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = model,
                    ["content"] = new JsonArray(),
                    ["stop_reason"] = null,
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
                }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_start", msgStartJson);

            // content_block_start
            var blockStartJson = new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "content_block_start", blockStartJson);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ")) continue;

                var dataStr = line.Substring(6).Trim();
                if (dataStr == "[DONE]") break;

                try
                {
                    var chunkNode = JsonNode.Parse(dataStr);
                    string? textChunk = chunkNode?["delta"]?.GetValue<string>()
                        ?? chunkNode?["text"]?.GetValue<string>()
                        ?? chunkNode?["output_text"]?.GetValue<string>();

                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        var deltaJson = new JsonObject
                        {
                            ["type"] = "content_block_delta",
                            ["index"] = 0,
                            ["delta"] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = textChunk
                            }
                        }.ToJsonString();
                        await WriteRawSseAsync(outStream, "content_block_delta", deltaJson);
                    }
                }
                catch { }
            }

            // content_block_stop
            var blockStopJson = new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 }.ToJsonString();
            await WriteRawSseAsync(outStream, "content_block_stop", blockStopJson);

            // message_delta
            var msgDeltaJson = new JsonObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null },
                ["usage"] = new JsonObject { ["output_tokens"] = 10 }
            }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_delta", msgDeltaJson);

            // message_stop
            var msgStopJson = new JsonObject { ["type"] = "message_stop" }.ToJsonString();
            await WriteRawSseAsync(outStream, "message_stop", msgStopJson);
            await outStream.FlushAsync();
        }
        else
        {
            var respStr = await upstreamResp.Content.ReadAsStringAsync();
            var respNode = JsonNode.Parse(respStr);
            var content = respNode?["output_text"]?.GetValue<string>()
                ?? respNode?["output"]?[0]?["content"]?[0]?["text"]?.GetValue<string>()
                ?? "";

            var anthropicResp = new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"),
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = model,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = content }
                },
                ["stop_reason"] = "end_turn",
                ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 }
            };
            await WriteJsonAsync(ctx, 200, anthropicResp);
        }
    }

    private static async Task WriteRawSseAsync(Stream stream, string eventName, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
