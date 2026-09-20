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
            MessageBox.Show(I18nService.T("ID.SelectOne"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var (nc, nd, nx, no, np) = CcSwitchImport.Import(selected);
            Imported = true;
            MessageBox.Show(
                I18nService.F("ID.DoneFmt", nc, nd, nx, no, np),
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(I18nService.F("ID.FailFmt", ex.Message), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
