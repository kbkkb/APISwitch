using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow
{
    // ==================== Modern Real-time Floating Drag & Drop Engine ====================

    enum DragCategory { None, Tab, Card }
    DragCategory _activeDragCategory = DragCategory.None;

    // Tab dragging
    TabItem? _draggedTab;
    WpfPoint _tabDragStartPoint;
    WpfPoint _tabGrabOffset;
    bool _isTabDragging = false;
    bool _isReorderingTabs = false;

    // Card dragging
    System.Windows.Controls.ListBox? _draggedCardListBox;
    ListBoxItem? _draggedCardItem;
    object? _draggedCardData;
    WpfPoint _cardDragStartPoint;
    WpfPoint _cardGrabOffset;
    bool _isCardDragging = false;
    bool _isReorderingCards = false;
    double _cardFixedX = 250;

    static string TabOrderFile => Path.Combine(AgPaths.AppData, "APISwitch", "tab-order.json");

    static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    static bool IsVisualChildOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, parent)) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    void OnTabItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // The TabItem Header is on the sidebar (Column 0, width 236).
        // If the click is anywhere on the right content area (X >= 236), ignore completely so cards can be dragged!
        var pos = e.GetPosition(this);
        if (pos.X >= 236) return;

        var dep = e.OriginalSource as DependencyObject;
        if (sender is TabItem tab)
        {
            var bd = tab.Template?.FindName("Bd", tab) as FrameworkElement ?? tab;
            if (bd != null && !IsVisualChildOf(dep, bd))
            {
                return;
            }

            // Clear any card drag state
            _draggedCardListBox = null;
            _draggedCardItem = null;
            _draggedCardData = null;
            _isCardDragging = false;

            _draggedTab = tab;
            _tabDragStartPoint = pos;
            _tabGrabOffset = e.GetPosition(bd);
            _isTabDragging = false;
        }
    }

    void OnCardListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not System.Windows.Controls.ListBox lb) return;

        // Clear any tab drag state
        _draggedTab = null;
        _isTabDragging = false;

        var dep = e.OriginalSource as DependencyObject;
        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(dep) != null ||
            FindVisualParent<System.Windows.Controls.TextBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.PasswordBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.ComboBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.CheckBox>(dep) != null)
        {
            return;
        }

        var item = FindVisualParent<ListBoxItem>(dep);
        if (item != null && item.DataContext != null)
        {
            _draggedCardListBox = lb;
            _draggedCardItem = item;
            _draggedCardData = item.DataContext;
            _cardDragStartPoint = e.GetPosition(this);
            _cardGrabOffset = e.GetPosition(item);
            _isCardDragging = false;
        }
    }

    void OnWindowPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_isTabDragging || _isCardDragging)
            {
                EndAllDrag();
            }
            return;
        }

        var currentPos = e.GetPosition(this);

        // 1. Check initiation of Tab Drag
        if (_draggedTab != null && !_isTabDragging && _activeDragCategory == DragCategory.None)
        {
            var diff = currentPos - _tabDragStartPoint;
            if (Math.Abs(diff.Y) > 5 || Math.Abs(diff.X) > 5)
            {
                StartTabDrag(currentPos);
            }
        }

        // 2. Check initiation of Card Drag
        if (_draggedCardItem != null && !_isCardDragging && _activeDragCategory == DragCategory.None)
        {
            var diff = currentPos - _cardDragStartPoint;
            if (Math.Abs(diff.Y) > 5 || Math.Abs(diff.X) > 5)
            {
                StartCardDrag(currentPos);
            }
        }

        // 3. Process Tab Dragging
        if (_isTabDragging && _draggedTab != null)
        {
            UpdateGhostPosition();
            LiveReorderTab(currentPos);
            e.Handled = true;
            return;
        }

        // 4. Process Card Dragging
        if (_isCardDragging && _draggedCardListBox != null && _draggedCardData != null)
        {
            UpdateGhostPosition();
            LiveReorderCard(currentPos);
            e.Handled = true;
            return;
        }
    }

    void StartTabDrag(WpfPoint mousePos)
    {
        if (_draggedTab == null) return;
        _isTabDragging = true;
        _activeDragCategory = DragCategory.Tab;

        // Render only the sidebar tab button Bd, NEVER the full content page
        FrameworkElement targetVisual = _draggedTab;
        if (_draggedTab.Template?.FindName("Bd", _draggedTab) is FrameworkElement bd)
        {
            targetVisual = bd;
        }

        double w = targetVisual.ActualWidth > 0 ? targetVisual.ActualWidth : 216;
        double h = targetVisual.ActualHeight > 0 ? targetVisual.ActualHeight : 42;

        if (_tabGrabOffset.X <= 0 || _tabGrabOffset.X > w || _tabGrabOffset.Y <= 0 || _tabGrabOffset.Y > h)
        {
            _tabGrabOffset = new WpfPoint(Math.Min(30, w / 2), Math.Min(20, h / 2));
        }

        DragGhostRect.Width = w;
        DragGhostRect.Height = h;
        DragGhostBorder.Width = w;
        DragGhostBorder.Height = h;
        DragGhostBorder.CornerRadius = new CornerRadius(8);
        DragGhostRect.RadiusX = 8;
        DragGhostRect.RadiusY = 8;

        int width = Math.Max(1, (int)Math.Ceiling(w));
        int height = Math.Max(1, (int)Math.Ceiling(h));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(targetVisual)
            {
                Stretch = Stretch.None,
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        DragGhostRect.Fill = new ImageBrush(rtb);

        targetVisual.Opacity = 0.35;
        DragOverlayCanvas.Visibility = Visibility.Visible;
        UpdateGhostPosition();

        this.CaptureMouse();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
    }

    void StartCardDrag(WpfPoint mousePos)
    {
        if (_draggedCardItem == null || _draggedCardListBox == null) return;
        _isCardDragging = true;
        _activeDragCategory = DragCategory.Card;

        double w = _draggedCardItem.ActualWidth;
        double h = _draggedCardItem.ActualHeight;
        if (w <= 0 || h <= 0) return;

        try
        {
            var origin = _draggedCardItem.TranslatePoint(new WpfPoint(0, 0), this);
            _cardFixedX = Math.Max(200, origin.X);
        }
        catch
        {
            _cardFixedX = 250;
        }

        if (_cardGrabOffset.X <= 0 || _cardGrabOffset.X > w || _cardGrabOffset.Y <= 0 || _cardGrabOffset.Y > h)
        {
            _cardGrabOffset = new WpfPoint(Math.Min(40, w / 2), Math.Min(25, h / 2));
        }

        DragGhostRect.Width = w;
        DragGhostRect.Height = h;
        DragGhostBorder.Width = w;
        DragGhostBorder.Height = h;
        DragGhostBorder.CornerRadius = new CornerRadius(12);
        DragGhostRect.RadiusX = 12;
        DragGhostRect.RadiusY = 12;

        int width = Math.Max(1, (int)Math.Ceiling(w));
        int height = Math.Max(1, (int)Math.Ceiling(h));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(_draggedCardItem)
            {
                Stretch = Stretch.None,
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        DragGhostRect.Fill = new ImageBrush(rtb);

        _draggedCardItem.Opacity = 0.25;
        DragOverlayCanvas.Visibility = Visibility.Visible;
        UpdateGhostPosition();

        this.CaptureMouse();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
    }

    void UpdateGhostPosition()
    {
        if (DragOverlayCanvas == null || DragGhostBorder == null) return;

        double ghostH = DragGhostBorder.Height > 0 ? DragGhostBorder.Height : 50;
        var mouseWin = Mouse.GetPosition(this);

        double x;
        double y;

        if (_isTabDragging)
        {
            x = 8;
            y = mouseWin.Y - _tabGrabOffset.Y;
            double minY = 46;
            double maxY = Math.Max(minY, this.ActualHeight - ghostH - 12);
            y = Math.Clamp(y, minY, maxY);
        }
        else // Card dragging
        {
            double deltaX = Math.Clamp(mouseWin.X - _cardDragStartPoint.X, -25, 25);
            x = _cardFixedX + deltaX;

            y = mouseWin.Y - _cardGrabOffset.Y;
            double minY = 46;
            double maxY = Math.Max(minY, this.ActualHeight - ghostH - 12);
            y = Math.Clamp(y, minY, maxY);
        }

        Canvas.SetLeft(DragGhostBorder, x);
        Canvas.SetTop(DragGhostBorder, y);
    }

    void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isTabDragging || _isCardDragging)
        {
            EndAllDrag();
            e.Handled = true;
        }
        else
        {
            _draggedTab = null;
            _draggedCardItem = null;
            _draggedCardData = null;
            _draggedCardListBox = null;
            _isTabDragging = false;
            _isCardDragging = false;
            _activeDragCategory = DragCategory.None;
        }
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isReorderingTabs || _isReorderingCards) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            if (_isTabDragging || _isCardDragging)
            {
                this.CaptureMouse();
                return;
            }
        }
        if (_isTabDragging || _isCardDragging)
        {
            EndAllDrag();
        }
    }

    void EndAllDrag()
    {
        bool wasTabDragging = _isTabDragging;
        bool wasCardDragging = _isCardDragging;
        var finishedCardLb = _draggedCardListBox;

        DragOverlayCanvas.Visibility = Visibility.Collapsed;
        DragGhostRect.Fill = null;

        if (_draggedTab != null)
        {
            _draggedTab.Opacity = 1.0;
            if (_draggedTab.Template?.FindName("Bd", _draggedTab) is FrameworkElement bd)
            {
                bd.Opacity = 1.0;
            }
        }
        if (_draggedCardItem != null)
        {
            _draggedCardItem.Opacity = 1.0;
        }
        if (finishedCardLb != null)
        {
            for (int i = 0; i < finishedCardLb.Items.Count; i++)
            {
                if (finishedCardLb.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi)
                {
                    lbi.Opacity = 1.0;
                }
            }
        }

        this.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;

        if (wasTabDragging)
        {
            SaveTabOrder();
        }

        if (wasCardDragging && finishedCardLb != null)
        {
            SaveCardOrder(finishedCardLb);
        }

        _draggedTab = null;
        _isTabDragging = false;
        _isReorderingTabs = false;

        _draggedCardListBox = null;
        _draggedCardItem = null;
        _draggedCardData = null;
        _isCardDragging = false;
        _isReorderingCards = false;
        _activeDragCategory = DragCategory.None;
    }

    void SaveCardOrder(System.Windows.Controls.ListBox lb)
    {
        try
        {
            if (lb == ProfileList)
            {
                var deduped = _profiles.GroupBy(p => !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                ProfileStore.SaveOrder(deduped);
            }
            else if (lb == CodexList)
            {
                var list = CliStore.DeduplicateCodex(_codexCollection.Select(r => r.P).ToList());
                _codexProviders = list;
                CliStore.SaveCodex(list);
            }
            else if (lb == ClaudeList)
            {
                var list = CliStore.DeduplicateClaude(_claudeCollection.Select(r => r.P).ToList());
                _claudeProviders = list;
                CliStore.SaveClaude(list);
            }
            else if (lb == DesktopList)
            {
                var list = CliStore.DeduplicateClaude(_desktopCollection.Select(r => r.P).ToList());
                _desktopProviders = list;
                CliStore.SaveClaudeDesktop(list);
            }
            else if (lb == OcList)
            {
                var list = CliStore.DeduplicateOpenCode(_ocCollection.Select(r => r.P).ToList());
                _openCodeProviders = list;
                CliStore.SaveOpencode(list);
            }
            else if (lb == PiList)
            {
                var list = CliStore.DeduplicatePi(_piCollection.Select(r => r.P).ToList());
                _piProviders = list;
                CliStore.SavePiProviders(list);
            }
        }
        catch { }
    }

    void LiveReorderTab(WpfPoint currentPos)
    {
        if (_draggedTab == null || _isReorderingTabs) return;

        int currentIndex = Tabs.Items.IndexOf(_draggedTab);
        if (currentIndex < 0) return;

        // Check swapping with upper tab
        if (currentIndex > 0 && Tabs.Items[currentIndex - 1] is TabItem prevTab)
        {
            var prevVisual = prevTab.Template?.FindName("Bd", prevTab) as FrameworkElement ?? prevTab;
            if (TryGetMidY(prevVisual, this, out double prevMid) && currentPos.Y < prevMid)
            {
                _isReorderingTabs = true;
                try
                {
                    var selected = Tabs.SelectedItem;
                    Tabs.Items.RemoveAt(currentIndex);
                    Tabs.Items.Insert(currentIndex - 1, _draggedTab);
                    Tabs.SelectedItem = selected;
                    Tabs.UpdateLayout();
                    this.CaptureMouse();
                }
                catch { }
                finally
                {
                    _isReorderingTabs = false;
                }
                return;
            }
        }

        // Check swapping with lower tab
        if (currentIndex < Tabs.Items.Count - 1 && Tabs.Items[currentIndex + 1] is TabItem nextTab)
        {
            var nextVisual = nextTab.Template?.FindName("Bd", nextTab) as FrameworkElement ?? nextTab;
            if (TryGetMidY(nextVisual, this, out double nextMid) && currentPos.Y > nextMid)
            {
                _isReorderingTabs = true;
                try
                {
                    var selected = Tabs.SelectedItem;
                    Tabs.Items.RemoveAt(currentIndex);
                    Tabs.Items.Insert(currentIndex + 1, _draggedTab);
                    Tabs.SelectedItem = selected;
                    Tabs.UpdateLayout();
                    this.CaptureMouse();
                }
                catch { }
                finally
                {
                    _isReorderingTabs = false;
                }
                return;
            }
        }
    }

    static bool TryGetMidY(FrameworkElement? el, UIElement relativeTo, out double midY)
    {
        midY = 0;
        if (el == null || !el.IsLoaded || !el.IsVisible) return false;
        try
        {
            var pt = el.TranslatePoint(new WpfPoint(0, el.ActualHeight / 2), relativeTo);
            midY = pt.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    void LiveReorderCard(WpfPoint currentPos)
    {
        if (_draggedCardListBox == null || _draggedCardData == null || _isReorderingCards) return;

        var lb = _draggedCardListBox;
        int count = lb.Items.Count;
        if (count <= 1) return;

        int currentIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (ReferenceEquals(lb.Items[i], _draggedCardData) || IsMatchingRow(lb.Items[i], _draggedCardData))
            {
                currentIndex = i;
                break;
            }
        }
        if (currentIndex < 0) return;

        // Auto-scroll when dragging near top/bottom edges of the scroll container
        var sv = FindVisualParent<ScrollViewer>(lb);
        if (sv != null)
        {
            try
            {
                var svTopLeft = sv.TranslatePoint(new WpfPoint(0, 0), this);
                double relY = currentPos.Y - svTopLeft.Y;
                if (relY < 40 && sv.VerticalOffset > 0)
                {
                    sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - 14));
                }
                else if (relY > sv.ActualHeight - 40 && sv.VerticalOffset < sv.ScrollableHeight)
                {
                    sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 14));
                }
            }
            catch { }
        }

        // Check upper card (Move UP)
        if (currentIndex > 0)
        {
            var prevContainer = (lb.ItemContainerGenerator.ContainerFromIndex(currentIndex - 1) as FrameworkElement)
                                ?? FindListBoxItemByData(lb, lb.Items[currentIndex - 1]);
            if (TryGetMidY(prevContainer, this, out double prevMid) && currentPos.Y < prevMid)
            {
                SwapCardItems(currentIndex, currentIndex - 1);
                return;
            }
        }

        // Check lower card (Move DOWN)
        if (currentIndex < count - 1)
        {
            var nextContainer = (lb.ItemContainerGenerator.ContainerFromIndex(currentIndex + 1) as FrameworkElement)
                                ?? FindListBoxItemByData(lb, lb.Items[currentIndex + 1]);
            if (TryGetMidY(nextContainer, this, out double nextMid) && currentPos.Y > nextMid)
            {
                SwapCardItems(currentIndex, currentIndex + 1);
                return;
            }
        }
    }

    void SwapCardItems(int fromIndex, int toIndex)
    {
        if (_isReorderingCards) return;
        _isReorderingCards = true;
        try
        {
            var lb = _draggedCardListBox;
            if (lb == null) return;
            int count = lb.Items.Count;
            if (fromIndex < 0 || fromIndex >= count || toIndex < 0 || toIndex >= count || fromIndex == toIndex) return;

            if (lb == ProfileList)
            {
                _profiles.Move(fromIndex, toIndex);
            }
            else if (lb == CodexList)
            {
                _codexCollection.Move(fromIndex, toIndex);
            }
            else if (lb == ClaudeList)
            {
                _claudeCollection.Move(fromIndex, toIndex);
            }
            else if (lb == DesktopList)
            {
                _desktopCollection.Move(fromIndex, toIndex);
            }
            else if (lb == OcList)
            {
                _ocCollection.Move(fromIndex, toIndex);
            }
            else if (lb == PiList)
            {
                _piCollection.Move(fromIndex, toIndex);
            }

            lb.UpdateLayout();

            // Synchronize opacity: ensure only the item at toIndex is dimmed, all others 1.0
            for (int i = 0; i < lb.Items.Count; i++)
            {
                var lbi = lb.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem
                          ?? FindListBoxItemByData(lb, lb.Items[i]);
                if (lbi != null)
                {
                    if (i == toIndex)
                    {
                        lbi.Opacity = 0.25;
                        _draggedCardItem = lbi;
                    }
                    else
                    {
                        lbi.Opacity = 1.0;
                    }
                }
            }
        }
        catch { }
        finally
        {
            _isReorderingCards = false;
        }
    }

    static bool IsMatchingRow(object? a, object? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a is Profile pa && b is Profile pb)
            return string.Equals(pa.Email, pb.Email, StringComparison.OrdinalIgnoreCase);
        if (a is CodexRow ca && b is CodexRow cb)
            return (string.Equals(ca.P.Id, cb.P.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ca.P.Name, cb.P.Name, StringComparison.OrdinalIgnoreCase)) ||
                   string.Equals(ca.P.Name, cb.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is ClaudeRow cla && b is ClaudeRow clb)
            return string.Equals(cla.P.Name, clb.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is OpenCodeRow oa && b is OpenCodeRow ob)
            return string.Equals(oa.P.Id, ob.P.Id, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(oa.P.Name, ob.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is PiRow pia && b is PiRow pib)
            return string.Equals(pia.P.Id, pib.P.Id, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pia.P.Name, pib.P.Name, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject? depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(depObj);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var grandChild in FindVisualChildren<T>(child))
                yield return grandChild;
        }
    }

    static ListBoxItem? FindListBoxItemByData(System.Windows.Controls.ListBox lb, object? data)
    {
        if (data == null) return null;
        if (lb.ItemContainerGenerator.ContainerFromItem(data) is ListBoxItem item)
            return item;
        return FindVisualChildren<ListBoxItem>(lb).FirstOrDefault(x => ReferenceEquals(x.DataContext, data) || IsMatchingRow(x.DataContext, data));
    }

    void SaveTabOrder()
    {
        try
        {
            var tags = Tabs.Items.OfType<TabItem>()
                .Select(t => t.Tag as string)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            var dir = Path.GetDirectoryName(TabOrderFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TabOrderFile, JsonSerializer.Serialize(tags));
        }
        catch { }
    }

    void RestoreTabOrder()
    {
        try
        {
            if (!File.Exists(TabOrderFile)) return;
            var tags = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TabOrderFile));
            if (tags == null || tags.Count == 0) return;

            var allTabs = Tabs.Items.OfType<TabItem>().ToList();
            _isReorderingTabs = true;
            try
            {
                Tabs.Items.Clear();
                foreach (var tag in tags)
                {
                    var tab = allTabs.FirstOrDefault(t => (t.Tag as string) == tag);
                    if (tab != null)
                    {
                        Tabs.Items.Add(tab);
                        allTabs.Remove(tab);
                    }
                }
                foreach (var remaining in allTabs)
                {
                    Tabs.Items.Add(remaining);
                }
                if (Tabs.Items.Count > 0)
                {
                    Tabs.SelectedIndex = 0;
                }
            }
            finally
            {
                _isReorderingTabs = false;
            }
        }
        catch { }
    }

}
