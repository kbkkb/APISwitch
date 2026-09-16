using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

public static class ModelFetchService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
    }

    public static async Task<List<string>> FetchModelsAsync(
        string baseUrl,
        string? apiKey = null,
        Dictionary<string, string>? customHeaders = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("请先填写 Base URL");

        baseUrl = baseUrl.Trim();
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = "https://" + baseUrl;
        }
        baseUrl = baseUrl.TrimEnd('/');

        var candidates = new List<string>();
        if (baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(baseUrl);
        }
        else if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(baseUrl + "/models");
            var rootUrl = baseUrl.Substring(0, baseUrl.Length - 3).TrimEnd('/');
            if (rootUrl.Length > 0)
                candidates.Add(rootUrl + "/models");
        }
        else
        {
            candidates.Add(baseUrl + "/models");
            candidates.Add(baseUrl + "/v1/models");
        }

        string? lastError = null;

        foreach (var url in candidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "curl/7.68.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                if (customHeaders != null)
                {
                    foreach (var kv in customHeaders)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        request.Headers.TryAddWithoutValidation(kv.Key.Trim(), kv.Value ?? "");
                    }
                }

                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var list = ParseModelIds(body);
                    if (list.Count > 0)
                        return list;
                    else
                        lastError = $"端点 {url} 返回成功，但模型列表为空";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("鉴权失败 (HTTP 401)：请检查 API Key 是否填写正确");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    lastError = "访问被拒绝 (HTTP 403)：请检查 API Key 权限或端点 IP 限制";
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    lastError = $"端点 {url} 未找到 (404)";
                }
                else
                {
                    lastError = $"端点 {url} 响应错误 (HTTP {(int)response.StatusCode}: {response.ReasonPhrase})";
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
            }
        }

        throw new InvalidOperationException(lastError ?? "未能探测到可用模型列表，请确认 Base URL 与 API Key 是否有效");
    }

    private static List<string> ParseModelIds(string json)
    {
        var result = new List<string>();
        try
        {
            var node = JsonNode.Parse(json);
            if (node == null) return result;

            if (node is JsonObject obj)
            {
                if (obj["data"] is JsonArray dataArr)
                {
                    foreach (var item in dataArr)
                    {
                        if (item is JsonObject m && m["id"]?.GetValue<string>() is string id && !string.IsNullOrWhiteSpace(id))
                            result.Add(id.Trim());
                        else if (item is JsonValue val && val.TryGetValue<string>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
                            result.Add(strVal.Trim());
                    }
                }
                else if (obj["models"] is JsonArray modelsArr)
                {
                    foreach (var item in modelsArr)
                    {
                        if (item is JsonObject m)
                        {
                            var id = m["id"]?.GetValue<string>() ?? m["name"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                if (id.StartsWith("models/")) id = id.Substring("models/".Length);
                                result.Add(id.Trim());
                            }
                        }
                    }
                }
                else if (obj["models"] is JsonObject modelsObj)
                {
                    foreach (var kv in modelsObj)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key))
                            result.Add(kv.Key.Trim());
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject m && m["id"]?.GetValue<string>() is string id && !string.IsNullOrWhiteSpace(id))
                        result.Add(id.Trim());
                    else if (item is JsonValue val && val.TryGetValue<string>(out var strVal) && !string.IsNullOrWhiteSpace(strVal))
                        result.Add(strVal.Trim());
                }
            }
        }
        catch { }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
