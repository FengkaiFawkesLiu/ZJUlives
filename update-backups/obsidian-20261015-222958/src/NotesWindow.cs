using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    string noteLibraryId = "default";
    bool noteGraphView = true;
    NoteLibrary CurrentLibrary => store.Data.NoteLibraries.FirstOrDefault(l => l.Id == noteLibraryId) ?? store.Data.NoteLibraries[0];
    void RenderNotes()
    {
        var library = CurrentLibrary; noteLibraryId = library.Id; heading.Text = "知识笔记"; subtitle.Text = "把课堂、想法与生活连接起来 · 搜索当前库的笔记和关联内容";
        var dock = new DockPanel(); body.Content = dock;
        var libraries = Combo(store.Data.NoteLibraries.Select(l => l.Name).ToArray(), library.Name); libraries.MinWidth = 145;
        libraries.SelectedIndex = store.Data.NoteLibraries.IndexOf(library);
        libraries.SelectionChanged += (_, _) => { if (libraries.SelectedIndex >= 0) { noteLibraryId = store.Data.NoteLibraries[libraries.SelectedIndex].Id; Render(); } };
        var toolbar = Row(libraries, new Border { Width = 10 }, Button("＋ 笔记库", () => EditLibrary(null)), Button("库设置", () => EditLibrary(library)), Button(noteGraphView ? "思维导图" : "关系图谱", () => { noteGraphView = !noteGraphView; Render(); }), Button("＋ 新建笔记", () => EditKnowledgeNote(null), true), Button("Markdown 笔记", () => EditKnowledgeNote(null, true)));
        toolbar.Margin = new Thickness(0, 0, 0, 16); DockPanel.SetDock(toolbar, Dock.Top); dock.Children.Add(toolbar);
        var notes = store.Data.Items.Where(i => i.Kind == "笔记" && !i.Cancelled && i.LibraryId == library.Id).OrderByDescending(i => i.Modified).ToList();
        var query = search.Text.Trim();
        var filtered = notes.Where(n => (n.Title + " " + n.Note).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(220) }); grid.ColumnDefinitions.Add(new()); dock.Children.Add(grid);
        var left = Stack(Text(library.Name, 17, bold: true), Space(5), Text($"{notes.Count} 篇笔记 · {NotesModel.Graph(store.Data, library).Edges.Count} 条连接", 11, Muted), Space(16));
        foreach (var note in filtered)
        {
            var b = Button("", () => EditKnowledgeNote(note)); b.Content = Stack(Text(note.Title, 13, bold: true), Space(5), Text(note.Modified.ToString("MM-dd HH:mm") + " · " + note.NoteLinks.Count + " 个关联", 10, Muted));
            b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 0, 12, 8); left.Children.Add(b);
        }
        if (filtered.Count == 0) left.Children.Add(Text(query.Length > 0 ? "没有匹配的笔记" : "从一篇笔记开始。", 12, Muted));
        left.Children.Add(Space(16)); left.Children.Add(Button("日程备注与附件", ShowLegacyMaterials));
        grid.Children.Add(new ScrollViewer { Content = left });
        if (noteGraphView)
        {
            var graph = BuildNoteGraph(library, query); Grid.SetColumn(graph, 1); grid.Children.Add(graph);
        }
        else
        {
            var map = BuildNoteMindMap(library, query); Grid.SetColumn(map, 1); grid.Children.Add(map);
        }
    }
    void EditLibrary(NoteLibrary? existing)
    {
        var dialog = Dialog(this, existing == null ? "新建笔记库" : "笔记库设置", 570, 470);
        var name = new TextBox { Text = existing?.Name ?? "", MaxLength = 80 };
        var courses = new CheckBox { Content = "允许关联整门课程", IsChecked = existing?.AllowCourses ?? true };
        var items = new CheckBox { Content = "允许关联待办、考试、个人活动与校园活动", IsChecked = existing?.AllowItems ?? true };
        dialog.Content = new Border { Padding = new Thickness(25), Child = Stack(Text("为一组想法留个空间", 22, bold: true), Space(20), Field("笔记库名称", name), courses, items, Space(12), Text("每个库独立管理笔记和图谱。关闭关联类别只隐藏相关连接，不删除已有关系。", 12, Muted), Space(22), Row(Button("取消", () => dialog.Close()), Button("保存笔记库", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { MessageBox.Show(dialog, "请填写笔记库名称。"); return; }
            if (store.Data.NoteLibraries.Any(l => l.Id != existing?.Id && l.Name == name.Text.Trim())) { MessageBox.Show(dialog, "笔记库名称已存在。"); return; }
            var library = existing ?? new NoteLibrary();
            if (Commit(() => { library.Name = name.Text.Trim(); library.AllowCourses = courses.IsChecked == true; library.AllowItems = items.IsChecked == true; if (existing == null) store.Data.NoteLibraries.Add(library); })) { noteLibraryId = library.Id; dialog.Close(); Render(); }
        }, true))) };
        dialog.ShowDialog();
    }
    void ShowLegacyMaterials()
    {
        var dialog = Dialog(this, "日程备注与附件", 720, 700); var list = new StackPanel { Margin = new Thickness(22) };
        foreach (var i in store.Data.Items.Where(i => i.Kind != "笔记" && (i.Note.Length > 0 || i.Attachments.Count > 0))) list.Children.Add(Card(Stack(Text(i.Title, 17, bold: true), Space(7), Text(i.Kind + " · " + i.Attachments.Count + " 个附件", 12, Muted), Space(8), Text(i.Note, 13), Space(10), Button("打开事项", () => Edit(new(i, i.Start, i.End))))));
        if (list.Children.Count == 0) list.Children.Add(Text("暂无附带备注或附件的日程。", 14, Muted));
        dialog.Content = new ScrollViewer { Content = list }; dialog.ShowDialog();
    }
    void OpenNoteTarget(NoteNode node)
    {
        if (node.Kind == "note") { var item = store.Data.Items.FirstOrDefault(i => NotesModel.Key(i) == node.Key); if (item != null) EditKnowledgeNote(item); return; }
        var linked = store.Data.Items.Where(i => i.Kind == "笔记" && i.LibraryId == CurrentLibrary.Id && i.NoteLinks.Any(l => l.Key == node.Key)).ToList();
        var dialog = Dialog(this, node.Kind == "course" ? "课程与笔记" : "事项与笔记", 680, 650);
        var panel = Stack(Text(node.Label, 23, bold: true), Space(10), Text(node.Detail, 12, Muted), Space(18), Text("关联笔记", 16, bold: true), Space(12)); panel.Margin = new Thickness(25);
        foreach (var n in linked)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = Button("取消关联", () =>
            {
                var current = store.Data.Items.FirstOrDefault(i => i.Id == n.Id);
                if (current == null) { panel.Children.Remove(row); return; }
                if (Commit(() => { current.NoteLinks.RemoveAll(l => l.Key == node.Key); current.Modified = DateTime.Now; NotesModel.SyncReciprocal(store.Data, current); })) { panel.Children.Remove(row); }
            });
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(Button(n.Title, () => EditKnowledgeNote(n))); panel.Children.Add(row);
        }
        if (node.Kind == "course")
        {
            panel.Children.Add(Space(20)); panel.Children.Add(Text("课程信息", 16, bold: true));
            foreach (var course in store.Data.Items.Where(i => i.Kind == "课程" && NotesModel.CourseKey(i) == node.Key).OrderBy(i => i.Start).Take(40)) panel.Children.Add(Text($"{course.Start:MM-dd HH:mm} — {course.End:HH:mm} · {course.Location}" + (course.Cancelled ? " · 已删除" : course.SchoolSuspended ? " · 停课" : ""), 12, Muted));
        }
        else if (node.Kind == "item")
        {
            var item = store.Data.Items.FirstOrDefault(i => "item:" + i.Id == node.Key); if (item != null) { panel.Children.Add(Space(20)); panel.Children.Add(Button("打开原事项", () => Edit(new(item, item.Start, item.End)))); }
        }
        else if (node.Kind == "campus")
        {
            string url = node.Key[7..]; if (CampusFeeds.SafeUrl(url)) { panel.Children.Add(Space(20)); panel.Children.Add(Button("查看官网原文", () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))); }
        }
        dialog.Content = new ScrollViewer { Content = panel }; dialog.ShowDialog();
    }
    void ChooseNoteLinks(Window owner, Item draft, NoteLibrary library, string category, Action changed)
    {
        var dialog = Dialog(owner, "选择关联", 700, 660);
        var dock = new DockPanel { Margin = new Thickness(24) }; dialog.Content = dock;
        var query = new TextBox { ToolTip = "搜索标题或课程教师" }; var list = new StackPanel();
        var candidates = NotesModel.Targets(store.Data, library, draft.Id).Where(t => category == "other" ? t.Kind is "item" or "campus" : t.Kind == category).ToList();
        var selected = draft.NoteLinks.Select(l => l.Key).ToHashSet();
        var bottom = Row(Button("取消", () => dialog.Close()), Button("确定关联", () =>
        {
            var keys = candidates.Select(c => c.Key).ToHashSet(); draft.NoteLinks.RemoveAll(l => keys.Contains(l.Key));
            draft.NoteLinks.AddRange(candidates.Where(c => selected.Contains(c.Key)).Select(c => new NoteLink { Key = c.Key, Label = c.Label, Kind = c.Kind }));
            changed(); dialog.Close();
        }, true)); DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var top = Stack(Text("连接相关的内容", 22, bold: true), Space(12), query, Space(14)); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); dock.Children.Add(new ScrollViewer { Content = list });
        void Draw()
        {
            list.Children.Clear();
            foreach (var c in candidates.Where(c => (c.Label + c.Detail).Contains(query.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var box = new CheckBox { Content = Stack(Text(c.Label, 14, bold: true), Text(c.Detail, 11, Muted)), IsChecked = selected.Contains(c.Key), Margin = new Thickness(0, 7, 0, 7) };
                box.Click += (_, _) => { if (box.IsChecked == true) selected.Add(c.Key); else selected.Remove(c.Key); }; list.Children.Add(box);
            }
            if (list.Children.Count == 0) list.Children.Add(Text("暂无可关联的内容。笔记只能关联同一个库中的笔记。", 13, Muted));
        }
        query.TextChanged += (_, _) => Draw(); Draw(); dialog.ShowDialog();
    }
    void EditKnowledgeNote(Item? original, bool markdown = false, Item? template = null)
    {
        var library = original == null && template == null ? CurrentLibrary : store.Data.NoteLibraries.First(l => l.Id == (original ?? template)!.LibraryId);
        var draft = original?.Copy() ?? template?.Copy() ?? new Item { Kind = "笔记", LibraryId = library.Id, Reminders = new() };
        markdown = markdown || draft.NoteFormat == MarkdownNotes.Format;
        var dialog = Dialog(this, "笔记 · " + library.Name, markdown ? 1380 : 1120, 830); dialog.MinWidth = 900;
        var dock = new DockPanel { Margin = new Thickness(24) }; dialog.Content = dock;
        bool dirty = false, closing = false; var openedModified = original?.Modified;
        var status = Text("保存在本地 · " + library.Name, 12, Muted);
        var actions = new DockPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        var title = new TextBox { Text = draft.Title, FontSize = 23, MaxLength = 300, Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(title, Dock.Top); dock.Children.Add(title);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(265) }); dock.Children.Add(grid);
        var editorDock = new DockPanel { Margin = new Thickness(0, 0, 18, 0) }; grid.Children.Add(editorDock);
        INoteCodec codec = new RichNoteCodec();
        var editor = new RichTextBox { Background = Brushes.White, BorderBrush = Line, BorderThickness = new Thickness(1), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsTab = true };
        try { editor.Document = codec.Decode(draft.NoteFormat is "rtf-v1" or "rtf-v2" ? draft.NoteBody : "", draft.Note); editor.Document.Foreground = Ink; }
        catch (Exception ex) { MessageBox.Show(this, "笔记正文格式无法读取，已停止打开以保护原数据：" + ex.Message); return; }
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(tools, Dock.Top); editorDock.Children.Add(tools); editorDock.Children.Add(editor);
        void Tool(string label, Action action) { var b = Button(label, action); b.Focusable = false; b.Padding = new Thickness(10, 7, 10, 7); b.Margin = new Thickness(0, 0, 6, 5); tools.Children.Add(b); }
        Tool("粗体", () => EditingCommands.ToggleBold.Execute(null, editor)); Tool("斜体", () => EditingCommands.ToggleItalic.Execute(null, editor)); Tool("下划线", () => EditingCommands.ToggleUnderline.Execute(null, editor));
        Tool("标题", () => { editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 24.0); editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold); editor.Focus(); });
        Tool("正文", () => { editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 15.0); editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal); editor.Focus(); });
        Tool("项目符号", () => EditingCommands.ToggleBullets.Execute(null, editor)); Tool("编号", () => EditingCommands.ToggleNumbering.Execute(null, editor));
        Tool("撤销", () => { if (editor.CanUndo) editor.Undo(); }); Tool("重做", () => { if (editor.CanRedo) editor.Redo(); });
        Tool("插入图片", () =>
        {
            var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp", Title = "插入本地图片" }; if (picker.ShowDialog(dialog) != true) return;
            try {
                using var stream = File.OpenRead(picker.FileName); var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1200; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                var picture = new System.Windows.Controls.Image { Source = bitmap, Width = Math.Min(520, bitmap.PixelWidth), Stretch = Stretch.Uniform };
                new InlineUIContainer(picture, editor.CaretPosition); dirty = true;
            } catch (Exception ex) { MessageBox.Show(dialog, "图片无法插入：" + ex.Message); }
        });
        Tool("排版", () => EditTypography(dialog, editor, () => { dirty = true; status.Text = "排版已修改 · 保存后生效"; }));
        Tool("数学公式", () => EditFormula(dialog, editor));
        NoteCanvas? paper = null;
        Tool("手写", () => paper?.SetMode(System.Windows.Controls.InkCanvasEditingMode.Ink));
        editor.PreviewMouseDoubleClick += (_, e) => {
            DependencyObject? target = e.OriginalSource as DependencyObject;
            while (target != null && target is not System.Windows.Controls.Image) target = target is Visual ? VisualTreeHelper.GetParent(target) : null;
            if (target is System.Windows.Controls.Image image && image.Tag is NoteEmbed embed) { e.Handled = true; if (embed.Kind == "math") EditFormula(dialog, editor, image); else if (embed.Kind == "ink") EditHandwriting(dialog, editor, image); }
        };
        var side = new StackPanel(); var sideScroll = new ScrollViewer { Content = side }; Grid.SetColumn(sideScroll, 1); grid.Children.Add(sideScroll);
        var links = new StackPanel(); var files = new StackPanel(); var staged = new Dictionary<Attachment, string>();
        void Mark() { dirty = true; status.Text = "有未保存的修改 · Ctrl+S 保存"; }
        MarkdownEditor? markdownEditor = null;
        if (markdown) { editorDock.Children.Remove(tools); editorDock.Children.Remove(editor); markdownEditor = new MarkdownEditor(draft.NoteBody, Mark); editorDock.Children.Add(markdownEditor); }
        else
        {
            editorDock.Children.Remove(editor);
            var formatting = NoteFormatting.TextTools(editor); DockPanel.SetDock(formatting, Dock.Top); editorDock.Children.Add(formatting);
            var formulaTools = NoteFormatting.FormulaTools(editor, Mark); DockPanel.SetDock(formulaTools, Dock.Top); editorDock.Children.Add(formulaTools);
            try { paper = new NoteCanvas(editor, draft.NoteInk, Mark); } catch (Exception ex) { MessageBox.Show(this, "笔迹无法读取，已停止打开以保护数据：" + ex.Message); return; }
            editorDock.Children.Add(paper);
        }
        if (!markdown) Tool("复制文字为 Markdown", () => { if (staged.Count > 0) { MessageBox.Show(dialog, "请先保存新添加的附件，再复制为 Markdown。"); return; } var copy = draft.Copy(); copy.Id = Guid.NewGuid().ToString("N"); copy.Title = title.Text + " · Markdown"; copy.NoteBody = codec.PlainText(editor.Document); copy.NoteFormat = MarkdownNotes.Format; copy.NoteInk = ""; EditKnowledgeNote(null, true, copy); });
        void DrawLinks()
        {
            links.Children.Clear(); var targets = NotesModel.Targets(store.Data, library, draft.Id).ToDictionary(t => t.Key);
            foreach (var link in draft.NoteLinks.Where(l => NotesModel.Visible(l, library)).ToList())
            {
                var label = targets.TryGetValue(link.Key, out var found) ? found.Label : link.Label + "（已移除）";
                var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) }; var remove = Button("×", () => { draft.NoteLinks.Remove(link); Mark(); DrawLinks(); }); remove.Padding = new Thickness(7, 2, 7, 2); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(Text(label, 12)); links.Children.Add(row);
            }
            if (links.Children.Count == 0) links.Children.Add(Text("尚未建立关联", 12, Muted));
        }
        side.Children.Add(Text("关联内容", 17, bold: true)); side.Children.Add(Space(12));
        side.Children.Add(Button("关联笔记", () => ChooseNoteLinks(dialog, draft, library, "note", () => { Mark(); DrawLinks(); })));
        if (library.AllowCourses) side.Children.Add(Button("关联课程", () => ChooseNoteLinks(dialog, draft, library, "course", () => { Mark(); DrawLinks(); })));
        if (library.AllowItems) side.Children.Add(Button("关联事项", () => ChooseNoteLinks(dialog, draft, library, "other", () => { Mark(); DrawLinks(); })));
        side.Children.Add(Space(12)); side.Children.Add(links); DrawLinks();
        side.Children.Add(Space(24)); side.Children.Add(Text("与此笔记关联", 17, bold: true)); side.Children.Add(Space(10));
        var backlinks = store.Data.Items.Where(i => i.Kind == "笔记" && i.LibraryId == library.Id && i.Id != draft.Id && i.NoteLinks.Any(l => l.Key == "note:" + draft.Id)).ToList();
        foreach (var back in backlinks) side.Children.Add(Button(back.Title, () => EditKnowledgeNote(back)));
        if (backlinks.Count == 0) side.Children.Add(Text("尚无其他关联笔记", 12, Muted));
        side.Children.Add(Space(24)); side.Children.Add(Text("本地附件", 17, bold: true)); side.Children.Add(Space(10)); side.Children.Add(files);
        void DrawFiles()
        {
            files.Children.Clear(); foreach (var a in draft.Attachments.ToList())
            {
                var row = new DockPanel(); var remove = Button("×", () => { draft.Attachments.Remove(a); staged.Remove(a); Mark(); DrawFiles(); }); remove.Padding = new Thickness(7, 2, 7, 2); DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var open = Button(a.Name, () =>
                {
                    string path = staged.TryGetValue(a, out var local) ? local : Path.Combine(store.Files, a.File);
                    if (new[] { ".exe", ".bat", ".cmd", ".ps1", ".js", ".vbs", ".msi", ".scr", ".com", ".lnk", ".url", ".hta" }.Contains(Path.GetExtension(path).ToLowerInvariant())) { MessageBox.Show(dialog, "程序或脚本附件请到数据文件夹检查后打开。"); return; }
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
                }); open.Content = Text(a.Name, 11); row.Children.Add(open); files.Children.Add(row);
            }
        }
        DrawFiles(); side.Children.Add(Button("＋ 添加附件", () => { var picker = new OpenFileDialog { Multiselect = true }; if (picker.ShowDialog(dialog) == true) foreach (string file in picker.FileNames) { var a = new Attachment { Name = Path.GetFileName(file) }; draft.Attachments.Add(a); staged[a] = file; Mark(); } DrawFiles(); }));
        bool Save()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title.Text)) { MessageBox.Show(dialog, "请填写笔记标题。"); return false; }
                var current = original == null ? null : store.Data.Items.FirstOrDefault(i => i.Id == original.Id);
                if (original != null && current == null) { MessageBox.Show(dialog, "原笔记已删除，请先复制正文保存。"); return false; }
                if (current != null && current.Modified != openedModified) { MessageBox.Show(dialog, "笔记已在其他窗口更新，请先复制当前内容，关闭后重新打开，避免覆盖。"); return false; }
                var updated = current?.Copy() ?? draft.Copy(); updated.Title = title.Text.Trim(); updated.Note = markdown ? MarkdownNotes.Plain(markdownEditor!.Source.Text) : codec.PlainText(editor.Document); updated.NoteBody = markdown ? markdownEditor!.Source.Text : codec.Encode(editor.Document); updated.NoteFormat = markdown ? MarkdownNotes.Format : codec.Format; updated.NoteInk = markdown ? draft.NoteInk : paper!.Encode(); updated.NoteLinks = draft.NoteLinks; updated.Modified = DateTime.Now;
                foreach (var pair in staged.ToList()) { pair.Key.File = store.AddFile(pair.Value).File; staged.Remove(pair.Key); }
                updated.Attachments = draft.Attachments;
                if (!Commit(() => { if (current != null) store.Data.Items.Remove(current); store.Data.Items.Add(updated); NotesModel.SyncReciprocal(store.Data, updated); })) return false;
                dirty = false; closing = true; dialog.Close(); return true;
            }
            catch (Exception ex) { MessageBox.Show(dialog, "笔记未保存：" + ex.Message); return false; }
        }
        var buttons = Row(Button("取消", () => dialog.Close()), Button("保存", () => Save(), true)); DockPanel.SetDock(buttons, Dock.Right); actions.Children.Add(buttons);
        if (original != null) { var delete = Button("删除笔记", () =>
        {
            if (MessageBox.Show(dialog, "删除这篇笔记及其关联连线？其他笔记和课程将保留。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Commit(() => { store.Data.Items.RemoveAll(i => i.Id == draft.Id); foreach (var n in store.Data.Items) { n.NoteLinks.RemoveAll(l => l.Key == "note:" + draft.Id); if (n.ParentId == draft.Id) n.ParentId = null; } library.Positions.Remove("note:" + draft.Id); })) { closing = true; dialog.Close(); }
        }); DockPanel.SetDock(delete, Dock.Left); actions.Children.Add(delete); }
        actions.Children.Add(status);
        title.TextChanged += (_, _) => Mark(); editor.TextChanged += (_, _) => Mark();
        dialog.PreviewKeyDown += (_, e) => { if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control) { if (markdown) markdownEditor!.Insert("\n$$\n", "\n$$\n", @"\frac{a}{b}"); else EditFormula(dialog, editor); e.Handled = true; }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; } };
        dialog.Closing += (_, e) =>
        {
            if (closing || !dirty || preview && Equals(dialog.Tag, "test-abort")) return;
            var answer = MessageBox.Show(dialog, "笔记有未保存的修改。是否放弃修改？", "离开笔记", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        };
        dialog.Loaded += (_, _) => { title.Focus(); dirty = false; }; dialog.ShowDialog();
    }
}





