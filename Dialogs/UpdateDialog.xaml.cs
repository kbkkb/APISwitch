using System.Diagnostics;
using System.IO;
using System.Windows;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        VersionCompareText.Text = $"{info.CurrentVersion} → {info.LatestVersion}";
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? "APISwitch 新版本发布" : info.Title;

        PublishDateText.Text = info.PublishedAt.HasValue
            ? $"发布时间：{info.PublishedAt.Value:yyyy-MM-dd HH:mm}"
            : "发布时间：近期";

        PackageSizeText.Text = string.IsNullOrWhiteSpace(info.SizeFormatted)
            ? ""
            : $"更新包大小：{info.SizeFormatted}";

        ReleaseNotesText.Text = info.ReleaseNotes;

        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            BtnInstall.Content = "前往 GitHub 下载";
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrEmpty(_info.HtmlUrl) ? "https://github.com/kbkkb/APISwitch/releases" : _info.HtmlUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开浏览器: " + ex.Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_info.DownloadUrl))
        {
            OnOpenGitHub(sender, e);
            Close();
            return;
        }

        BtnInstall.IsEnabled = false;
        BtnCancel.Content = "取消下载";
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = "正在连接下载服务器…";

        _cts = new CancellationTokenSource();

        var assetName = string.IsNullOrEmpty(_info.AssetName) ? "APISwitch_Update.zip" : _info.AssetName;
        var tempDir = Path.Combine(Path.GetTempPath(), "APISwitch_Update");
        var destFile = Path.Combine(tempDir, assetName);

        var progress = new Progress<(long downloaded, long total, double percent)>(p =>
        {
            DownloadProgressBar.Value = p.percent;
            ProgressPercentText.Text = $"{p.percent:F0}%";
            if (p.total > 0)
            {
                ProgressStatusText.Text = $"正在下载: {p.downloaded / 1048576.0:F1} MB / {p.total / 1048576.0:F1} MB";
            }
            else
            {
                ProgressStatusText.Text = $"已下载: {p.downloaded / 1048576.0:F1} MB";
            }
        });

        try
        {
            await UpdateService.DownloadAssetAsync(_info.DownloadUrl, destFile, progress, _cts.Token);

            ProgressStatusText.Text = "下载完成！准备自动重启更新…";
            ProgressPercentText.Text = "100%";
            DownloadProgressBar.Value = 100;

            await Task.Delay(600);

            UpdateService.ApplyUpdateAndRestart(destFile);
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = "稍后再说";
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = "下载失败：" + ex.Message;
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = "关闭";
        }
    }
}