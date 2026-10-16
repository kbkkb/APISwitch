using System.Diagnostics;
using System.IO;
using System.Windows;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private CancellationTokenSource? _cts;
    private string _currentNotesLang = "zh-CN";

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

        if (string.IsNullOrEmpty(info.DownloadUrl))
        {
            BtnInstall.Content = I18nService.T("UD.GoGitHubDownload");
        }

        _currentNotesLang = I18nService.ResolvedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        UpdateNotesDisplay();

        I18nService.LanguageChanged += OnLanguageChanged;
        Closed += (s, e) => I18nService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        _currentNotesLang = I18nService.ResolvedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        UpdateNotesDisplay();
    }

    private void OnSelectZh(object sender, RoutedEventArgs e)
    {
        _currentNotesLang = "zh-CN";
        UpdateNotesDisplay();
    }

    private void OnSelectEn(object sender, RoutedEventArgs e)
    {
        _currentNotesLang = "en-US";
        UpdateNotesDisplay();
    }

    private void UpdateNotesDisplay()
    {
        bool isZh = _currentNotesLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        var activeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
        var activeFg = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var inactiveBg = System.Windows.Media.Brushes.Transparent;
        var inactiveFg = (System.Windows.Media.Brush)FindResource("TextDimBrush");

        BtnLangZh.Background = isZh ? activeBg : inactiveBg;
        BtnLangZh.Foreground = isZh ? activeFg : inactiveFg;
        BtnLangZh.FontWeight = isZh ? FontWeights.SemiBold : FontWeights.Normal;

        BtnLangEn.Background = !isZh ? activeBg : inactiveBg;
        BtnLangEn.Foreground = !isZh ? activeFg : inactiveFg;
        BtnLangEn.FontWeight = !isZh ? FontWeights.SemiBold : FontWeights.Normal;

        ReleaseNotesText.Text = _info.GetLocalizedReleaseNotes(_currentNotesLang);
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