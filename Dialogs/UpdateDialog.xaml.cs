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
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? I18nService.T("UD.ReleaseNewFmt") : info.Title;

        PublishDateText.Text = info.PublishedAt.HasValue
            ? I18nService.F("UD.PublishedFmt", info.PublishedAt.Value.ToString("yyyy-MM-dd HH:mm"))
            : I18nService.T("UD.PublishedRecent");

        PackageSizeText.Text = string.IsNullOrWhiteSpace(info.SizeFormatted)
            ? ""
            : I18nService.F("UD.SizeFmt", info.SizeFormatted);

        ReleaseNotesText.Text = info.ReleaseNotes;

        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            BtnInstall.Content = I18nService.T("UD.GoGitHubDownload");
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
            MessageBox.Show(I18nService.F("UD.OpenBrowserFail", ex.Message));
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
        BtnCancel.Content = I18nService.T("UD.CancelDownload");
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = I18nService.T("UD.Connecting");

        _cts = new CancellationTokenSource();

        var assetName = string.IsNullOrEmpty(_info.AssetName) ? "APISwitch_Update.zip" : Path.GetFileName(_info.AssetName);
        var tempDir = Path.Combine(Path.GetTempPath(), "APISwitch_Update", Guid.NewGuid().ToString("N"));
        var destFile = Path.Combine(tempDir, assetName);

        var progress = new Progress<(long downloaded, long total, double percent)>(p =>
        {
            DownloadProgressBar.Value = p.percent;
            ProgressPercentText.Text = $"{p.percent:F0}%";
            if (p.total > 0)
            {
                ProgressStatusText.Text = I18nService.F("UD.DownloadingFmt", p.downloaded / 1048576.0, p.total / 1048576.0);
            }
            else
            {
                ProgressStatusText.Text = I18nService.F("UD.DownloadedFmt", p.downloaded / 1048576.0);
            }
        });

        try
        {
            await UpdateService.DownloadAssetAsync(_info.DownloadUrl, destFile, progress, _cts.Token);

            ProgressStatusText.Text = I18nService.T("UD.DownloadDone");
            ProgressPercentText.Text = "100%";
            DownloadProgressBar.Value = 100;

            await Task.Delay(600);

            UpdateService.ApplyUpdateAndRestart(destFile);
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = I18nService.T("UD.LaterBtn");
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = I18nService.F("UD.DownloadFailFmt", ex.Message);
            BtnInstall.IsEnabled = true;
            BtnCancel.Content = I18nService.T("UD.CloseBtn");
        }
    }
}