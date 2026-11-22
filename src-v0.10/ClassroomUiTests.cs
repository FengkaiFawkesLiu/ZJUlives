using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Shike;
public sealed partial class MainWindow
{
    void RunClassroomUiTests(string dir, Action<bool,string> check, Action<Action,Action<AppPanel>> modal)
    {
        var course=new CloudCourse{Id="fixture",Name="线性代数",Term=ClassroomModel.CurrentTerm(store.Data.Settings),Updated=DateTime.Now,
            Lessons=new(){new(){Id="l1",Date="2026-10-24",Session="第 3–4 节",Title="向量空间",FilesListed=true,TranscriptRead=true},new(){Id="l2",Date="2026-10-23",Title="矩阵运算"}}};
        store.Data.Classroom.Courses.Add(course);store.Data.Classroom.Updated=DateTime.Now;
        SwitchView("智云课堂");UpdateLayout();WaitForPanelLayout();
        check(navButtons.ContainsKey("智云课堂") && GroupOf("智云课堂")==GroupOf("校园助手"),"Classroom sits beside campus assistant in existing navigation");
        check(Visuals<TextBlock>(body).Any(t=>t.Text=="线性代数"),"Current term course card renders");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-课程.png"));
        OpenCloudCourse(course);UpdateLayout();WaitForPanelLayout();
        check(cloudSelected.SetEquals(new[]{"l1","l2"}),"New lessons are selected by default");
        check(Visuals<CheckBox>(body).Count()==2,"Lesson selection uses native Shike controls");
        Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-课节.png"));
        modal(()=>CloudLink(course),panel=>{check(Visuals<ComboBox>(panel).Count()==1,"Manual course association is an inline Shike panel");Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-关联.png"));panel.Close();});
        modal(CloudSettings,panel=>{check(Visuals<PasswordBox>(panel).Count()==1,"API secret is masked in configuration panel");Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-AI设置.png"));panel.Close();});
        check(store.Data.Items.All(i=>i.Kind!="待办"),"Browsing classroom does not create todos");
        check(Visuals<Button>(body).Any(b=>b.Content?.ToString()=="一键收集并生成笔记"),"Batch action is available in lesson list");
        var job=new CloudJob{CourseId=course.Id,LessonId="l1"};store.Data.Classroom.Jobs.Add(job);
        var lesson=course.Lessons[0];lesson.Files=new(){new(){FileId="a",OriginalFileName="讲义.pdf"},new(){FileId="b",OriginalFileName="补充例题.pdf"}};
        bool chosen=false;
        modal(()=>chosen=ChooseCloudFiles(course,lesson,job,default).GetAwaiter().GetResult(),panel=>{
            var boxes=Visuals<CheckBox>(panel).ToList();check(boxes.Count==2&&boxes.All(b=>b.IsChecked==false),"Multiple courseware requires explicit selection");boxes[0].IsChecked=true;
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-课件选择.png"));
            Visuals<Button>(panel).First(b=>b.Content?.ToString()=="使用所选课件").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(chosen&&job.FileKeys.SequenceEqual(new[]{"a"}),"Only selected courseware is confirmed");
        var materials=new CloudMaterials{Transcript="合成课堂文字",Missing=new(){"缺少课件文字"}};
        CloudMissingChoice decision=CloudMissingChoice.Continue;
        modal(()=>decision=ChooseCloudMissing(course,lesson,materials,default).GetAwaiter().GetResult(),panel=>{
            check(Visuals<Button>(panel).Any(b=>b.Content?.ToString()=="跳过本节，继续批次"),"Missing-material dialog offers continue, skip and pause");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-材料缺失.png"));panel.Close();
        });
        check(decision==CloudMissingChoice.Pause,"Closing missing-material prompt pauses batch");
        var note=ClassroomModel.CreateNote(store.Data,course,lesson,job,new(){Title="线性代数 · 向量空间",BodyMarkdown="## 课堂要点\n基与维数",Success=true,Candidates=new ZJUSS.Core.Notes.NoteCandidate[]{new(){Id="candidate",Text="复习向量空间",SourceLessonText="合成课堂"}}},new());
        var edited=note.Copy();edited.NoteBody="用户已编辑";store.Data.Items.Remove(note);store.Data.Items.Add(edited);
        modal(()=>CloudCandidates(note),panel=>{
            check(store.Data.Items.All(i=>i.Kind!="待办"),"Candidate preview does not create tasks");
            Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-待办候选.png"));
            Visuals<Button>(panel).First(b=>b.Content?.ToString()=="确认加入待办").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(store.Data.Items.Count(i=>i.Kind=="待办")==1 && store.Data.Items.Single(i=>i.Id==note.Id).ClassroomSource!.Candidates.Single().TodoId!=null,"Candidate confirmation uses current saved note after editing");panel.Close();
        });
        Render();UpdateLayout();Theme.Screenshot((FrameworkElement)Content,Path.Combine(dir,"智云课堂-任务与笔记.png"));
        modal(CloudResumeNotice,panel=>{check(Visuals<Button>(panel).Any(b=>b.Content?.ToString()=="查看未完成任务"),"Startup resume notice waits for user action");panel.Close();});
    }
}
