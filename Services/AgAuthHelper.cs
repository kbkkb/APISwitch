using System.Text;
using APISwitch.Models;

namespace APISwitch.Services;

public static class AgAuthHelper
{
    public static byte[] Varint(ulong val)
    {
        var list = new List<byte>();
        while (true)
        {
            byte b = (byte)(val & 0x7F);
            val >>= 7;
            if (val > 0)
            {
                list.Add((byte)(b | 0x80));
            }
            else
            {
                list.Add(b);
                break;
            }
        }
        return list.ToArray();
    }

    public static byte[] Tag(int fieldNum, int wireType) => Varint((ulong)((fieldNum << 3) | wireType));

    public static byte[] VarintField(int fieldNum, ulong val) => Combine(Tag(fieldNum, 0), Varint(val));

    public static byte[] LenDelim(int fieldNum, byte[] data) => Combine(Tag(fieldNum, 2), Varint((ulong)data.Length), data);

    public static byte[] LenDelim(int fieldNum, string s) => LenDelim(fieldNum, Encoding.UTF8.GetBytes(s));

    public static byte[] Combine(params byte[][] chunks)
    {
        int total = chunks.Sum(c => c.Length);
        var res = new byte[total];
        int offset = 0;
        foreach (var c in chunks)
        {
            Buffer.BlockCopy(c, 0, res, offset, c.Length);
            offset += c.Length;
        }
        return res;
    }

    public static string BuildUserStatus(string email)
    {
        // 1. ctx: field 3 = email, field 7 = email
        var ctx = Combine(LenDelim(3, email), LenDelim(7, email));
        var ctxB64 = Convert.ToBase64String(ctx);

        // 2. inner item: field 1 = sentinel, field 2 = (field 1 = ctxB64)
        var innerF2 = LenDelim(1, ctxB64);
        var item = Combine(LenDelim(1, "userStatusSentinelKey"), LenDelim(2, innerF2));

        // 3. outer: field 1 = item
        var outer = LenDelim(1, item);
        return Convert.ToBase64String(outer);
    }

    public static string BuildOauthToken(string accessToken, string refreshToken, string idToken, long expirySecs)
    {
        // 1. expiry message: field 1 = expirySecs, field 2 = 0
        var expMsg = Combine(VarintField(1, (ulong)expirySecs), VarintField(2, 0));

        // 2. inner token: field 1 = access_token, field 2 = "Bearer", field 3 = refresh_token, field 4 = expMsg, field 5 = id_token
        var innerToken = Combine(
            LenDelim(1, accessToken),
            LenDelim(2, "Bearer"),
            LenDelim(3, refreshToken),
            LenDelim(4, expMsg),
            LenDelim(5, idToken)
        );
        var innerB64 = Convert.ToBase64String(innerToken);

        // 3. msg1: authStateWithContextSentinelKey with signedIn
        const string stateJson = "{\"state\":\"signedIn\",\"context\":{\"project\":\"\",\"showProjectError\":false,\"errorMessage\":\"\",\"ineligibleMessage\":\"\",\"verificationUrl\":\"\",\"isGcpTos\":false,\"browserOpenFailed\":false,\"appealUrl\":\"\",\"appealLinkText\":\"\"}}";
        var msg1 = Combine(
            LenDelim(1, "authStateWithContextSentinelKey"),
            LenDelim(2, LenDelim(1, stateJson))
        );

        // 4. msg2: oauthTokenInfoSentinelKey with innerB64
        var msg2 = Combine(
            LenDelim(1, "oauthTokenInfoSentinelKey"),
            LenDelim(2, LenDelim(1, innerB64))
        );

        // 5. outer: field 1 = msg1, field 1 = msg2
        var outer = Combine(LenDelim(1, msg1), LenDelim(1, msg2));
        return Convert.ToBase64String(outer);
    }

    public static Dictionary<string, string> BuildAuthValues(Profile profile)
    {
        var dict = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(profile.AccessToken) && !string.IsNullOrEmpty(profile.RefreshToken))
        {
            long exp = profile.ExpiryTimestamp ?? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
            var oauthToken = BuildOauthToken(profile.AccessToken, profile.RefreshToken, profile.IdToken ?? "", exp);
            var userStatus = BuildUserStatus(profile.Email);
            dict[AgState.KeyOauthToken] = oauthToken;
            dict[AgState.KeyUserStatus] = userStatus;
        }
        return dict;
    }
}
