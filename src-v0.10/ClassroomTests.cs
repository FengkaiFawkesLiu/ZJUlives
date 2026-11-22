using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZJUSS.Core.Notes;
using ZJUSS.Core.Classroom;

namespace Shike;
public static class ClassroomTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Check(bool yes) { if (!yes) throw new Exception("Classroom assertion failed"); }
        test("Published PDF extraction dependencies read a synthetic Chinese text layer",()=>{
            var result=new CoursewareTextExtractor().ExtractAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","classroom-synthetic.pdf")).GetAwaiter().GetResult();
            Check(result.IsAvailable&&result.Text!.Contains("只有一页的课件文字")&&result.PageCount==1);
        });
        test("Published PPTX extraction dependencies read synthetic Chinese slides",()=>{
            var result=new CoursewareTextExtractor().ExtractAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","classroom-synthetic.pptx")).GetAwaiter().GetResult();
            Check(result.IsAvailable&&result.Format=="pptx"&&result.Text!.Contains("[幻灯片1]"));
        });
        test("Classroom matching requires unique course and normalized term", () => {
            var data = new AppData(); var course = new CloudCourse { Id="c", Name="数学", Term="2026-2027-1" }; data.Classroom.Courses.Add(course);
            data.Items.Add(new() { Kind="课程", Title="数学", Scope="zju-hash-2026-3-course", Teacher="甲" });
            ClassroomModel.Match(data,course); Check(course.CourseKey == NotesModel.CourseKey(data.Items[0]));
            data.Classroom.AccountHash="other-account";ClassroomModel.Match(data,course);Check(course.CourseKey==null);data.Classroom.AccountHash=null;
            data.Items.Add(new() { Kind="课程", Title="数学", Scope="zju-hash-2026-3-course", Teacher="乙" });
            ClassroomModel.Match(data,course); Check(course.CourseKey == null);
            course.ManualLink=true; course.CourseKey="course:chosen"; ClassroomModel.Match(data,course); Check(course.CourseKey=="course:chosen");
        });
        test("Classroom note versions preserve edits and repeated job cannot duplicate", () => {
            var data = new AppData(); var course = new CloudCourse { Id="c", Name="数学", Term="2026-2027-1", CourseKey="course:math" }; var lesson = new CloudLesson { Id="l" };
            var parsed = new NoteParseResult { Success=true, Title="数学 2026-10-24", BodyMarkdown="## 课堂要点\n内容" }; var job = new CloudJob();
            var note = ClassroomModel.CreateNote(data,course,lesson,job,parsed,new()); note.NoteBody="我的修改";
            Check(ClassroomModel.CreateNote(data,course,lesson,job,parsed,new()).Id==note.Id);
            var next=ClassroomModel.CreateNote(data,course,lesson,new(),parsed,new());
            Check(data.Items.Count==2 && note.NoteBody=="我的修改" && next.ClassroomSource!.Version==2 && next.NoteLinks.Single().Key=="course:math");
        });
        test("Classroom candidates only become todos on confirmation and confirmation deduplicates", () => {
            var data=new AppData(); var candidate=new CloudCandidate { Text="预习" }; var note=new Item { Kind="笔记",Title="课堂笔记" }; data.Items.Add(note);
            Check(!data.Items.Any(i=>i.Kind=="待办")); var todo=ClassroomModel.ConfirmCandidate(data,note,candidate,"预习下一章",null);
            Check(todo.AssistantNoDue && ClassroomModel.ConfirmCandidate(data,note,candidate,"重复",null).Id==todo.Id && data.Items.Count==2);
        });
        test("Classroom queue, attachments and provenance survive backup and restore", () => {
            var store=new Store(Path.Combine(temp,"classroom-backup")); var file=Path.Combine(temp,"classroom.pdf"); File.WriteAllText(file,"synthetic fixture");
            store.Data.Items.Add(new() { Kind="笔记", Title="材料", ClassroomSource=new() {CourseId="c",LessonId="l",JobId="j",Version=1}, Attachments=new(){store.AddFile(file)} });
            store.Data.Classroom.Jobs.Add(new() {Id="j",CourseId="c",LessonId="l",State="待选择材料",FilesConfirmed=true,FileKeys=new(){"f"}});
            string zip=Path.Combine(temp,"classroom.zip"); store.Backup(zip); store.Data.Classroom=new(); store.Save(); store.Restore(zip);
            Check(store.Data.Classroom.Jobs.Single().FilesConfirmed && store.Data.Items.Single().ClassroomSource!.JobId=="j" && File.Exists(Path.Combine(store.Files,store.Data.Items.Single().Attachments.Single().File)));
        });
    }
}
