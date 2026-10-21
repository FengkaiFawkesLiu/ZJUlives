using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Shike;

// An application editor is a control in the workspace, never a second native window.
// The local dispatcher frame preserves existing confirmation/save call ordering.
public class AppPanel : ContentControl
{
    public FrameworkElement? Owner { get; set; }
    public string Title { get; set; } = "";
    public double PreferredWidth { get; set; } = 600;
    public double MinimumContentWidth { get; set; } = 430;
    public bool FocusWorkspace { get; set; }
    public bool IsClosed { get; private set; }
    internal bool IsClosing { get; private set; }
    internal ColumnWorkspace? Host { get; private set; }
    DispatcherFrame? frame;
    bool? result;
    public event CancelEventHandler? Closing;
    public event EventHandler? Closed;
    public IEnumerable<AppPanel> OwnedPanels => Host?.Panels.Where(p => p.Owner == this) ?? Enumerable.Empty<AppPanel>();
    public bool? DialogResult
    {
        get => result;
        set { var previous = result; result = value; Close(); if (!IsClosed) result = previous; }
    }
    public AppPanel()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Foreground = Theme.Ink;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is StackPanel || newContent is Border { Child: StackPanel })
        {
            Content = null;
            Content = new ScrollViewer { Content = (UIElement)newContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }
    public bool? ShowDialog()
    {
        if (IsClosed || Host != null) throw new InvalidOperationException("此内容列已打开或关闭。");
        var main = Application.Current.MainWindow as MainWindow
            ?? throw new InvalidOperationException("请先打开拾课主界面。");
        Host = main.Workspace;
        // Nested tools belong to the current editor, even when legacy callers pass the main window.
        if (Host.Active != null && Owner is not AppPanel) Owner = Host.Active;
        frame = new DispatcherFrame();
        Host.Add(this);
        if (!IsClosed) Dispatcher.PushFrame(frame);
        return result;
    }
    public void Close()
    {
        if (IsClosed || IsClosing) return;
        if (Host != null && !Host.CloseAbove(this)) return;
        var args = new CancelEventArgs(); Closing?.Invoke(this, args);
        if (args.Cancel) return;
        IsClosing = true;
        Host?.Remove(this);
        IsClosed = true;
        Closed?.Invoke(this, EventArgs.Empty);
        if (frame != null) frame.Continue = false;
    }
}

