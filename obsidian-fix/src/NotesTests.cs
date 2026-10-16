using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;

namespace Shike;

public static class NotesTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
        Item Note(string title) => new() { Kind = "笔记", Title = title };
        test("Rich note codec preserves bold text and plain search text", () => {
            INoteCodec codec = new RichNoteCodec(); var document = codec.Decode("", ""); document.Blocks.Clear(); document.Blocks.Add(new Paragraph(new Bold(new Run("课堂重点"))));
            var result = codec.Decode(codec.Encode(document), ""); Assert(codec.PlainText(result) == "课堂重点");
            var pointer = result.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward); Assert(pointer != null && (FontWeight)new TextRange(pointer, pointer.GetNextInsertionPosition(LogicalDirection.Forward) ?? pointer).GetPropertyValue(TextElement.FontWeightProperty) == FontWeights.Bold);
        });
        test("Course graph deduplicates meetings and isolates libraries", () => {
            var data = new AppData(); var library = data.NoteLibraries[0]; data.NoteLibraries.Add(new() { Id = "other", Name = "另一个库" });
            var a = Note("A"); var b = Note("B"); b.LibraryId = "other";
            var course = new Item { Kind = "课程", Title = "数学", Teacher = "老师", Scope = "term" }; var otherMeeting = course.Copy(); otherMeeting.Id = Guid.NewGuid().ToString("N"); otherMeeting.Start = otherMeeting.Start.AddDays(7); otherMeeting.End = otherMeeting.End.AddDays(7);
            a.NoteLinks.Add(new() { Key = NotesModel.CourseKey(course), Label = course.Title, Kind = "course" }); data.Items.AddRange(new[] { a, b, course, otherMeeting });
            var graph = NotesModel.Graph(data, library); Assert(graph.Nodes.Count == 2 && graph.Edges.Count == 1 && NotesModel.Targets(data, library).Count(n => n.Kind == "course") == 1 && graph.Nodes.All(n => n.Label != "B"));
            library.AllowCourses = false; Assert(NotesModel.Graph(data, library).Nodes.Count == 1 && a.NoteLinks.Count == 1); library.AllowCourses = true; Assert(NotesModel.Graph(data, library).Edges.Count == 1);
        });
        test("Embedded local image survives rich note save and reload", () => {
            var codec = new RichNoteCodec(); var document = codec.Decode("", "图片说明");
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
            ((Paragraph)document.Blocks.FirstBlock!).Inlines.Add(new InlineUIContainer(new System.Windows.Controls.Image { Source = bitmap, Width = 40, Height = 40 }));
            var encoded = codec.Encode(document); var decoded = codec.Decode(encoded, "");
            Assert(decoded.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Any(c => c.Child is System.Windows.Controls.Image));
        });
        test("Cross-library note links rejected before persistence", () => {
            var data = new AppData(); data.NoteLibraries.Add(new() { Id = "other" }); var a = Note("A"); var b = Note("B"); b.LibraryId = "other";
            a.NoteLinks.Add(new() { Key = "note:" + b.Id, Kind = "note", Label = "B" }); data.Items.AddRange(new[] { a, b });
            try { Engine.Validate(data); throw new Exception("Cross-library link accepted"); } catch (InvalidDataException) { }
        });
        test("Note backup restores rich text, attachments, links and graph positions", () => {
            var store = new Store(Path.Combine(temp, "notes-backup")); var a = Note("A"); var b = Note("B"); a.NoteLinks.Add(new() { Key = NotesModel.Key(b), Label = "B", Kind = "note" });
            var codec = new RichNoteCodec(); a.NoteBody = codec.Encode(codec.Decode("", "本地内容")); a.NoteFormat = codec.Format; a.Note = "本地内容";
            string file = Path.Combine(temp, "note-attachment.txt"); File.WriteAllText(file, "附件内容"); a.Attachments.Add(store.AddFile(file)); store.Data.Items.AddRange(new[] { a, b }); store.Data.NoteLibraries[0].Positions[NotesModel.Key(a)] = new() { X = 420, Y = 230 };
            string zip = Path.Combine(temp, "notes-full.zip"); store.Backup(zip); store.Data.Items.Clear(); store.Save(); store.Restore(zip);
            var restored = store.Data.Items.Single(n => n.Id == a.Id); Assert(codec.PlainText(codec.Decode(restored.NoteBody, "")) == "本地内容" && restored.NoteLinks.Single().Key == NotesModel.Key(b) && File.ReadAllText(Path.Combine(store.Files, restored.Attachments.Single().File)) == "附件内容" && store.Data.NoteLibraries[0].Positions[NotesModel.Key(a)].X == 420);
        });
        test("Legacy note migration preserves IDs, notes and course association with one backup", () => {
            var store = new Store(Path.Combine(temp, "notes-migrate")); var course = new Item { Title = "课程", Kind = "课程" }; var a = Note("旧笔记"); a.Note = "保留"; a.ParentId = course.Id; store.Data.Items.AddRange(new[] { course, a }); store.Save(); NotesModel.Migrate(store); NotesModel.Migrate(store);
            Assert(a.LibraryId == "default" && a.Note == "保留" && a.NoteLinks.Single().Key == NotesModel.CourseKey(course) && Directory.GetFiles(store.Root, "笔记系统升级前-*.zip").Length == 1);
        });
        test("Deleting a target leaves external reference recognizable", () => {
            var data = new AppData(); var a = Note("A"); a.NoteLinks.Add(new() { Key = "item:missing", Kind = "item", Label = "旧活动" }); data.Items.Add(a); var graph = NotesModel.Graph(data, data.NoteLibraries[0]); Assert(graph.Nodes.Single(n => n.Kind == "item").Missing);
        });
        test("Adding and removing note association updates both endpoints without duplicates", () => {
            var data = new AppData(); var a = Note("A"); var b = Note("B"); data.Items.AddRange(new[] { a, b }); var target = NotesModel.Targets(data, data.NoteLibraries[0], a.Id).Single();
            NotesModel.Connect(data, data.NoteLibraries[0], NotesModel.Key(a), target); NotesModel.Connect(data, data.NoteLibraries[0], NotesModel.Key(a), target);
            Assert(a.NoteLinks.Count == 1 && b.NoteLinks.Single().Key == NotesModel.Key(a) && NotesModel.Graph(data, data.NoteLibraries[0]).Edges.Count == 1);
            b.NoteLinks.Clear(); NotesModel.SyncReciprocal(data, b); Assert(a.NoteLinks.Count == 0 && b.NoteLinks.Count == 0);
        });
        test("Schema upgrade unions directed legacy edges instead of losing them", () => {
            var store = new Store(Path.Combine(temp, "reciprocal")); var a = Note("A"); var b = Note("B"); var c = Note("C");
            b.NoteLinks.Add(new() { Key = NotesModel.Key(a), Kind = "note", Label = a.Title }); c.NoteLinks.Add(new() { Key = NotesModel.Key(b), Kind = "note", Label = b.Title }); store.Data.Items.AddRange(new[] { a, b, c }); store.Data.NoteSchemaVersion = 1; store.Save(); NotesModel.Migrate(store);
            Assert(a.NoteLinks.Single().Key == NotesModel.Key(b) && b.NoteLinks.Count == 2 && c.NoteLinks.Count == 1 && store.Data.NoteSchemaVersion == 2);
            NotesModel.Migrate(store); Assert(NotesModel.Graph(store.Data, store.Data.NoteLibraries[0]).Edges.Count == 2);
        });
    }
}
