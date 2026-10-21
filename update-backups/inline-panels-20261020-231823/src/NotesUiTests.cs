using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Shike;
public sealed partial class MainWindow
{
    void RunNoteUiTests(string dir, Action<bool, string> check, Action<Action, Action<Window>> modal, Action<Window, string> click)
    {
        modal(() => EditLibrary(null), window => { Visuals<TextBox>(window).Single().Text = "学习与探索"; click(window, "保存笔记库"); });
        var library = CurrentLibrary; check(library.Name == "学习与探索", "Create independent note library via UI");
        var course = new Item { Title = "微积分", Kind = "课程", Teacher = "测试教师", Scope = "test-term" };
        store.Data.Items.Add(course); var meeting = course.Copy(); meeting.Id = Guid.NewGuid().ToString("N"); meeting.Start = meeting.Start.AddDays(7); meeting.End = meeting.End.AddDays(7); store.Data.Items.Add(meeting);
        var b = new Item { Title = "极限与连续", Kind = "笔记", LibraryId = library.Id, Note = "用极限连接变化与连续。" }; store.Data.Items.Add(b);
        var task = new Item { Title = "完成本周习题", Kind = "待办" }; store.Data.Items.Add(task); store.Save();
        modal(() => EditKnowledgeNote(null), window =>
        {
            Visuals<TextBox>(window).First(t => t.MaxLength == 300).Text = "导数的几何意义";
            var rich = Visuals<RichTextBox>(window).Single(); rich.Document.Blocks.Clear(); rich.Document.Blocks.Add(new Paragraph(new Bold(new Run("从割线到切线"))) { FontSize = 24 }); rich.Document.Blocks.Add(new Paragraph(new Run("导数描述函数在某一点的局部变化率。将课堂推导与自己的例子连接起来。")));
            var bulletList = new System.Windows.Documents.List(); bulletList.ListItems.Add(new ListItem(new Paragraph(new Run("回顾极限的定义")))); bulletList.ListItems.Add(new ListItem(new Paragraph(new Run("整理课堂例题")))); rich.Document.Blocks.Add(bulletList);
            void Link(string button, string label)
            {
                Exception? error = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var picker = window.OwnedWindows.Cast<Window>().Single();
                    try {
                        picker.UpdateLayout(); var boxes = Visuals<CheckBox>(picker).Where(c => Visuals<TextBlock>(c).Any(t => t.Text == label)).ToList();
                        check(boxes.Count == 1, "Association picker deduplicates target: " + label);
                        boxes[0].IsChecked = true; boxes[0].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); click(picker, "确定关联");
                    } catch (Exception ex) { error = ex; picker.Close(); }
                }), DispatcherPriority.ApplicationIdle);
                click(window, button); if (error != null) throw error;
            }
            Link("关联笔记", b.Title); Link("关联课程", course.Title); Link("关联事项", task.Title);
            window.UpdateLayout(); Theme.Screenshot((FrameworkElement)window.Content, Path.Combine(dir, "直观笔记编辑.png")); click(window, "保存");
        });
        var a = store.Data.Items.Single(n => n.Title == "导数的几何意义"); check(a.NoteFormat == "rtf-v2" && a.NoteLinks.Count == 3 && a.Note.Contains("局部变化率"), "Rich note and three link categories saved");
        modal(() => EditKnowledgeNote(b), window => { check(Visuals<Button>(window).Any(btn => (btn.Content as string) == a.Title), "Backlink visible in linked note"); click(window, "取消"); });
        var more = new[] { "求导法则", "函数与图像", "微积分复习路线", "面试准备", "学习方法" }.Select(title => new Item { Kind = "笔记", Title = title, LibraryId = library.Id }).ToList();
        foreach (var n in more) { n.NoteLinks.Add(new() { Key = NotesModel.Key(a), Label = a.Title, Kind = "note" }); store.Data.Items.Add(n); }
        more[0].NoteLinks.Add(new() { Key = NotesModel.Key(b), Label = b.Title, Kind = "note" }); more[2].NoteLinks.Add(new() { Key = NotesModel.CourseKey(course), Label = course.Title, Kind = "course" }); store.Save();
        SwitchView("笔记与附件"); UpdateLayout(); Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        check(Visuals<System.Windows.Shapes.Ellipse>(body).Count() == NotesModel.Graph(store.Data, library).Nodes.Count, "Graph renders one circle per note or linked target");
        // Open the graph's target directly: synthetic mouse capture depends on desktop focus in headless runs.
        modal(() => OpenNoteTarget(NotesModel.Graph(store.Data, library).Nodes.Single(n => n.Key == NotesModel.Key(a))), window => { check(Visuals<TextBox>(window).Any(t => t.Text == a.Title), "Opening graph target selects correct note"); click(window, "取消"); });
        Theme.Screenshot((FrameworkElement)Content, Path.Combine(dir, "知识图谱.png"));
        void RightClick(string title)
        {
            UpdateLayout(); var node = Visuals<Border>(body).First(border => border.ToolTip is string text && text.StartsWith(title + "\n"));
            node.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent });
        }
        RightClick(b.Title); check(quickNoteKey == NotesModel.Key(b), "Right-click note enters quick association mode");
        RightClick(more[1].Title);
        check(b.NoteLinks.Any(l => l.Key == NotesModel.Key(more[1])) && more[1].NoteLinks.Any(l => l.Key == NotesModel.Key(b)), "Second right-click creates and persists mutual association");
        RightClick(more[1].Title); check(b.NoteLinks.Count(l => l.Key == NotesModel.Key(more[1])) == 1, "Repeated quick association creates no duplicate");
        RightClick(b.Title); check(quickNoteKey == null, "Right-click source exits quick association mode");
        RightClick(b.Title);
        RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        check(quickNoteKey == null, "Left-click anywhere exits quick association mode");
        modal(() => EditKnowledgeNote(more[1]), window =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var picker = window.OwnedWindows.Cast<Window>().Single(); picker.UpdateLayout();
                var box = Visuals<CheckBox>(picker).Single(c => Visuals<TextBlock>(c).Any(t => t.Text == b.Title));
                check(box.IsChecked == true, "Reverse endpoint shows existing association as selected");
                box.IsChecked = false; box.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); click(picker, "确定关联");
            }), DispatcherPriority.ApplicationIdle);
            click(window, "关联笔记"); click(window, "保存");
        });
        check(!b.NoteLinks.Any(l => l.Key == NotesModel.Key(more[1])) && !store.Data.Items.Single(i => i.Id == more[1].Id).NoteLinks.Any(l => l.Key == NotesModel.Key(b)), "Removing association in reverse editor removes both endpoints");
        UpdateLayout(); click(this, "保存布局"); check(new Store(store.Root).Data.NoteLibraries.Single(l => l.Id == library.Id).Positions.Count > 0, "Graph layout persists to local data");
        modal(() => EditLibrary(library), window => { Visuals<CheckBox>(window).First().IsChecked = false; click(window, "保存笔记库"); });
        check(NotesModel.Graph(store.Data, library).Nodes.All(n => n.Kind != "course") && a.NoteLinks.Any(l => l.Key == NotesModel.CourseKey(course)), "Disabling course category hides nodes without losing links");
        library.AllowCourses = true; store.Save();
        search.Text = "导数"; UpdateLayout(); check(Visuals<System.Windows.Shapes.Ellipse>(body).Any(), "Graph search retains matches and their neighbours"); search.Text = "";
        noteLibraryId = "default"; Render(); check(NotesModel.Graph(store.Data, CurrentLibrary).Nodes.All(n => n.Label != a.Title), "Switching libraries isolates graph contents");
    }
}


