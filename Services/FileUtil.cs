using System.IO;

namespace APISwitch.Services;

public static class FileUtil
{
    public static void AtomicWriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var bak = path + ".bak";
        if (File.Exists(path)) File.Copy(path, bak, overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
    }

    public static string? ReadTextIfExists(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}
