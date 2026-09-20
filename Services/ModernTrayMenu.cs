using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace APISwitch.Services;

public static class ModernTrayMenu
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static ContextMenuStrip Create(Action onShow, Action onRefresh, Action onLaunchIde, Action onExit, string version = "v0.1.1")
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ModernTrayMenuRenderer(),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            DropShadowEnabled = true,
            Padding = new Padding(4, 6, 4, 6),
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
        var header = new ToolStripMenuItem($"APISwitch  {version}")
        {
            Tag = "header",
            Image = TrayIcons.LoadAppLogo(),
            ImageScaling = ToolStripItemImageScaling.None,
            Font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Bold),
            Padding = new Padding(8, 7, 16, 5)
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

        menu.Items.Add(new ToolStripSeparator());

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

        if (item.Tag?.ToString() == "header") return;

        if (item.Selected && item.Enabled)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(4, 2, item.Width - 8, item.Height - 4);
            using var path = CreateRoundedPath(rect, 5);

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
        if (e.Item.Tag?.ToString() == "header")
        {
            var headerColor = Color.FromArgb(71, 85, 105);
            var hRect = e.TextRectangle;
            hRect.Offset(4, 0);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, hRect, headerColor, e.TextFormat);
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
            textColor = Color.FromArgb(37, 99, 235);
        }
        else
        {
            textColor = Color.FromArgb(30, 41, 59);
        }

        var rect = e.TextRectangle;
        rect.Offset(4, 0);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, rect, textColor, e.TextFormat);
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
