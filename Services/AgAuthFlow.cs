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

        // 3. Wait for callback
        HttpListenerContext context;
        using (cancellationToken.Register(() => { try { listener.Stop(); } catch { } }))
        {
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("用户取消或登录超时", ex);
            }
        }

        var req = context.Request;
        var query = req.QueryString;
        var returnedState = query["state"];
        var code = query["code"];
        var error = query["error"];

        // 4. Return user friendly HTML to browser
        var resp = context.Response;
        resp.ContentType = "text/html; charset=utf-8";

        string html;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || returnedState != state)
        {
            html = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>APISwitch - 授权失败</title>
<style>body{font-family:sans-serif;background:#0B0E17;color:#fff;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}
.card{background:#161B2E;border:1px solid #D63939;padding:36px;border-radius:16px;text-align:center;max-width:400px;}
h1{color:#F85149;margin:0 0 10px;font-size:20px;} p{color:#8B949E;font-size:14px;}</style></head>
<body><div class='card'><h1>❌ Google 授权未完成</h1><p>" + WebUtility.HtmlEncode(error ?? "未接收到有效授权码") + @"</p></div></body></html>";
            var errBytes = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = errBytes.Length;
            await resp.OutputStream.WriteAsync(errBytes, cancellationToken);
            resp.OutputStream.Close();
            throw new InvalidOperationException($"Google 授权失败: {error ?? "State mismatch or missing code"}");
        }

        html = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>APISwitch - 授权成功</title>
<style>body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#0B0E17;color:#E6EDF3;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}
.card{background:#161B2E;border:1px solid #283048;padding:40px;border-radius:16px;text-align:center;max-width:420px;box-shadow:0 10px 30px rgba(0,0,0,0.5);}
.icon{font-size:48px;margin-bottom:16px;} h1{font-size:22px;margin:0 0 12px;color:#4E88FF;} p{font-size:14px;color:#8B949E;line-height:1.6;margin:0;}</style></head>
<body><div class='card'><div class='icon'>✨</div><h1>Google 授权成功！</h1><p>APISwitch 已捕获登录凭据，您可以关闭此浏览器标签页并返回客户端。</p></div></body></html>";
        var successBytes = Encoding.UTF8.GetBytes(html);
        resp.ContentLength64 = successBytes.Length;
        await resp.OutputStream.WriteAsync(successBytes, cancellationToken);
        resp.OutputStream.Close();

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

        // Query Quota and Tier
        try
        {
            await AgQuotaService.RefreshQuotaAsync(profile);
        }
        catch { }

        ProfileStore.Save(profile);
        return profile;
    }
}
