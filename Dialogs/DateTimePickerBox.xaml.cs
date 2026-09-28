using System;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace APISwitch.Dialogs;

/// <summary>
/// 日期时间选择组件：字段按钮 + 弹出日历与时/分选择。
/// 选中日期后自动收起；时间调整即时生效；"此刻"一键取当前时间。
/// </summary>
public partial class DateTimePickerBox : UserControl
{
    bool _updating;

    /// <summary>值变化（用户交互或非静默 SetValue）时触发。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>当前值（日期 + 时分，秒为 0）。</summary>
    public DateTime Value { get; private set; } = DateTime.Today;

    public DateTimePickerBox()
    {
        InitializeComponent();

        for (int h = 0; h < 24; h++) HourBox.Items.Add(h);
        for (int m = 0; m < 60; m++) MinuteBox.Items.Add(m);

        Toggle.Checked += (_, _) =>
        {
            SyncCalendarToValue();
            Pop.IsOpen = true;
        };
        Pop.Closed += (_, _) => Toggle.IsChecked = false;

        // 日历选日期：更新日期部分并收起
        Cal.SelectedDatesChanged += (_, _) =>
        {
            if (_updating) return;
            if (Cal.SelectedDate is { } d)
                Apply(d.Date + Value.TimeOfDay, close: true);
        };

        HourBox.SelectionChanged += (_, _) =>
        {
            if (_updating || HourBox.SelectedItem is not int h) return;
            Apply(Value.Date + TimeSpan.FromHours(h));
        };
        MinuteBox.SelectionChanged += (_, _) =>
        {
            if (_updating || MinuteBox.SelectedItem is not int m) return;
            Apply(Value.Date + TimeSpan.FromMinutes(m));
        };

        NowBtn.Click += (_, _) => Apply(DateTime.Now, close: true);

        SetValue(DateTime.Today, silent: true);
    }

    /// <summary>编程赋值；silent 为 true 时不触发 ValueChanged（用于初始化/快速区间）。</summary>
    public void SetValue(DateTime v, bool silent = false)
    {
        _updating = true;
        Value = v;
        UpdateText();
        HourBox.SelectedItem = v.Hour;
        MinuteBox.SelectedItem = v.Minute;
        SyncCalendarToValue();
        _updating = false;
        if (!silent) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void Apply(DateTime v, bool close = false)
    {
        Value = v;
        UpdateText();
        if (close) ClosePopup();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void UpdateText() => FieldText.Text = Value.ToString("yyyy-MM-dd HH:mm");

    void SyncCalendarToValue()
    {
        _updating = true;
        Cal.DisplayDate = Value.Date;
        Cal.SelectedDate = Value.Date;
        _updating = false;
    }

    void ClosePopup()
    {
        Pop.IsOpen = false;
        Toggle.IsChecked = false;
    }
}
