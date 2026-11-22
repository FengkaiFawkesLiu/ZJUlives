using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ZJUSS.Core.Classroom;
using static Shike.Theme;

namespace Shike;

public sealed partial class MainWindow
{
    ClassroomCollector? cloudCollector;
    SchoolCredential? sharedSchoolSession;
    CancellationTokenSource? cloudCancel;
    bool cloudBusy;
    string? cloudCourseId, cloudTerm;
    string cloudStatus = "选择课程，收集课堂材料并整理笔记。";
    readonly HashSet<string> cloudSelected = new();
    CloudApiVault CloudConfig => new(store.Root);

    void RenderClassroom()
    {
        var p = Page();
        var controls = Row(Button("课程列表", () => { cloudCourseId = null; cloudSelected.Clear(); Render(); }),
            Button("刷新课程", () => _ = CloudAction(async (c,t) => { await c.RefreshCourses(store.Data,t); store.Save(); }, "课程列表已更新。")),
            Button("AI 设置", CloudSettings), Button("课堂笔记库", OpenCloudLibrary));
        if (cloudBusy) controls.Children.Add(Button("暂停本次任务", () => cloudCancel?.Cancel()));
        p.Children.Add(controls); p.Children.Add(Space(12)); p.Children.Add(Text(cloudStatus,13,Muted)); p.Children.Add(Space(16));
        RenderCloudQueue(p);
        var course = store.Data.Classroom.Courses.FirstOrDefault(c => c.Id == cloudCourseId);
        if (course == null)
        {
            var terms = store.Data.Classroom.Courses.Select(c => c.Term).Distinct().OrderByDescending(t => t).ToList();
            var current = ClassroomModel.CurrentTerm(store.Data.Settings);
            if (!terms.Contains(current)) terms.Insert(0,current);
            if (cloudTerm == null) cloudTerm = current;
            var filter = Combo(new[] { "全部学期" }.Concat(terms).ToArray(),cloudTerm);
            filter.SelectionChanged += (_,_) => { cloudTerm = filter.SelectedItem?.ToString(); Render(); };
            p.Children.Add(Field("学期",filter));
            var courses = store.Data.Classroom.Courses.Where(c => (cloudTerm == "全部学期" || c.Term == cloudTerm) && (c.Name.Contains(search.Text,StringComparison.OrdinalIgnoreCase) || c.Term.Contains(search.Text))).OrderBy(c=>c.Name).ToList();
            if (courses.Count == 0) p.Children.Add(Empty("暂无课堂课程",store.Data.Classroom.Updated == null ? "点击“刷新课程”，使用拾课的学校账号读取智云课堂。" : "本学期没有缓存课程，可以切换学期或刷新。"));
            foreach (var item in courses)
            {
                var content=Stack(Text(item.Name,20,bold:true),Space(8),Text(item.Term+" · "+(item.CourseKey==null?"未关联拾课课程":"已关联课程"),12,Muted));
                if(item.Info.Length>0) {content.Children.Add(Space(6));content.Children.Add(Text(item.Info,12,Muted));}
                content.Children.Add(Space(12));content.Children.Add(Row(Button("查看课节",()=>OpenCloudCourse(item)),Button("关联课程",()=>CloudLink(item))));
                p.Children.Add(Card(content));p.Children.Add(Space(12));
            }
            p.Children.Add(Text(store.Data.Classroom.Updated is DateTime at ? "上次读取："+at.ToString("yyyy-MM-dd HH:mm") : "课程和材料按需读取；已保存材料可离线查看。",11,Muted));
            return;
        }
        p.Children.Add(Card(Stack(Text(course.Name,23,bold:true),Space(8),Text(course.Term+" · "+(course.CourseKey==null?"未关联课程，仍可整理笔记":"已关联拾课课程"),12,Muted),Space(12),
            Row(Button("刷新课节",()=>_ = CloudAction(async(c,t)=>{await c.RefreshLessons(course,t);store.Save();},"课节列表已更新。")),Button("关联课程",()=>CloudLink(course))))));
        p.Children.Add(Space(12));
        var batch=Row(Button("选择未生成",()=>{cloudSelected.Clear();foreach(var l in course.Lessons.Where(l=>!l.Removed && !ClassroomModel.Versions(store.Data,course.Id,l.Id).Any()))cloudSelected.Add(l.Id);Render();}),
            Button("全选",()=>{cloudSelected.Clear();foreach(var l in course.Lessons.Where(l=>!l.Removed))cloudSelected.Add(l.Id);Render();}),Button("清空选择",()=>{cloudSelected.Clear();Render();}));
        AddCloudGenerateButton(batch,course);
        p.Children.Add(batch);p.Children.Add(Space(12));
        if(course.Lessons.Count==0)p.Children.Add(Empty("暂无课节", "点击“刷新课节”读取该课程的上课记录。"));
        foreach(var lesson in course.Lessons.OrderByDescending(l=>l.Date).ThenBy(l=>l.Session))
        {
            var check = new CheckBox { Content=lesson.Label,IsChecked=cloudSelected.Contains(lesson.Id),IsEnabled=!lesson.Removed,FontSize=15,Margin=new Thickness(0,0,0,9) };
            check.Checked+=(_,_)=>cloudSelected.Add(lesson.Id);check.Unchecked+=(_,_)=>cloudSelected.Remove(lesson.Id);
            var versions=ClassroomModel.Versions(store.Data,course.Id,lesson.Id).ToList();
            var content=Stack(check,Text((lesson.Removed?"平台已移除 · ":"")+(versions.Count>0?$"已生成 {versions.Count} 个版本":"尚未生成笔记")+" · "+(lesson.TranscriptRead?"已收集语音文字":"语音文字未读取"),12,Muted),Space(12));
            var actions=Row(Button("课堂材料",()=>CloudMaterialsDialog(course,lesson)));
            if(versions.Count>0)actions.Children.Add(Button("笔记与版本",()=>CloudVersions(course,lesson)));
            content.Children.Add(actions);p.Children.Add(Card(content));p.Children.Add(Space(10));
        }
    }
    // Implemented by the queue partial; declarations let collection and generation evolve separately.
    partial void RenderCloudQueue(StackPanel panel);
    partial void AddCloudGenerateButton(Panel panel, CloudCourse course);

