using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Shike.Theme;

namespace Shike;

// Each column has a fixed selection band and wraps independently, like a clock picker.
public sealed class TimeWheel : Border
{
    readonly WheelColumn hours, minutes;
    public TimeSpan Value => new(hours.Value, minutes.Value, 0);
    public TimeWheel(DateTime value)
    {
        hours = new WheelColumn(24, value.Hour, "小时"); minutes = new WheelColumn(60, value.Minute, "分钟");
        Background = Brushes.White; BorderBrush = Line; BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12); Padding = new Thickness(8, 2, 8, 2);
        Margin = new Thickness(18, 0, 0, 0);
        var colon = Text(":", 24, Green, true); colon.Margin = new Thickness(7, 0, 7, 0);
        Child = Row(hours, colon, minutes);
    }
    public void SetTime(int hour, int minute) { hours.Set(hour); minutes.Set(minute); }
    public void StepHours(int delta) => hours.Step(delta);
    public void StepMinutes(int delta) => minutes.Step(delta);
    sealed class WheelColumn : Border
    {
        readonly int count; readonly string unit; readonly TextBlock previous, selected, next;
        public int Value { get; private set; }
        Point? drag; double remainder;
        public WheelColumn(int count, int value, string unit)
        {
            this.count = count; this.unit = unit; Width = 65; Focusable = true; Cursor = Cursors.Hand;
            ToolTip = "滚动鼠标滚轮或上下拖动；也可使用 ↑ ↓ / Home / End";
            previous = Text("", 14, Muted); selected = Text("", 24, Green, true); next = Text("", 14, Muted);
            foreach (var text in new[] { previous, selected, next }) text.HorizontalAlignment = HorizontalAlignment.Center;
            Child = Stack(new Border { Height = 29, Child = previous }, new Border { Height = 38, CornerRadius = new CornerRadius(8), Background = Brush("#EAF3ED"), Child = selected }, new Border { Height = 29, Child = next });
            Set(value);
            PreviewMouseWheel += (_, e) => { Step(e.Delta > 0 ? -1 : 1); e.Handled = true; };
            KeyDown += (_, e) => { if (e.Key == Key.Up) Step(-1); else if (e.Key == Key.Down) Step(1); else if (e.Key == Key.Home) Set(0); else if (e.Key == Key.End) Set(count - 1); else return; e.Handled = true; };
            MouseLeftButtonDown += (_, e) => { Focus(); drag = e.GetPosition(this); remainder = 0; CaptureMouse(); e.Handled = true; };
            MouseMove += (_, e) => { if (drag is not Point last) return; var now = e.GetPosition(this); remainder += last.Y - now.Y; drag = now; int steps = (int)(remainder / 22); if (steps != 0) { Step(steps); remainder -= steps * 22; } };
            MouseLeftButtonUp += (_, e) => { drag = null; ReleaseMouseCapture(); e.Handled = true; };
            LostMouseCapture += (_, _) => drag = null;
            GotKeyboardFocus += (_, _) => { BorderBrush = Green; BorderThickness = new Thickness(1); };
            LostKeyboardFocus += (_, _) => BorderThickness = new Thickness(0);
        }
        public void Step(int delta) => Set(Value + delta);
        public void Set(int value)
        {
            Value = ((value % count) + count) % count;
            previous.Text = ((Value + count - 1) % count).ToString("00"); selected.Text = Value.ToString("00"); next.Text = ((Value + 1) % count).ToString("00");
            System.Windows.Automation.AutomationProperties.SetName(this, unit + " " + selected.Text);
        }
    }
}