// Bounded sizing preserves the entire logical layout. Only dense legacy pages
// scale down; the sidebar and the outer application window never move or resize.
public sealed class AdaptivePane : Decorator
{
    public double MinimumContentWidth { get; set; } = 430;
    double scale = 1;
    public AdaptivePane() { ClipToBounds = true; }
    protected override Size MeasureOverride(Size available)
    {
        if (Child == null) return new Size();
        double width = double.IsInfinity(available.Width) ? MinimumContentWidth : Math.Max(1, available.Width);
        // A newly opening column starts at zero width. Do not divide its height
        // by a near-zero scale during the animation.
        scale = Math.Clamp(width / MinimumContentWidth, .3, 1);
        Child.Measure(new Size(Math.Max(MinimumContentWidth, width / scale), double.IsInfinity(available.Height) ? 700 : available.Height / scale));
        return new Size(width, double.IsInfinity(available.Height) ? Child.DesiredSize.Height * scale : available.Height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        if (Child != null)
        {
            scale = Math.Clamp(Math.Max(1, final.Width) / MinimumContentWidth, .3, 1);
            Child.RenderTransform = new ScaleTransform(scale, scale);
            Child.Arrange(new Rect(0, 0, Math.Max(MinimumContentWidth, final.Width / scale), final.Height / scale));
        }
        return final;
    }
}

public sealed class StarAnimation : AnimationTimeline
{
    public double From { get; set; }
    public double To { get; set; }
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new StarAnimation { From = From, To = To };
    public override object GetCurrentValue(object origin, object destination, AnimationClock clock)
    {
        double t = clock.CurrentProgress ?? 0;
        t = 1 - Math.Pow(1 - t, 3);
        return new GridLength(Math.Max(0, From + (To - From) * t), GridUnitType.Star);
    }
}

public sealed class ColumnWorkspace : Grid
{
    readonly List<AppPanel> panels = new();
    readonly List<FrameworkElement> cells = new();
    readonly Dictionary<AppPanel, IInputElement?> focus = new();
    public IReadOnlyList<AppPanel> Panels => panels;
    public AppPanel? Active => panels.LastOrDefault();
    public bool CloseAll()
    {
        while (Active is AppPanel panel) { panel.Close(); if (!panel.IsClosed) return false; }
        return true;
    }
    public bool CloseAbove(AppPanel panel)
    {
        if (!panels.Contains(panel)) return true;
        while (Active != panel) { var top = Active!; top.Close(); if (!top.IsClosed) return false; }
        return true;
    }
    public ColumnWorkspace(FrameworkElement main)
    {
        ClipToBounds = true;
        var cell = new AdaptivePane { Child = main, MinimumContentWidth = 700 };
        cells.Add(cell); ColumnDefinitions.Add(new()); Children.Add(cell);
        GuardParentInput(cell);
        SizeChanged += (_, _) => Reflow(false);
    }
    void GuardParentInput(FrameworkElement cell)
    {
        cell.PreviewMouseDown += (_, e) =>
        {
            if (cells.LastOrDefault() == cell) return;
            DependencyObject? node=e.OriginalSource as DependencyObject;
            while(node != null && node != cell)
            {
                if(node is ScrollBar)return;
                node=node is Visual ? VisualTreeHelper.GetParent(node) : null;
            }
            e.Handled=true;
        };
    }
    public void Add(AppPanel panel)
    {
        focus[panel] = Keyboard.FocusedElement;
        var dock = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(14, 10, 14, 4) };
        var close = Theme.Button("‹ 返回", panel.Close);
        close.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(close, Dock.Left); top.Children.Add(close);
        top.Children.Add(Theme.Text(panel.Title, 12, Theme.Muted));
        DockPanel.SetDock(top, Dock.Top); if(!panel.FocusWorkspace) dock.Children.Add(top);
        dock.Children.Add(panel);
        var cell = new Border { Background = GlassTheme.Surface, BorderBrush = GlassTheme.Edge,
            BorderThickness = new Thickness(1, 0, 0, 0), Child = new AdaptivePane { Child = dock, MinimumContentWidth = panel.MinimumContentWidth } };
        panels.Add(panel); cells.Add(cell); Children.Add(cell);
        GuardParentInput(cell);
        Reflow(true);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (panel.IsLoaded && Active == panel)
                panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }), DispatcherPriority.Input);
    }
    public void Remove(AppPanel panel)
    {
        int i = panels.IndexOf(panel); if (i < 0) return;
        int cellIndex = i + 1;
        bool singleRow = RowDefinitions.Count <= 1 && ColumnDefinitions.Count == cells.Count;
        bool lastRow = RowDefinitions.Count > 1 && cellIndex == (RowDefinitions.Count - 1) * ColumnDefinitions.Count;
        if (Motion.Enabled && panels.Any(p=>p.FocusWorkspace))
        {
            var done=new DispatcherFrame();var shift=new TranslateTransform();cells[cellIndex].RenderTransform=shift;
            shift.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,24,TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseIn } });
            cells[cellIndex].BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(Motion.DurationMs)));
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(Motion.DurationMs) };
            timer.Tick+=(_,_)=>{timer.Stop();done.Continue=false;};timer.Start();Dispatcher.PushFrame(done);
        }
        if (Motion.Enabled && !panels.Any(p => p.FocusWorkspace) && (singleRow || lastRow))
        {
            var done = new DispatcherFrame();
            if (singleRow) for (int c = 0; c < ColumnDefinitions.Count; c++)
            {
                var column = ColumnDefinitions[c];
                double from = column.Width.Value;
                column.BeginAnimation(ColumnDefinition.WidthProperty, new StarAnimation
                {
                    From = from, To = c == cellIndex ? 0 : 1,
                    Duration = TimeSpan.FromMilliseconds(Motion.DurationMs)
                });
            }
            if (lastRow)
            {
                var row = RowDefinitions[^1];
                row.BeginAnimation(RowDefinition.HeightProperty, new StarAnimation { From = row.Height.Value, To = 0, Duration = TimeSpan.FromMilliseconds(Motion.DurationMs) });
            }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.DurationMs) };
            timer.Tick += (_, _) => { timer.Stop(); done.Continue = false; }; timer.Start();
            Dispatcher.PushFrame(done);
        }
        Children.Remove(cells[cellIndex]); cells.RemoveAt(cellIndex); panels.RemoveAt(i);
        Reflow(false);
        if (focus.Remove(panel, out var previous) && previous is UIElement element && element.IsVisible && element.IsEnabled) Keyboard.Focus(previous);
    }
    void Reflow(bool animate)
    {
        int count = cells.Count;
        int focused = panels.FindLastIndex(p => p.FocusWorkspace);
        if (focused >= 0)
        {
            foreach (var column in ColumnDefinitions) column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            foreach (var row in RowDefinitions) row.BeginAnimation(RowDefinition.HeightProperty, null);
            ColumnDefinitions.Clear(); RowDefinitions.Clear(); ColumnDefinitions.Add(new()); RowDefinitions.Add(new());
            int primary = focused + 1;
            for (int n = 0; n < count; n++)
            {
                var cell = cells[n]; bool shown = n == primary || n == count - 1;
                cell.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                SetRow(cell, 0); SetColumn(cell, 0); SetColumnSpan(cell, 1); SetZIndex(cell, n);
                cell.HorizontalAlignment = n > primary ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
                cell.Width = n > primary ? Math.Min(Math.Max(430, ActualWidth * .6), Math.Max(1, ActualWidth)) : double.NaN;
                cell.IsHitTestVisible = n == count - 1;
                KeyboardNavigation.SetTabNavigation(cell, n == count - 1 ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
            }
            if (animate && Motion.Enabled)
            {
                var cell = cells[^1]; var shift = new TranslateTransform(20, 0); cell.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.DurationMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            return;
        }
        foreach (var cell in cells) { cell.Visibility = Visibility.Visible; cell.Width = double.NaN; cell.HorizontalAlignment = HorizontalAlignment.Stretch; cell.RenderTransform = Transform.Identity; SetZIndex(cell,0); }
        // At the supported minimum window width, a third column moves below,
        // preserving every earlier view within the unchanged workspace bounds.
        double width = ActualWidth > 0 ? ActualWidth : 1100;
        int columns = Math.Min(count, width >= 840 ? 3 : width >= 560 ? 2 : 1);
        int rows = (int)Math.Ceiling((double)count / columns);
        var previous = ColumnDefinitions.Select(c => c.Width.Value).ToArray();
        var previousRows = RowDefinitions.Select(r => r.Height.Value).ToArray();
        foreach (var col in ColumnDefinitions) col.BeginAnimation(ColumnDefinition.WidthProperty, null);
        foreach (var row in RowDefinitions) row.BeginAnimation(RowDefinition.HeightProperty, null);
        ColumnDefinitions.Clear(); RowDefinitions.Clear();
        for (int r = 0; r < rows; r++)
        {
            var row = new RowDefinition(); RowDefinitions.Add(row);
            if (animate && Motion.Enabled && rows > 1)
                row.BeginAnimation(RowDefinition.HeightProperty, new StarAnimation { From = r < previousRows.Length ? previousRows[r] : 0, To = 1, Duration = TimeSpan.FromMilliseconds(Motion.DurationMs) });
        }
        for (int c = 0; c < columns; c++)
        {
            var column = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }; ColumnDefinitions.Add(column);
            if (animate && Motion.Enabled && rows == 1)
                column.BeginAnimation(ColumnDefinition.WidthProperty, new StarAnimation {
                    From = c < previous.Length ? previous[c] : 0, To = 1, Duration = TimeSpan.FromMilliseconds(Motion.DurationMs) });
        }
        for (int n = 0; n < count; n++)
        {
            Grid.SetRow(cells[n], n / columns); Grid.SetColumn(cells[n], n % columns);
            Grid.SetColumnSpan(cells[n], n == count - 1 && rows > 1 ? columns - n % columns : 1);
            // Parent content stays visible with normal contrast, but cannot be
            // edited underneath an active form.
            // Keep scrolling available in earlier columns while guarding their
            // edit buttons. The active form owns keyboard focus.
            cells[n].IsHitTestVisible = true;
            KeyboardNavigation.SetTabNavigation(cells[n], n == count - 1 ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        }
    }
}

// Existing call sites retain their result semantics; confirmations render as a
// new workspace column, never as a MessageBox over the app.
public static class UiMessage
{
    public static MessageBoxResult Show(string text, string caption = "拾课", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(Application.Current?.MainWindow, text, caption, buttons, image, defaultResult);
    public static MessageBoxResult Show(FrameworkElement? owner, string text, string caption = "拾课", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        // Startup failures have no main workspace. This is the sole native fallback.
        if (Application.Current?.MainWindow is not MainWindow main || !main.IsLoaded)
            return System.Windows.MessageBox.Show(text, caption, buttons, image, defaultResult);
        var panel = Theme.Dialog(owner ?? main, caption);
        var result = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? MessageBoxResult.No : MessageBoxResult.Cancel;
        var body = Theme.Stack(Theme.Text(caption, 21, bold: true), Theme.Space(16), Theme.Text(text), Theme.Space(24));
        var actions = new WrapPanel();
        var choices = buttons switch {
            MessageBoxButton.YesNo => new[] { ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            MessageBoxButton.YesNoCancel => new[] { ("取消", MessageBoxResult.Cancel), ("否", MessageBoxResult.No), ("是", MessageBoxResult.Yes) },
            MessageBoxButton.OKCancel => new[] { ("取消", MessageBoxResult.Cancel), ("确定", MessageBoxResult.OK) },
            _ => new[] { ("确定", MessageBoxResult.OK) }
        };
        foreach (var (label, value) in choices)
        {
            var button = Theme.Button(label, () => { result = value; panel.Close(); });
            button.IsDefault = value == defaultResult || defaultResult == MessageBoxResult.None && value == choices[0].Item2;
            actions.Children.Add(button);
        }
        body.Children.Add(actions);
        panel.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = body } };
        panel.ShowDialog(); return result;
    }
}
