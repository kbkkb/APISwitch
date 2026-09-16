using System.Windows;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public partial class ImportDialog : Window
{
    readonly List<CcImportItem> _items;

    public bool Imported { get; private set; }

    public ImportDialog()
    {
        InitializeComponent();
        _items = CcSwitchImport.Load();
        foreach (var i in _items) i.Selected = i.IsCurrent;
        ItemsList.ItemsSource = _items;
        if (_items.Count == 0)
            ImportBtn.IsEnabled = false;
    }

    void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = true;
        ItemsList.Items.Refresh();
    }

    void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = false;
        ItemsList.Items.Refresh();
    }

    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    void OnImport(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请至少勾选一项。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var (nc, nd, nx, no) = CcSwitchImport.Import(selected);
            Imported = true;
            MessageBox.Show(
                $"导入完成：Claude CLI {nc}，Claude 客户端 {nd}，Codex {nx}，OpenCode {no} 个。",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败：" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