    void OpenCloudCourse(CloudCourse course)
    {
        cloudCourseId=course.Id;cloudSelected.Clear();
        foreach(var l in course.Lessons.Where(l=>!l.Removed&&!ClassroomModel.Versions(store.Data,course.Id,l.Id).Any()))cloudSelected.Add(l.Id);
        Render();
        if(course.Updated==null)_=CloudAction(async(c,t)=>{await c.RefreshLessons(course,t);store.Save();foreach(var l in course.Lessons.Where(l=>!l.Removed&&!ClassroomModel.Versions(store.Data,course.Id,l.Id).Any()))cloudSelected.Add(l.Id);},"课节列表已读取。");
    }
    ClassroomCollector? GetCloudCollector()
    {
        if(cloudCollector!=null)return cloudCollector;
        var credential=sharedSchoolSession ?? Vault.Read();
        if(credential==null) {AssistantLogin();credential=sharedSchoolSession??Vault.Read();}
        if(credential==null){cloudStatus="请先连接学校账号。";return null;}
        string owner=Engine.Hash(credential.Username);
        if(store.Data.Classroom.AccountHash is string prior && prior!=owner)
        {store.Data.Classroom=new();store.Save();cloudCourseId=null;cloudSelected.Clear();}
        store.Data.Classroom.AccountHash=owner;
        cloudCollector=new ClassroomCollector(store.Root,credential);return cloudCollector;
    }
    async Task CloudAction(Func<ClassroomCollector,CancellationToken,Task> action,string done)
    {
        if(cloudBusy){cloudStatus="当前有课堂任务正在运行，请稍候或暂停。";Render();return;}
        try
        {
            var collector=GetCloudCollector();if(collector==null){Render();return;}
            cloudCancel=new();cloudBusy=true;cloudStatus="正在读取课堂材料…";RenderCloudIfVisible();
            await action(collector,cloudCancel.Token);cloudCancel.Token.ThrowIfCancellationRequested();cloudStatus=done;
        }
        catch(OperationCanceledException){cloudStatus="任务已暂停，已完成材料保留。";}
        catch(Exception ex){cloudStatus="本次未完成："+CloudError(ex);}
        finally{cloudBusy=false;cloudCancel?.Dispose();cloudCancel=null;if(!exiting)RenderCloudIfVisible();}
    }
    static string CloudError(Exception ex) => ex switch {
        IOException=>"本机文件读写失败，请检查空间和权限后重试。",
        UnauthorizedAccessException=>"本机文件权限不足。",
        System.Security.Cryptography.CryptographicException=>"本机凭据无法解密，请重新配置。",
        InvalidOperationException or InvalidDataException=>ex.Message,
        _=>"连接或处理失败，请检查网络后重试。"
    };
    void RenderCloudIfVisible(){if(view=="智云课堂")Render();}
    void StopClassroom()
    {
        cloudCancel?.Cancel();sharedSchoolSession=null;
        cloudCollector?.Dispose();cloudCollector=null;
    }
    void OpenCloudLibrary()
    {
        if(!store.Data.NoteLibraries.Any(l=>l.Id=="zhiyun-classroom"))
            if(!Commit(()=>store.Data.NoteLibraries.Add(new(){Id="zhiyun-classroom",Name="课堂笔记"})))return;
        noteLibraryId="zhiyun-classroom";SwitchView("笔记与附件");
    }
    void CloudLink(CloudCourse course)
    {
        if(cloudBusy){UiMessage.Show(this,"请先暂停课堂任务，再修改关联。");return;}
        var choices=store.Data.Items.Where(i=>i.Kind=="课程"&&!i.Cancelled&&!i.SchoolRemoved).GroupBy(NotesModel.CourseKey).Select(g=>g.First()).OrderBy(i=>i.Title).ToList();
        var dialog=Dialog(this,"关联拾课课程",620,480);
        var select=Combo(new[]{"暂不关联"}.Concat(choices.Select(i=>i.Title+" · "+i.Teacher+" · "+(ClassroomModel.ScopeTerm(i.Scope)??"个人课程"))).ToArray(),"暂不关联");
        int selected=choices.FindIndex(i=>NotesModel.CourseKey(i)==course.CourseKey);select.SelectedIndex=selected+1;
        dialog.Content=new Border {Padding=new Thickness(26),Child=Stack(Text("为“"+course.Name+"”关联课程",21,bold:true),Space(14),select,Space(12),Text("生成笔记会关联到此课程；已有版本的课程关联也会同步更新。",12,Muted),Space(18),Row(Button("保存关联",()=>{
            string? old=course.CourseKey;string? key=select.SelectedIndex>0?NotesModel.CourseKey(choices[select.SelectedIndex-1]):null;
            if(Commit(()=>{course.CourseKey=key;course.ManualLink=true;foreach(var note in store.Data.Items.Where(n=>n.ClassroomSource?.CourseId==course.Id)){note.NoteLinks.RemoveAll(l=>l.Kind=="course"&&l.Key==old);if(key!=null&&!note.NoteLinks.Any(l=>l.Key==key))note.NoteLinks.Add(new(){Kind="course",Key=key,Label=course.Name});}}))dialog.Close();
        },true),Button("取消",()=>dialog.Close())))};dialog.ShowDialog();
    }
    async void CloudMaterialsDialog(CloudCourse course,CloudLesson lesson)
    {
        if(!lesson.FilesListed || !lesson.TranscriptRead)
            await CloudAction(async(c,t)=>await c.Inspect(course,lesson,store.Save,t),"课堂材料已收集。");
        if(exiting)return;
        var content=new LocalClassroomContentStore(Path.Combine(store.Root,"classroom"));
        var dialog=Dialog(this,"课堂材料 · "+course.Name,800,760);var p=new StackPanel {Margin=new Thickness(26)};
        dialog.Content=new ScrollViewer{Content=p};p.Children.Add(Text(lesson.Label,20,bold:true));p.Children.Add(Space(12));
        string transcript="";
        try{if(lesson.TranscriptRead)transcript=await content.ReadTranscriptAsync(course.Id,lesson.Id);}catch{transcript="本机文字文件缺失，请重新收集。";}
        p.Children.Add(new TextBox{Text=transcript.Length>0?transcript:"暂无语音文字",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=290,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        p.Children.Add(Space(16));p.Children.Add(Text("课件",17,bold:true));
        foreach(var file in lesson.Files)
        {
            var f=file;p.Children.Add(Space(8));
            p.Children.Add(Row(Text((f.OriginalFileName??"课件.pdf")+(f.PageCount is int pages?$" · {pages} 页":"")),Button("打开 / 下载",async()=>{
                string? path=null;try{path=content.ResolvePptPath(f);}catch{}
                if(path==null){await CloudAction(async(c,t)=>{var job=new CloudJob{FileKeys=new(){ClassroomModel.FileKey(f)}};await c.Materials(course,lesson,job,store.Save,t);},"课件已保存。");try{path=content.ResolvePptPath(lesson.Files.First(x=>ClassroomModel.FileKey(x)==ClassroomModel.FileKey(f)));}catch{}}
                if(path!=null)try{Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch{UiMessage.Show(dialog,"无法打开，请检查默认 PDF / PPT 程序。");}
            })));
        }
        p.Children.Add(Space(18));p.Children.Add(Row(Button("重新收集文字与课件清单",async()=>{if(cloudBusy)return;dialog.Close();lesson.FilesListed=false;lesson.TranscriptRead=false;await CloudAction(async(c,t)=>await c.Inspect(course,lesson,store.Save,t),"材料清单已更新。");}),Button("关闭",()=>dialog.Close())));dialog.ShowDialog();
    }
    void CloudVersions(CloudCourse course,CloudLesson lesson)
    {
        var dialog=Dialog(this,"课堂笔记与版本",740,640);var p=new StackPanel {Margin=new Thickness(26)};dialog.Content=new ScrollViewer{Content=p};
        p.Children.Add(Text(course.Name+" · "+lesson.Date,22,bold:true));
        foreach(var note in ClassroomModel.Versions(store.Data,course.Id,lesson.Id))
        {p.Children.Add(Space(12));p.Children.Add(Card(Stack(Text(note.Title,16,bold:true),Space(6),Text(note.ClassroomSource!.Generated.ToString("yyyy-MM-dd HH:mm"),12,Muted),Space(10),Row(Button("阅读 / 编辑",()=>{var current=store.Data.Items.FirstOrDefault(n=>n.Id==note.Id);if(current!=null)EditKnowledgeNote(current);}),Button("待办候选",()=>CloudCandidates(note))))));}
        p.Children.Add(Space(14));p.Children.Add(Button("关闭",()=>dialog.Close()));dialog.ShowDialog();
    }
    partial void CloudCandidates(Item note);
    void CloudSettings()
    {
        var dialog=Dialog(this,"课堂笔记 · AI 设置",650,720);var p=new StackPanel {Margin=new Thickness(26)};dialog.Content=new ScrollViewer{Content=p};
        CloudApiSettings? settings=null;string warning="";try{settings=CloudConfig.Read();}catch{warning="原配置无法解密，请重新填写。";}
        var endpoint=new TextBox{Text=settings?.Endpoint??"https://api.deepseek.com/chat/completions"};var key=new PasswordBox{Password=settings?.Key??"",Padding=new Thickness(10),MinHeight=38};var model=new TextBox{Text=settings?.Model??"deepseek-chat"};
        var risk=new CheckBox{Content="若使用远程 HTTP，我确认密钥与课堂文字可能被窃听",IsChecked=settings?.HttpConfirmed??false};
        var status=Text(warning,12,Muted);
        p.Children.Add(Text("配置课堂笔记 AI",23,bold:true));p.Children.Add(Space(12));p.Children.Add(Text("生成时会将所选课节的语音文字、所选课件文字及课程信息发送给此 API。按服务商规则计费；配置用 Windows 加密保存，不进入备份。",13,Muted));p.Children.Add(Space(18));
        p.Children.Add(Field("完整 Chat Completions 地址（含 /chat/completions）",endpoint));p.Children.Add(Field("API 密钥",key));p.Children.Add(Field("模型名称",model));p.Children.Add(risk);p.Children.Add(Space(12));p.Children.Add(status);p.Children.Add(Space(16));
        p.Children.Add(Row(Button("保存",()=>{try{CloudConfig.Save(new(){Endpoint=endpoint.Text.Trim(),Key=key.Password.Trim(),Model=model.Text.Trim(),HttpConfirmed=risk.IsChecked==true});dialog.Close();}catch(Exception ex){status.Text=CloudError(ex);}},true),Button("清除配置",()=>{if(cloudBusy){status.Text="请先暂停生成任务。";return;}try{CloudConfig.Clear();key.Clear();status.Text="已清除配置，已有笔记保留。";}catch(Exception ex){status.Text=CloudError(ex);}}),Button("取消",()=>dialog.Close())));
        dialog.ShowDialog();key.Clear();
    }
}
