using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using APISwitch.Services;

namespace APISwitch.Dialogs;

/// <summary>
/// 日期时间选择组件：字段按钮 + 自绘弹出面板（月份导航 / 日期网格 / 表盘式时分选择）。
/// 选日期只更新值、不关闭面板；表盘点击即按角度选中（24 小时 / 60 分钟全量可选）；
/// 点「完成」或面板外区域收起。
/// </summary>
public partial class DateTimePickerBox : UserControl
{
    enum DialMode { Hour, Minute }

    DateTime _viewMonth;                 // 面板当前显示的月份（该月 1 日）
    DateTime _picked = DateTime.Today;   // 面板内暂存的日期时间
    DialMode _dialMode = DialMode.Hour;

    readonly List<Button> _dayButtons = new();

    static readonly Brush DayNormal = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
    static readonly Brush DayMuted = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    static readonly Brush DayToday = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
    static readonly Brush DayOnAccent = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    static readonly Brush AccentFill = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
    static readonly Brush TodayRing = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xFE));
    static readonly Brush DialTrack = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    static readonly Brush DialInnerTrack = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));
    static readonly Brush DialTick = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    static readonly Brush DialLabel = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    /// <summary>值变化（用户交互或非静默 SetValue）时触发。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>当前值（日期 + 时分，秒为 0）。</summary>
    public DateTime Value { get; private set; } = DateTime.Today;

    public DateTimePickerBox()
    {
        InitializeComponent();

        Toggle.Checked += (_, _) =>
        {
            _picked = Value;
            _viewMonth = new DateTime(Value.Year, Value.Month, 1);
            _dialMode = DialMode.Hour;
            RenderCalendar();
            RenderTime();
            Pop.IsOpen = true;
        };
        Toggle.Unchecked += (_, _) => Pop.IsOpen = false;
        Pop.Closed += (_, _) => Toggle.IsChecked = false;

        PrevMonthBtn.Click += (_, _) => { _viewMonth = _viewMonth.AddMonths(-1); RenderCalendar(); };
        NextMonthBtn.Click += (_, _) => { _viewMonth = _viewMonth.AddMonths(1); RenderCalendar(); };

        // 时 / 分 箭头步进（±1 时 / ±5 分）
        HourDownBtn.Click += (_, _) => StepTime(-1, 0);
        HourUpBtn.Click += (_, _) => StepTime(1, 0);
        MinuteDownBtn.Click += (_, _) => StepTime(0, -5);
        MinuteUpBtn.Click += (_, _) => StepTime(0, 5);

        // 点框内文本 → 展开对应的表盘
        HourText.MouseLeftButtonUp += (_, _) => { _dialMode = DialMode.Hour; RenderTime(); };
        HourText.MouseLeftButtonUp += (_, _) => SetDialMode(DialMode.Hour);
        MinuteText.MouseLeftButtonUp += (_, _) => SetDialMode(DialMode.Minute);

        // 表盘：支持点击，也支持按住拖动实时改值
        Dial.MouseLeftButtonDown += OnDialDown;
        Dial.MouseMove += OnDialMove;
        Dial.MouseLeftButtonUp += OnDialUp;

        NowBtn.Click += (_, _) =>
        {
            _picked = DateTime.Now;
            _viewMonth = new DateTime(_picked.Year, _picked.Month, 1);
            RenderCalendar();
            RenderTime();
            Commit();
            ClosePopup();
        };
        DoneBtn.Click += (_, _) => ClosePopup();

        // 收起完全交给 WPF 内置的 StaysOpen=False（仅"弹层边界之外"的点击才关闭），
        // 不再自行判定外部点击 —— 那类判断会把面板内手势误判为外部，导致面板立即关闭。

        BuildDayGrid();
        SetValue(DateTime.Today, silent: true);
    }

    // ---------- 日期网格（按钮只建一次，之后仅原地更新状态） ----------

    void BuildDayGrid()
    {
        DayGrid.Children.Clear();
        _dayButtons.Clear();
        for (int i = 0; i < 42; i++)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("DayCell"),
                Content = "",
                Tag = null,
            };
            btn.Click += OnDayClick;
            _dayButtons.Add(btn);
            DayGrid.Children.Add(btn);
        }
    }

    // ---------- 渲染 ----------

    void RenderCalendar()
    {
        MonthTitle.Text = I18nService.ResolvedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"{_viewMonth.Year}年{_viewMonth.Month}月"
            : _viewMonth.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

        var first = _viewMonth;
        // 周一为一周起点：把周一映射到 0
        int offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var today = DateTime.Today;

        // 原地更新已建好的 42 个按钮（不重建可视化树，避免点击时抖动/失效）
        for (int i = 0; i < _dayButtons.Count; i++)
        {
            var day = start.AddDays(i);
            var btn = _dayButtons[i];

            bool inMonth = day.Month == _viewMonth.Month && day.Year == _viewMonth.Year;
            bool isPicked = day == _picked.Date;
            bool isToday = day == today;

            btn.Content = day.Day.ToString();
            btn.Tag = day;
            btn.BorderThickness = default;
            btn.FontWeight = isPicked || isToday ? FontWeights.SemiBold : FontWeights.Normal;

            if (isPicked)
            {
                btn.Background = AccentFill;
                btn.Foreground = DayOnAccent;
            }
            else if (isToday)
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = DayToday;
                btn.BorderBrush = TodayRing;
                btn.BorderThickness = new Thickness(1);
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = inMonth ? DayNormal : DayMuted;
            }
        }
    }

    void RenderTime()
    {
        string hh = _picked.Hour.ToString("00");
        string mm = _picked.Minute.ToString("00");

        HourText.Text = hh;
        MinuteText.Text = mm;

        // 当前展开的表盘对应的输入框高亮，另一个保持朴素
        bool hourMode = _dialMode == DialMode.Hour;
        HourBox.Background = hourMode ? new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF)) : new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        HourBox.BorderBrush = hourMode ? AccentFill : DialTrack;
        HourText.Foreground = hourMode ? AccentFill : new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
        MinuteBox.Background = hourMode ? new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)) : new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));
        MinuteBox.BorderBrush = hourMode ? DialTrack : AccentFill;
        MinuteText.Foreground = hourMode ? new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)) : AccentFill;

        RenderDial();
    }

    // ---------- 表盘 ----------

    const double DialSize = 204;
    const double DialCenter = DialSize / 2;
    const double DialRadiusOuter = 84;   // 小时外环 / 分钟环
    const double DialRadiusInner = 56;   // 小时内环（12~23）

    /// <summary>极坐标 → 直角坐标。angleDeg 以 12 点为 0°，顺时针为正。</summary>
    static (double x, double y) Polar(double r, double angleDeg)
    {
        double rad = (angleDeg - 90) * Math.PI / 180.0;
        return (DialCenter + r * Math.Cos(rad), DialCenter + r * Math.Sin(rad));
    }

    void RenderDial()
    {
        Dial.Children.Clear();

        // 外圈轨道
        Dial.Children.Add(new Ellipse
        {
            Width = DialRadiusOuter * 2,
            Height = DialRadiusOuter * 2,
            Stroke = DialTrack,
            StrokeThickness = 1,
        });
        Canvas.SetLeft(Dial.Children[^1], DialCenter - DialRadiusOuter);
        Canvas.SetTop(Dial.Children[^1], DialCenter - DialRadiusOuter);

        if (_dialMode == DialMode.Hour)
        {
            // 内圈轨道（12~23）
            Dial.Children.Add(new Ellipse
            {
                Width = DialRadiusInner * 2,
                Height = DialRadiusInner * 2,
                Stroke = DialInnerTrack,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
            Canvas.SetLeft(Dial.Children[^1], DialCenter - DialRadiusInner);
            Canvas.SetTop(Dial.Children[^1], DialCenter - DialRadiusInner);

            // 12 个扇区：外环 0~11，内环 12~23 —— 24 小时全部可直接点选
            for (int i = 0; i < 12; i++)
            {
                double angle = i * 30.0;
                AddDialNumber(i, Polar(DialRadiusOuter, angle), i == _picked.Hour, 12);
                AddDialNumber(i + 12, Polar(DialRadiusInner, angle), i + 12 == _picked.Hour, 11);
            }
        }
        else
        {
            // 分钟：60 个刻度，每 5 分钟显示数字，其余为小点 —— 60 分钟全部可点选
            for (int m = 0; m < 60; m++)
            {
                double angle = m * 6.0;
                bool selected = m == _picked.Minute;

                if (m % 5 == 0)
                {
                    AddDialNumber(m, Polar(DialRadiusOuter, angle), selected, 11);
                }
                else
                {
                    var (dx, dy) = Polar(DialRadiusOuter, angle);
                    var dot = new Ellipse
                    {
                        Width = 3,
                        Height = 3,
                        Fill = selected ? AccentFill : DialTick,
                    };
                    Canvas.SetLeft(dot, dx - 1.5);
                    Canvas.SetTop(dot, dy - 1.5);
                    Dial.Children.Add(dot);
                }
            }
        }

        // 指针：中心 → 选中位置
        double handR = _dialMode == DialMode.Hour && _picked.Hour >= 12 ? DialRadiusInner : DialRadiusOuter;
        double handAngle = _dialMode == DialMode.Hour
            ? (_picked.Hour % 12) * 30.0
            : _picked.Minute * 6.0;
        var (hx, hy) = Polar(handR, handAngle);

        Dial.Children.Add(new Line
        {
            X1 = DialCenter,
            Y1 = DialCenter,
            X2 = hx,
            Y2 = hy,
            Stroke = AccentFill,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        // 指针末端圆点
        Dial.Children.Add(new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = AccentFill,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
        });
        Canvas.SetLeft(Dial.Children[^1], hx - 7);
        Canvas.SetTop(Dial.Children[^1], hy - 7);

        // 中心点
        Dial.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = AccentFill,
        });
        Canvas.SetLeft(Dial.Children[^1], DialCenter - 3);
        Canvas.SetTop(Dial.Children[^1], DialCenter - 3);
    }

    void AddDialNumber(int value, (double x, double y) pos, bool selected, double fontSize)
    {
        var tb = new TextBlock
        {
            Text = value.ToString("00"),
            FontSize = fontSize,
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
            Foreground = selected ? AccentFill : DialLabel,
            Width = 26,
            Height = 20,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(tb, pos.x - 13);
        Canvas.SetTop(tb, pos.y - 10);
        Dial.Children.Add(tb);
    }

    bool _dialDragging;

    bool ApplyFromPoint(System.Windows.Point p)
    {
        double dx = p.X - DialCenter;
        double dy = p.Y - DialCenter;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 22) return false;

        double clock = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 90 + 360) % 360;

        if (_dialMode == DialMode.Hour)
        {
            int sector = (int)Math.Round(clock / 30.0) % 12;
            bool inner = dist < (DialRadiusOuter + DialRadiusInner) / 2;
            int hour = inner ? sector + 12 : sector;
            _picked = _picked.Date.AddHours(hour);
        }
        else
        {
            int minute = (int)Math.Round(clock / 6.0) % 60;
            _picked = _picked.Date.AddMinutes(minute);
        }

        RenderTime();
        Commit();
        return true;
    }

    void OnDialDown(object sender, MouseButtonEventArgs e)
    {
        _dialDragging = true;
        Dial.CaptureMouse();
        ApplyFromPoint(e.GetPosition(Dial));
        e.Handled = true;
    }

    void OnDialMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dialDragging) return;
        if (e.LeftButton == MouseButtonState.Pressed)
            ApplyFromPoint(e.GetPosition(Dial));
    }

    void OnDialUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dialDragging) return;
        _dialDragging = false;
        Dial.ReleaseMouseCapture();
        ApplyFromPoint(e.GetPosition(Dial));
    }

    // ---------- 交互 ----------

    void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTime day }) return;
        _picked = day.Date + _picked.TimeOfDay;
        _viewMonth = new DateTime(day.Year, day.Month, 1);
        RenderCalendar();
        Commit();   // 只更新值，不收起面板，便于接着调时间
    }

    void Commit()
    {
        Value = _picked;
        FieldText.Text = Value.ToString("yyyy-MM-dd HH:mm");
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void SetDialMode(DialMode mode)
    {
        if (_dialMode == mode) return;
        _dialMode = mode;
        RenderTime();
    }

    void StepTime(int dHour, int dMinute)
    {
        var t = _picked.AddHours(dHour).AddMinutes(dMinute);
        _picked = t.Date + t.TimeOfDay;
        RenderTime();
        Commit();
    }
    void ClosePopup()
    {
        Pop.IsOpen = false;
        Toggle.IsChecked = false;
    }

    // ---------- 对外 API ----------

    /// <summary>编程赋值；silent 为 true 时不触发 ValueChanged（用于初始化/快速区间）。</summary>
    public void SetValue(DateTime v, bool silent = false)
    {
        _picked = v;
        Value = v;
        FieldText.Text = v.ToString("yyyy-MM-dd HH:mm");
        HourText.Text = v.Hour.ToString("00");
        MinuteText.Text = v.Minute.ToString("00");
        if (!silent) ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
