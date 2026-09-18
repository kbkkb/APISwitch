using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using APISwitch.Models;

namespace APISwitch.Services;

public static class AgAuthFlow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<Profile> StartGoogleLoginAsync(CancellationToken cancellationToken = default)
    {
        // 1. Find a free port on loopback
        int port;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        var redirectUri = $"http://127.0.0.1:{port}/oauth-callback";
        var state = Guid.NewGuid().ToString("N");

        var scopes = "openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile https://www.googleapis.com/auth/cclog https://www.googleapis.com/auth/experimentsandconfigs";
        var authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?" +
                      $"client_id={Uri.EscapeDataString(AgQuotaService.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&response_type=code" +
                      $"&scope={Uri.EscapeDataString(scopes)}" +
                      $"&access_type=offline" +
                      $"&prompt=consent" +
                      $"&include_granted_scopes=true" +
                      $"&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/oauth-callback/");
        listener.Start();

        // 2. Open browser
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        // 3. Wait for a callback that matches our unique state.
        HttpListenerContext context;
        string code = "";
        while (true)
        {
            using var cancellationRegistration = cancellationToken.Register(() => { try { listener.Stop(); } catch { } });
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("用户取消或登录超时", ex);
            }

            // Google redirects to /oauth-callback without the listener's trailing slash.
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!path.Equals("/oauth-callback", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/oauth-callback/", StringComparison.OrdinalIgnoreCase))
            {
                try { context.Response.StatusCode = 404; context.Response.Close(); } catch { }
                continue;
            }

            var query = context.Request.QueryString;
            var returnedState = query["state"];
            code = query["code"] ?? "";
            var error = query["error"];

            // Only an explicit authorization error is fatal. Ignore unrelated local callbacks so an
            // attacker cannot win the race with a bogus state.
            if (!string.IsNullOrEmpty(error))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                var failureHtml = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>APISwitch - 授权失败</title></head><body><h1>❌ Google 授权未完成</h1><p>" + WebUtility.HtmlEncode(error) + @"</p></body></html>";
                var failureBytes = Encoding.UTF8.GetBytes(failureHtml);
                context.Response.ContentLength64 = failureBytes.Length;
                await context.Response.OutputStream.WriteAsync(failureBytes, cancellationToken);
                context.Response.OutputStream.Close();
                throw new InvalidOperationException($"Google 授权失败: {error}");
            }

            if (returnedState == state && !string.IsNullOrEmpty(code))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                var successHtml = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>APISwitch - 授权成功</title></head><body><h1>✨ Google 授权成功！</h1><p>APISwitch 已捕获登录凭据，您可以关闭此浏览器标签页并返回客户端。</p></body></html>";
                var successBytes = Encoding.UTF8.GetBytes(successHtml);
                context.Response.ContentLength64 = successBytes.Length;
                await context.Response.OutputStream.WriteAsync(successBytes, cancellationToken);
                context.Response.OutputStream.Close();
                break;
            }

            try { context.Response.StatusCode = 400; context.Response.Close(); } catch { }
        }

        // 5. Exchange code for tokens
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = AgQuotaService.ClientId,
            ["client_secret"] = AgQuotaService.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        });

        var tokenResp = await Http.SendAsync(tokenReq, cancellationToken);
        tokenResp.EnsureSuccessStatusCode();

        var tokenJson = await tokenResp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;

        var accessToken = root.GetProperty("access_token").GetString()!;
        var refreshToken = root.TryGetProperty("refresh_token", out var rfElem) ? rfElem.GetString() : null;
        var idToken = root.TryGetProperty("id_token", out var idElem) ? idElem.GetString() : null;
        long expiresIn = root.TryGetProperty("expires_in", out var expElem) ? expElem.GetInt64() : 3600;

        // 6. Decode user email and name from id_token
        string email = "";
        string name = "";

        if (!string.IsNullOrEmpty(idToken))
        {
            try
            {
                var parts = idToken.Split('.');
                if (parts.Length >= 2)
                {
                    var payload = parts[1];
                    payload = payload.Replace('-', '+').Replace('_', '/');
                    switch (payload.Length % 4)
                    {
                        case 2: payload += "=="; break;
                        case 3: payload += "="; break;
                    }
                    var idJson = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                    using var idDoc = JsonDocument.Parse(idJson);
                    if (idDoc.RootElement.TryGetProperty("email", out var eElem)) email = eElem.GetString() ?? "";
                    if (idDoc.RootElement.TryGetProperty("name", out var nElem)) name = nElem.GetString() ?? "";
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(email))
        {
            // Fallback to userinfo API
            try
            {
                using var uiReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
                uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var uiResp = await Http.SendAsync(uiReq, cancellationToken);
                if (uiResp.IsSuccessStatusCode)
                {
                    var uiJson = await uiResp.Content.ReadAsStringAsync(cancellationToken);
                    using var uiDoc = JsonDocument.Parse(uiJson);
                    if (uiDoc.RootElement.TryGetProperty("email", out var eElem)) email = eElem.GetString() ?? "";
                    if (uiDoc.RootElement.TryGetProperty("name", out var nElem)) name = nElem.GetString() ?? "";
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(email))
        {
            email = "google_user_" + DateTime.Now.ToString("HHmmss");
        }

        var profile = new Profile
        {
            Email = email,
            Name = !string.IsNullOrEmpty(name) ? name : email,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            IdToken = idToken,
            ExpiryTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
            CapturedAtUtc = DateTime.UtcNow
        };

        // A login can return a rotated refresh token. Merge it into an existing archive instead of
        // overwriting its slug file with a fresh profile.
        var existing = ProfileStore.Load().FirstOrDefault(x =>
            x.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.AccessToken = profile.AccessToken;
            existing.RefreshToken = profile.RefreshToken ?? existing.RefreshToken;
            existing.IdToken = profile.IdToken ?? existing.IdToken;
            existing.ExpiryTimestamp = profile.ExpiryTimestamp;
            existing.CapturedAtUtc = profile.CapturedAtUtc;
            profile = existing;
        }

        // Query Quota and Tier
        try
        {
            await AgQuotaService.RefreshQuotaAsync(profile);
        }
        catch { }

        try
        {
            profile.Values = AgAuthHelper.BuildAuthValues(profile);
        }
        catch { }

        ProfileStore.Save(profile);
        return profile;
    }
}
