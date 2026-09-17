using System.Windows;

namespace APISwitch.Dialogs;

public enum CopyTarget { ClaudeCli, ClaudeDesktop, Codex, OpenCode, Pi }

public partial class CopyTargetsDialog : Window
{
    public List<CopyTarget> Targets { get; } = new();

    public CopyTargetsDialog(string sourceName, CopyTarget exclude)
    {
        InitializeComponent();
        SourceText.Text = $"将「{sourceName}」复制到：";
        if (exclude == CopyTarget.ClaudeCli) ClaudeCheck.Visibility = Visibility.Collapsed;
        if (exclude == CopyTarget.ClaudeDesktop) DesktopCheck.Visibility = Visibility.Collapsed;
        if (exclude == CopyTarget.Codex) CodexCheck.Visibility = Visibility.Collapsed;
        if (exclude == CopyTarget.OpenCode) OpenCodeCheck.Visibility = Visibility.Collapsed;
        if (exclude == CopyTarget.Pi) PiCheck.Visibility = Visibility.Collapsed;
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (ClaudeCheck.IsChecked == true) Targets.Add(CopyTarget.ClaudeCli);
        if (DesktopCheck.IsChecked == true) Targets.Add(CopyTarget.ClaudeDesktop);
        if (CodexCheck.IsChecked == true) Targets.Add(CopyTarget.Codex);
        if (OpenCodeCheck.IsChecked == true) Targets.Add(CopyTarget.OpenCode);
        if (PiCheck.IsChecked == true) Targets.Add(CopyTarget.Pi);
        if (Targets.Count == 0)
        {
            MessageBox.Show("请至少选择一个目标。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
