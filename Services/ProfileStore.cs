using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using APISwitch.Models;

namespace APISwitch.Services;

public static class ProfileStore
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<Profile> Load()
    {
        var list = new List<Profile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(AgPaths.ProfilesDir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(f));
                if (p == null) continue;
                var key = !string.IsNullOrWhiteSpace(p.Email) ? p.Email : p.Name;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (seen.Contains(key)) continue;
                    seen.Add(key);
                }
                p.FilePath = f;
                list.Add(p);
            }
            catch { }
        }

        if (File.Exists(OrderFile))
        {
            try
            {
                var order = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderFile));
                if (order != null && order.Count > 0)
                {
                    var map = order.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx, StringComparer.OrdinalIgnoreCase);
                    return list.OrderBy(p =>
                    {
                        var key = !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name;
                        return map.TryGetValue(key, out var idx) ? idx : 99999;
                    }).ThenByDescending(p => p.CapturedAtUtc).ToList();
                }
            }
            catch { }
        }
        return list.OrderByDescending(p => p.CapturedAtUtc).ToList();
    }

    static string OrderFile => Path.Combine(AgPaths.AppData, "APISwitch", "antigravity-order.json");

    public static void SaveOrder(IEnumerable<Profile> profiles)
    {
        try
        {
            var keys = profiles.Select(p => !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name).Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dir = Path.GetDirectoryName(OrderFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(OrderFile, JsonSerializer.Serialize(keys, JsonOpts));
        }
        catch { }
    }

    public static void Save(Profile p)
    {
        var slug = Sanitize(string.IsNullOrEmpty(p.Email) ? p.Name : p.Email);
        if (string.IsNullOrEmpty(slug)) slug = "account-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        var path = Path.Combine(AgPaths.ProfilesDir, slug + ".json");
        p.FilePath = path;
        File.WriteAllText(path, JsonSerializer.Serialize(p, JsonOpts));
    }

    public static void Delete(Profile p)
    {
        if (!string.IsNullOrEmpty(p.FilePath) && File.Exists(p.FilePath))
            File.Delete(p.FilePath);
    }

    static string Sanitize(string s) =>
        new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
}
