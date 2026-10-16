using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    void RenderCalendar(bool week)
    {
        var root = new DockPanel(); body.Content = root;
        var bar = DateBar(week); DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        var legend = Row(Text("● 课程", 11, Kind("课程")), new Border { Width = 20 }, Text("● 考试", 11, Kind("考试")), new Border { Width = 20 }, Text("● 个人活动", 11, Kind("活动")), new Border { Width = 20 }, Text("点击日程可编辑 · 双击空白处添加", 11, Muted));
        legend.Margin = new Thickness(0, 0, 0, 15); DockPanel.SetDock(legend, Dock.Top); root.Children.Add(legend);
        var start = week ? Engine.Monday(date) : date.Date; int count = week ? 7 : 1;
        var headers = new Grid { Margin = new Thickness(0, 0, 17, 0) }; headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        for (int day = 0; day < count; day++)
        {
            var d = start.AddDays(day); headers.ColumnDefinitions.Add(new ColumnDefinition());
            var text = Text(d.ToString("ddd  M/d", System.Globalization.CultureInfo.GetCultureInfo("zh-CN")) + (d == DateTime.Today ? " · 今天" : ""), 12, d == DateTime.Today ? Green : Muted, true); text.HorizontalAlignment = HorizontalAlignment.Center;
            var dayNotice = store.Data.Settings.ApplyAcademicHolidays ? AcademicCalendar.Day(d) : null;
            var header = Stack(text);
            if (dayNotice != null) { var badge = Text(dayNotice.Label, 10, Green, true); badge.HorizontalAlignment = HorizontalAlignment.Center; header.Children.Add(Space(4)); header.Children.Add(badge); }
            var cell = new Border { Background = dayNotice != null ? Brush("#EEF3E8") : d == DateTime.Today ? Brush("#EAF3ED") : Brushes.White, Padding = new Thickness(4, 10, 4, 10), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 1, 1), Child = header, ToolTip = dayNotice?.Detail }; Grid.SetColumn(cell, day + 1); headers.Children.Add(cell);
        }
        DockPanel.SetDock(headers, Dock.Top); root.Children.Add(headers);
        var grid = new Grid { Height = 1440, Background = Brushes.White }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
        var scale = new Canvas { Height = 1440, ClipToBounds = true };
        for (int h = 0; h < 24; h++) { var t = Text(h.ToString("00") + ":00", 10, Muted); Canvas.SetTop(t, h * 60 + 3); Canvas.SetLeft(t, 2); scale.Children.Add(t); } grid.Children.Add(scale);
        for (int day = 0; day < count; day++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition()); DateTime current = start.AddDays(day);
            var canvas = new Canvas { Height = 1440, ClipToBounds = true, Background = current == DateTime.Today ? Brush("#FBFDF9") : Brushes.White };
            Grid.SetColumn(canvas, day + 1); grid.Children.Add(canvas);
            var occurrences = On(current).ToList();
            void Paint()
            {
                canvas.Children.Clear(); double width = canvas.ActualWidth;
                if (width < 1) return;
                for (int hour = 0; hour < 24; hour++)
                {
                    var line = new Border { Height = 1, Width = width, Background = Line }; Canvas.SetTop(line, hour * 60); canvas.Children.Add(line);
                }
                var vertical = new Border { Height = 1440, Width = 1, Background = Line }; canvas.Children.Add(vertical);
                var groups = new List<List<Occurrence>>();
                foreach (var o in occurrences)
                {
                    if (groups.Count == 0 || groups[^1].Max(x => x.End) <= o.Start) groups.Add(new());
                    groups[^1].Add(o);
                }
                foreach (var group in groups)
                {
                    var occupied = new List<DateTime>(); var assignments = new List<(Occurrence Occ, int Lane)>();
                    foreach (var o in group)
                    {
                        int lane = occupied.FindIndex(t => t <= o.Start); if (lane < 0) { lane = occupied.Count; occupied.Add(o.End); } else occupied[lane] = o.End;
                        assignments.Add((o, lane));
                    }
                    foreach (var (o, lane) in assignments)
                    {
                        double top = Math.Max(0, (o.Start - current).TotalMinutes), bottom = Math.Min(1440, (o.End - current).TotalMinutes);
                        double height = bottom - top;
                        var title = Text(o.Item.Title, week ? 11 : 14, Kind(o.Item.Kind), true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
                        var place = Text(o.Item.Location.Length > 0 ? o.Item.Location : "地点待公布", 10, Muted); place.TextWrapping = TextWrapping.NoWrap; place.TextTrimming = TextTrimming.CharacterEllipsis;
                        var content = Stack(title, place);
                        if (height >= 58) content.Children.Add(Text($"{o.Start:HH:mm}–{o.End:HH:mm}" + (group.Count > 1 ? " · 重叠" : ""), 10, Kind(o.Item.Kind)));
                        if (height >= 78 && o.Item.SchoolAdjustment.Length > 0) content.Children.Add(Text(o.Item.SchoolAdjustment, 9, Green));
                        var button = Button("", () => Edit(o)); button.Content = content; button.Background = Tint(o.Item.Kind); button.BorderBrush = Kind(o.Item.Kind); button.BorderThickness = new Thickness(3, 0, 0, 0); button.Padding = new Thickness(8, 5, 5, 3); button.Margin = new Thickness(0);
                        button.HorizontalContentAlignment = HorizontalAlignment.Left; button.VerticalContentAlignment = VerticalAlignment.Top;
                        button.Width = Math.Max(8, (width - 6) / occupied.Count - 3); button.Height = Math.Max(24, bottom - top - 3); button.ClipToBounds = true;
                        button.ToolTip = $"{o.Item.Title}\n{o.Start:MM-dd HH:mm} — {o.End:MM-dd HH:mm}\n地点：{(o.Item.Location.Length > 0 ? o.Item.Location : "待公布")}\n{(o.Item.Teacher.Length > 0 ? "教师：" + o.Item.Teacher : "")}\n{o.Item.SchoolAdjustment}";
                        Canvas.SetLeft(button, 4 + lane * ((width - 6) / occupied.Count)); Canvas.SetTop(button, top + 1); canvas.Children.Add(button);
                    }
                }
                if (current == DateTime.Today)
                {
                    var now = new Border { Height = 2, Width = width, Background = Brush("#CB725B"), IsHitTestVisible = false }; Canvas.SetTop(now, DateTime.Now.TimeOfDay.TotalMinutes); canvas.Children.Add(now);
                }
            }
            canvas.SizeChanged += (_, _) => Paint();
            canvas.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && e.OriginalSource == canvas) { var minute = Math.Clamp((int)(e.GetPosition(canvas).Y / 15) * 15, 0, 1425); Edit(null, "活动", current.AddMinutes(minute)); } };
        }
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Visible }; root.Children.Add(scroll);
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(7 * 60);
    }
}
