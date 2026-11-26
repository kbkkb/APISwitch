using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace APISwitch.Services;

public static class ModernTrayMenu
{
    public sealed record TrayAccountEntry(Models.Profile Profile, string Email, string TierTag, string GemPct, string ClaudePct, bool IsCurrent);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int MenuWidth = 296;

    public static ContextMenuStrip Create(
        Action onShow,
        Action onRefresh,
        Action onLaunchIde,
        Action onExit,
        string version = "v0.1.1",
        Func<IReadOnlyList<TrayAccountEntry>>? getAccounts = null,
        Action<Models.Profile>? onSwitchAccount = null,
        Action? onOpenUsage = null,
        Action? onToggleWidget = null,
        Func<bool>? isWidgetEnabled = null)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ModernTrayMenuRenderer(),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            DropShadowEnabled = true,
            AutoSize = true,
            MinimumSize = new Size(MenuWidth, 0),
            Padding = new Padding(6, 7, 6, 7),
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Regular)
        };

        menu.HandleCreated += (_, _) =>
        {
            try
            {
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            }
            catch { }
        };

        // Header
        var header = new ToolStripMenuItem(version)
        {
            Tag = "header",
            AutoSize = false,
            Width = MenuWidth - 12,
            Height = 36,
            Image = TrayIcons.LoadAppLogo(),
            ImageScaling = ToolStripItemImageScaling.None,
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Padding = new Padding(8, 6, 12, 6)
        };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        // Show Main Window
        var showItem = new ToolStripMenuItem(I18nService.T("Tray.Show"))
        {
            Image = TrayIcons.CreateWindowIcon(),
            ImageScaling = ToolStripItemImageScaling.None,
            Font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Bold),
            Padding = new Padding(8, 6, 16, 6)
        };
        showItem.Click += (_, _) => onShow();
        menu.Items.Add(showItem);

        if (onOpenUsage != null)
        {
            var usageItem = new ToolStripMenuItem(I18nService.T("Tray.UsageMonitor"))
            {
                Image = TrayIcons.CreateChartIcon(),
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(8, 6, 16, 6)
            };
            usageItem.Click += (_, _) => onOpenUsage();
            menu.Items.Add(usageItem);
        }

        ToolStripMenuItem? widgetItem = null;
        if (onToggleWidget != null)
        {
            bool enabledNow = isWidgetEnabled?.Invoke() ?? true;
            widgetItem = new ToolStripMenuItem(I18nService.T(enabledNow ? "Tray.HideWidget" : "Tray.ShowWidget"))
            {
                Image = TrayIcons.CreateWidgetIcon(),
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(8, 6, 16, 6)
            };
            widgetItem.Click += (_, _) => onToggleWidget();
            menu.Items.Add(widgetItem);
        }

        // Refresh Quotas
        var refreshItem = new ToolStripMenuItem(I18nService.T("Tray.Refresh"))
        {
            Image = TrayIcons.CreateLightningIcon(),
            ImageScaling = ToolStripItemImageScaling.None,
            Padding = new Padding(8, 6, 16, 6)
        };
        refreshItem.Click += (_, _) => onRefresh();
        menu.Items.Add(refreshItem);

        // Launch IDE
        var launchItem = new ToolStripMenuItem(I18nService.T("Tray.Launch"))
        {
            Image = TrayIcons.CreateRocketIcon(),
            ImageScaling = ToolStripItemImageScaling.None,
            Padding = new Padding(8, 6, 16, 6)
        };
        launchItem.Click += (_, _) => onLaunchIde();
        menu.Items.Add(launchItem);

        var accountsAnchorSep = new ToolStripSeparator();
        menu.Items.Add(accountsAnchorSep);

        // Exit
        var exitItem = new ToolStripMenuItem(I18nService.T("Tray.Exit"))
        {
            Tag = "danger",
            Image = TrayIcons.CreateExitIcon(),
            ImageScaling = ToolStripItemImageScaling.None,
            Padding = new Padding(8, 6, 16, 6)
        };
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(exitItem);

        if (getAccounts != null || widgetItem != null)
        {
            menu.Opening += (_, _) =>
            {
                try
                {
                    if (widgetItem != null && isWidgetEnabled != null)
                    {
                        widgetItem.Text = I18nService.T(isWidgetEnabled() ? "Tray.HideWidget" : "Tray.ShowWidget");
                    }
                    if (getAccounts == null) return;
                    for (int i = menu.Items.Count - 1; i >= 0; i--)
                    {
                        var tagObj = menu.Items[i].Tag;
                        var t = tagObj as string;
                        if (tagObj is TrayAccountEntry || t == "dyn_acc_hdr" || t == "dyn_acc_sep")
                        {
                            var oldItem = menu.Items[i];
                            var oldImg = oldItem.Image;
                            menu.Items.RemoveAt(i);
                            oldImg?.Dispose();
                            oldItem.Dispose();
                        }
                    }

                    var accounts = getAccounts();
                    if (accounts == null || accounts.Count == 0) return;

                    int insertIdx = menu.Items.IndexOf(accountsAnchorSep);
                    if (insertIdx < 0) insertIdx = menu.Items.Count - 1;

                    var topSep = new ToolStripSeparator { Tag = "dyn_acc_sep" };
                    menu.Items.Insert(insertIdx++, topSep);

                    var subHdr = new ToolStripMenuItem(I18nService.T("Tray.AccountsHeader"))
                    {
                        Tag = "dyn_acc_hdr",
                        Enabled = false,
                        AutoSize = false,
                        Width = MenuWidth - 12,
                        Height = 24,
                        Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Bold),
                        Padding = new Padding(8, 4, 12, 2)
                    };
                    menu.Items.Insert(insertIdx++, subHdr);

                    foreach (var entry in accounts.Take(8))
                    {
                        var accItem = new ToolStripMenuItem(entry.Email)
                        {
                            Tag = entry,
                            AutoSize = false,
                            Width = MenuWidth - 12,
                            Height = 32,
                            Image = TrayIcons.CreateAccountDotIcon(entry.IsCurrent),
                            ImageScaling = ToolStripItemImageScaling.None,
                            Font = new Font("Segoe UI", 9f, entry.IsCurrent ? FontStyle.Bold : FontStyle.Regular),
                            Padding = new Padding(8, 5, 10, 5)
                        };
                        var captured = entry.Profile;
                        var isCurr = entry.IsCurrent;
                        accItem.Click += (_, _) =>
                        {
                            if (!isCurr) onSwitchAccount?.Invoke(captured);
                            else onShow();
                        };
                        menu.Items.Insert(insertIdx++, accItem);
                    }
                }
                catch { }
            };
        }

        return menu;
    }
}

