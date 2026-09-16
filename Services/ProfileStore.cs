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
        foreach (var f in Directory.GetFiles(AgPaths.ProfilesDir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(f));
                if (p == null) continue;
                p.FilePath = f;
                list.Add(p);
            }
            catch { }
        }
        return list.OrderByDescending(p => p.CapturedAtUtc).ToList();
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
