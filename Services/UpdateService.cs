using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

public class UpdateInfo
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string Title { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string AssetName { get; set; } = "";
    public long AssetSize { get; set; }

    public string SizeFormatted
    {
        get
        {
            if (AssetSize <= 0) return "";
            if (AssetSize < 1024 * 1024) return $"{AssetSize / 1024.0:F1} KB";
            return $"{AssetSize / (1024.0 * 1024.0):F1} MB";
        }
    }
}

public static class UpdateService
{
    private const string RepoOwner = "kbkkb";
    private const string RepoName = "APISwitch";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static UpdateService()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("APISwitch-Updater", GetCurrentVersionString()));
    }

    public static string GetCurrentVersionString()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "0.1.1";
    }

    public static async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var resp = await Http.SendAsync(req);
            var currentVer = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 1);
            var currentVerStr = $"v{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}";

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateInfo
                {
                    HasUpdate = false,
                    CurrentVersion = currentVerStr,
                    LatestVersion = currentVerStr,
                    Title = "暂无更新",
                    ReleaseNotes = "当前仓库尚未发布新版本，您当前运行的已是最新版本。"
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var node = JsonNode.Parse(json);
            if (node == null) return null;

            var tagName = node["tag_name"]?.ToString() ?? "";
            var title = node["name"]?.ToString() ?? tagName;
            var body = node["body"]?.ToString() ?? "";
            var htmlUrl = node["html_url"]?.ToString() ?? $"https://github.com/{RepoOwner}/{RepoName}/releases";
            var publishedStr = node["published_at"]?.ToString();
            DateTime? publishedAt = null;
            if (DateTime.TryParse(publishedStr, out var dt)) publishedAt = dt.ToLocalTime();

            var cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanTag, out var remoteVer))
            {
                var parts = cleanTag.Split('.');
                if (parts.Length == 2) Version.TryParse($"{cleanTag}.0", out remoteVer);
            }

            var hasUpdate = remoteVer != null && remoteVer > currentVer;

            // Pick the best download asset (prefer .zip, then .exe)
            string downloadUrl = "";
            string assetName = "";
            long assetSize = 0;

            if (node["assets"] is JsonArray assets && assets.Count > 0)
            {
                JsonNode? bestAsset = null;
                foreach (var a in assets)
                {
                    if (a == null) continue;
                    var name = a["name"]?.ToString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        bestAsset = a;
                        break;
                    }
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && bestAsset == null)
                    {
                        bestAsset = a;
                    }
                }

                bestAsset ??= assets[0];
                if (bestAsset != null)
                {
                    downloadUrl = bestAsset["browser_download_url"]?.ToString() ?? "";
                    assetName = bestAsset["name"]?.ToString() ?? "";
                    assetSize = bestAsset["size"]?.GetValue<long>() ?? 0;
                }
            }

            return new UpdateInfo
            {
                HasUpdate = hasUpdate,
                CurrentVersion = $"v{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}",
                LatestVersion = tagName.StartsWith('v') || tagName.StartsWith('V') ? tagName : $"v{tagName}",
                Title = title,
                ReleaseNotes = string.IsNullOrWhiteSpace(body) ? "（作者未提供详细更新说明）" : body,
                PublishedAt = publishedAt,
                HtmlUrl = htmlUrl,
                DownloadUrl = downloadUrl,
                AssetName = assetName,
                AssetSize = assetSize
            };
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadAssetAsync(string url, string destinationPath, IProgress<(long downloaded, long total, double percent)>? progress, CancellationToken ct = default)
    {
        var tempDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(tempDir) && !Directory.Exists(tempDir))
        {
            Directory.CreateDirectory(tempDir);
        }

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[16384];
        long totalRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            if (totalBytes > 0 && progress != null)
            {
                var percent = (double)totalRead / totalBytes * 100.0;
                progress.Report((totalRead, totalBytes, percent));
            }
        }
    }

    public static void ApplyUpdateAndRestart(string packagePath)
    {
        var currentPid = Environment.ProcessId;
        var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        var mainExe = Environment.ProcessPath ?? Path.Combine(appDir, "APISwitch.exe");
        var tempDir = Path.Combine(Path.GetTempPath(), "APISwitch_Update_Work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "apply_update.ps1");
        string psScript;

        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            psScript = @"
$pidToWait = " + currentPid + @"
$zipPath = '" + packagePath.Replace("'", "''") + @"'
$targetDir = '" + appDir.Replace("'", "''") + @"'
$mainExe = '" + mainExe.Replace("'", "''") + @"'

while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Milliseconds 500

try {
    Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force
} catch {
    Start-Sleep -Seconds 1
    Expand-Archive -Path $zipPath -DestinationPath $targetDir -Force
}

Start-Process -FilePath $mainExe
";
        }
        else
        {
            psScript = @"
$pidToWait = " + currentPid + @"
$sourceExe = '" + packagePath.Replace("'", "''") + @"'
$mainExe = '" + mainExe.Replace("'", "''") + @"'

while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Milliseconds 500

try {
    Copy-Item -Path $sourceExe -Destination $mainExe -Force
} catch {
    Start-Sleep -Seconds 1
    Copy-Item -Path $sourceExe -Destination $mainExe -Force
}

Start-Process -FilePath $mainExe
";
        }

        File.WriteAllText(scriptPath, psScript, System.Text.Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true
        };

        Process.Start(psi);
        System.Windows.Application.Current.Shutdown();
    }
}