public class ModernTrayColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.White;
    public override Color MenuBorder => Color.FromArgb(226, 232, 240);
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Color.FromArgb(241, 245, 249);
    public override Color MenuStripGradientBegin => Color.White;
    public override Color MenuStripGradientEnd => Color.White;
    public override Color ImageMarginGradientBegin => Color.White;
    public override Color ImageMarginGradientMiddle => Color.White;
    public override Color ImageMarginGradientEnd => Color.White;
    public override Color SeparatorDark => Color.FromArgb(226, 232, 240);
    public override Color SeparatorLight => Color.White;
}

public class ModernTrayMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernTrayMenuRenderer() : base(new ModernTrayColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(226, 232, 240), 1);
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(3, 2, item.Width - 6, item.Height - 4);

        if (item.Tag?.ToString() == "header")
        {
            using var hdrPath = CreateRoundedPath(rect, 7);
            using var hdrBrush = new SolidBrush(Color.FromArgb(244, 247, 255));
            using var hdrPen = new Pen(Color.FromArgb(224, 231, 255), 1);
            e.Graphics.FillPath(hdrBrush, hdrPath);
            e.Graphics.DrawPath(hdrPen, hdrPath);
            return;
        }

        if (item.Tag is ModernTrayMenu.TrayAccountEntry acc && !item.Selected && acc.IsCurrent)
        {
            using var curPath = CreateRoundedPath(rect, 6);
            using var curBrush = new SolidBrush(Color.FromArgb(240, 253, 244));
            using var curPen = new Pen(Color.FromArgb(187, 247, 208), 1);
            e.Graphics.FillPath(curBrush, curPath);
            e.Graphics.DrawPath(curPen, curPath);
            return;
        }

        if (item.Selected && item.Enabled)
        {
            using var path = CreateRoundedPath(rect, 6);
            bool isDanger = item.Tag?.ToString() == "danger";
            var fillColor = isDanger
                ? Color.FromArgb(254, 242, 242)
                : Color.FromArgb(238, 242, 255);
            using var brush = new SolidBrush(fillColor);
            e.Graphics.FillPath(brush, path);

            var borderColor = isDanger
                ? Color.FromArgb(254, 202, 202)
                : Color.FromArgb(199, 210, 254);
            using var pen = new Pen(borderColor, 1);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var tagObj = e.Item.Tag;
        var tag = tagObj as string;

        if (tag == "header")
        {
            var leftRect = new Rectangle(e.TextRectangle.X + 4, 0, Math.Max(80, e.Item.Width - e.TextRectangle.X - 72), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, "APISwitch", e.TextFont, leftRect, Color.FromArgb(15, 23, 42),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            var verText = string.IsNullOrWhiteSpace(e.Text) ? "v0.3.4" : e.Text.Trim();
            using var pillFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            var pillRect = new Rectangle(e.Item.Width - 62, (e.Item.Height - 19) / 2, 52, 19);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pillPath = CreateRoundedPath(pillRect, 5);
            using var pillBg = new SolidBrush(Color.FromArgb(224, 231, 255));
            e.Graphics.FillPath(pillBg, pillPath);
            TextRenderer.DrawText(e.Graphics, verText, pillFont, pillRect, Color.FromArgb(67, 56, 202),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (tag == "dyn_acc_hdr")
        {
            var hRect = new Rectangle(12, 0, e.Item.Width - 20, e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, hRect, Color.FromArgb(100, 116, 139),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }

        if (tagObj is ModernTrayMenu.TrayAccountEntry acc)
        {
            bool hasQuotas = acc.GemPct != "—" || acc.ClaudePct != "—";
            int rightReserve = hasQuotas ? 114 : 62;
            int textX = e.TextRectangle.X + 2;
            int textW = Math.Max(60, e.Item.Width - textX - rightReserve);
            var emailRect = new Rectangle(textX, 0, textW, e.Item.Height);
            var emailColor = e.Item.Selected
                ? Color.FromArgb(67, 56, 202)
                : (acc.IsCurrent ? Color.FromArgb(21, 128, 61) : Color.FromArgb(30, 41, 59));

            TextRenderer.DrawText(e.Graphics, acc.Email, e.TextFont, emailRect, emailColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pillFont = new Font("Segoe UI", 7.75f, FontStyle.Bold);
            int pillY = (e.Item.Height - 18) / 2;
            int rightEdge = e.Item.Width - 10;

            if (hasQuotas)
            {
                DrawMiniPill(e.Graphics, new Rectangle(rightEdge - 50, pillY, 50, 18), $"C {acc.ClaudePct}",
                    Color.FromArgb(243, 232, 255), Color.FromArgb(109, 40, 217), pillFont);
                DrawMiniPill(e.Graphics, new Rectangle(rightEdge - 104, pillY, 50, 18), $"G {acc.GemPct}",
                    Color.FromArgb(224, 242, 254), Color.FromArgb(3, 105, 161), pillFont);
            }
            else
            {
                DrawMiniPill(e.Graphics, new Rectangle(rightEdge - 48, pillY, 48, 18), acc.TierTag,
                    Color.FromArgb(241, 245, 249), Color.FromArgb(71, 85, 105), pillFont);
            }
            return;
        }

        Color textColor;
        if (!e.Item.Enabled)
        {
            textColor = Color.FromArgb(148, 163, 184);
        }
        else if (e.Item.Tag?.ToString() == "danger" && e.Item.Selected)
        {
            textColor = Color.FromArgb(220, 38, 38);
        }
        else if (e.Item.Selected)
        {
            textColor = Color.FromArgb(67, 56, 202);
        }
        else
        {
            textColor = Color.FromArgb(30, 41, 59);
        }

        var rect = e.TextRectangle;
        rect.Offset(4, 0);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, textColor, e.TextFormat);
    }

    static void DrawMiniPill(Graphics g, Rectangle rect, string text, Color bgColor, Color fgColor, Font font)
    {
        using var path = CreateRoundedPath(rect, 4);
        using var bg = new SolidBrush(bgColor);
        g.FillPath(bg, path);
        TextRenderer.DrawText(g, text, font, rect, fgColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(226, 232, 240), 1);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    static GraphicsPath CreateRoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2f;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public static class TrayIcons
{
    public static Image CreateWindowIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(78, 136, 255), 1.5f);
        g.DrawRectangle(pen, 1.5f, 2.5f, 13, 11);
        g.DrawLine(pen, 1.5f, 5.5f, 14.5f, 5.5f);
        using var dotBrush = new SolidBrush(Color.FromArgb(78, 136, 255));
        g.FillEllipse(dotBrush, 3.5f, 3.5f, 1.2f, 1.2f);
        return bmp;
    }

    public static Image CreateChartIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b1 = new SolidBrush(Color.FromArgb(129, 140, 248));
        using var b2 = new SolidBrush(Color.FromArgb(79, 70, 229));
        g.FillRectangle(b1, 2.5f, 8.5f, 2.6f, 5f);
        g.FillRectangle(b2, 6.7f, 4.5f, 2.6f, 9f);
        g.FillRectangle(b1, 10.9f, 6.5f, 2.6f, 7f);
        return bmp;
    }

    public static Image CreateAccountDotIcon(bool isCurrent)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (isCurrent)
        {
            using var halo = new SolidBrush(Color.FromArgb(209, 250, 229));
            g.FillEllipse(halo, 2.5f, 2.5f, 11f, 11f);
            using var core = new SolidBrush(Color.FromArgb(16, 185, 129));
            g.FillEllipse(core, 5f, 5f, 6f, 6f);
        }
        else
        {
            using var pen = new Pen(Color.FromArgb(148, 163, 184), 1.4f);
            g.DrawEllipse(pen, 4.5f, 4.5f, 7f, 7f);
        }
        return bmp;
    }

    public static Image CreateLightningIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(78, 136, 255));
        PointF[] pts =
        {
            new(9.5f, 1f),
            new(4.5f, 8.5f),
            new(8.5f, 8.5f),
            new(6.5f, 15f),
            new(12.5f, 7f),
            new(8.5f, 7f)
        };
        g.FillPolygon(brush, pts);
        return bmp;
    }

    public static Image CreateRocketIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(16, 185, 129));
        PointF[] pts =
        {
            new(8f, 1.5f),
            new(12f, 6.5f),
            new(10.5f, 11.5f),
            new(5.5f, 11.5f),
            new(4f, 6.5f)
        };
        g.FillPolygon(brush, pts);
        using var fireBrush = new SolidBrush(Color.FromArgb(245, 158, 11));
        PointF[] fire =
        {
            new(6.5f, 12f),
            new(8f, 15f),
            new(9.5f, 12f)
        };
        g.FillPolygon(fireBrush, fire);
        return bmp;
    }

    public static Image CreateExitIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(239, 68, 68), 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawLine(pen, 3.5f, 3.5f, 12.5f, 12.5f);
        g.DrawLine(pen, 12.5f, 3.5f, 3.5f, 12.5f);
        return bmp;
    }

    public static Image CreateWidgetIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var rectPath = new GraphicsPath();
        float r = 4.5f;
        var rect = new RectangleF(1.5f, 3.5f, 13f, 9f);
        rectPath.AddArc(rect.X, rect.Y, r, r, 180, 90);
        rectPath.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        rectPath.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        rectPath.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        rectPath.CloseFigure();
        using var fill = new SolidBrush(Color.FromArgb(238, 242, 255));
        using var pen = new Pen(Color.FromArgb(79, 70, 229), 1.35f);
        g.FillPath(fill, rectPath);
        g.DrawPath(pen, rectPath);
        using var dot1 = new SolidBrush(Color.FromArgb(2, 132, 199));
        using var dot2 = new SolidBrush(Color.FromArgb(124, 58, 237));
        g.FillEllipse(dot1, 4f, 6.5f, 3f, 3f);
        g.FillEllipse(dot2, 9f, 6.5f, 3f, 3f);
        return bmp;
    }

    public static Image? LoadAppLogo()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/apiswitch.png");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo?.Stream != null)
            {
                using var orig = Image.FromStream(streamInfo.Stream);
                var bmp = new Bitmap(16, 16);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(orig, 0, 0, 16, 16);
                return bmp;
            }
        }
        catch { }

        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "apiswitch.png");
            if (File.Exists(path))
            {
                using var orig = Image.FromFile(path);
                var bmp = new Bitmap(16, 16);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(orig, 0, 0, 16, 16);
                return bmp;
            }
        }
        catch { }

        try
        {
            if (!string.IsNullOrEmpty(Environment.ProcessPath))
            {
                var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (icon != null) return new Icon(icon, 16, 16).ToBitmap();
            }
        }
        catch { }

        return null;
    }
}
