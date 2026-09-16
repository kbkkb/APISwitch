using System.Text;
using System.Text.Json;

namespace APISwitch.Services;

public static class AgState
{
    public const string KeyOauthToken = "antigravityUnifiedStateSync.oauthToken";
    public const string KeyUserStatus = "antigravityUnifiedStateSync.userStatus";
    public const string KeyAuthStatus = "antigravityAuthStatus";
    public const string KeyLegacyAgentState = "jetskiStateSync.agentManagerInitState";

    public static readonly string[] AuthKeys = { KeyOauthToken, KeyUserStatus, KeyAuthStatus, KeyLegacyAgentState };

    static byte[]? ParseUserContext(string userStatusB64)
    {
        try
        {
            var outer = MiniProto.Parse(Convert.FromBase64String(userStatusB64));
            var inner = MiniProto.AsBytes(outer, 1);
            if (inner == null) return null;
            var data = MiniProto.AsBytes(MiniProto.Parse(inner), 2);
            if (data == null) return null;
            var rawB64 = MiniProto.AsString(MiniProto.Parse(data), 1);
            if (rawB64 == null) return null;
            return Convert.FromBase64String(rawB64);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractEmail(string? userStatusB64)
    {
        if (string.IsNullOrEmpty(userStatusB64)) return null;
        var ctxBytes = ParseUserContext(userStatusB64);
        if (ctxBytes == null) return null;
        try
        {
            var ctx = MiniProto.Parse(ctxBytes);
            foreach (var num in new[] { 7, 3 })
            {
                var s = MiniProto.AsString(ctx, num);
                if (s != null && s.Contains('@') && !s.Contains(' ')) return s;
            }
            foreach (var f in ctx.Where(f => f.WireType == 2))
            {
                var s = Encoding.UTF8.GetString(f.Bytes);
                if (s.Contains('@') && !s.Contains(' ') && s.Length < 128) return s;
            }
        }
        catch { }
        return null;
    }

    public static string? ExtractPlan(string? userStatusB64)
    {
        if (string.IsNullOrEmpty(userStatusB64)) return null;
        var ctxBytes = ParseUserContext(userStatusB64);
        if (ctxBytes == null) return null;
        try
        {
            var ctx = MiniProto.Parse(ctxBytes);
            var sub = MiniProto.AsBytes(ctx, 36);
            if (sub == null) return null;
            var subFields = MiniProto.Parse(sub);
            return MiniProto.AsString(subFields, 3) ?? MiniProto.AsString(subFields, 2);
        }
        catch { }
        return null;
    }

    public static string? ExtractAuthState(string? oauthTokenB64, string? email = null)
    {
        if (string.IsNullOrEmpty(oauthTokenB64)) return "signedOut";
        try
        {
            var raw = Convert.FromBase64String(oauthTokenB64);
            // In Antigravity IDE, a valid session contains substantial OAuth tokens (> 500 bytes).
            // A signed-out or cleared state is typically < 200 bytes.
            if (!string.IsNullOrEmpty(email) && raw.Length > 200)
                return "signedIn";
            if (raw.Length > 500)
                return "signedIn";

            var state = ScanForState(raw, 5);
            if (!string.IsNullOrEmpty(state))
            {
                if (state == "signedOut" && raw.Length > 300)
                    return "signedIn";
                return state;
            }
            return raw.Length > 200 ? "signedIn" : "signedOut";
        }
        catch { }
        return "signedOut";
    }

    static string? ScanForState(byte[] buf, int depth)
    {
        if (depth < 0 || buf.Length == 0) return null;
        List<MiniProto.Field> fields;
        try { fields = MiniProto.Parse(buf); } catch { return null; }
        foreach (var f in fields.Where(f => f.WireType == 2 && f.Bytes.Length > 1))
        {
            var s = TryGetString(f.Bytes);
            if (s != null && s.StartsWith('{') && s.Contains("\"state\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("state", out var st))
                        return st.GetString();
                }
                catch { }
            }
            var nested = ScanForState(f.Bytes, depth - 1);
            if (nested != null) return nested;
        }
        return null;
    }

    static string? TryGetString(byte[] b)
    {
        try
        {
            var s = Encoding.UTF8.GetString(b);
            return s.Contains('\uFFFD') ? null : s;
        }
        catch { return null; }
    }
}
