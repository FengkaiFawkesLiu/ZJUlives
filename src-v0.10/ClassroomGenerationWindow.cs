using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ZJUSS.Core.NoteApi;
using static Shike.Theme;

namespace Shike;
public sealed partial class MainWindow
{
    void CloudResumeNotice()
    {
        int count=store.Data.Classroom.Jobs.Count(ClassroomModel.Pending);
        if(count==0)return;
        var dialog=Dialog(this,"继续课堂笔记整理",580,360);
        dialog.Content=new Border{Padding=new Thickness(26),Child=Stack(Text($"有 {count} 节课尚未整理完成",22,bold:true),Space(14),Text("上次进度已保存。可以进入智云课堂继续任务，已生成笔记不会重复创建；继续调用 AI 可能产生费用。",13,Muted),Space(20),Row(Button("查看未完成任务",()=>{dialog.Close();SwitchView("智云课堂");},true),Button("稍后处理",()=>dialog.Close())))};
        dialog.ShowDialog();
    }
    partial void AddCloudGenerateButton(Panel panel,CloudCourse course)
    {
        var button=Button("一键收集并生成笔记",()=>{
            if(cloudBusy)return;
            if(cloudSelected.Count==0){UiMessage.Show(this,"请先勾选要整理的课节。");return;}
            var ids=cloudSelected.ToList();
            if(ids.Any(id=>ClassroomModel.Versions(store.Data,course.Id,id).Any()) && UiMessage.Show(this,"选择中包含已有笔记的课节。继续后会生成新版本，保留现有笔记和手动修改。","生成新版本",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            if(Commit(()=>ClassroomQueue.Enqueue(store.Data,course,ids)))_ = RunCloudQueue();
        },true);button.IsEnabled=!cloudBusy;panel.Children.Add(button);
    }
    partial void RenderCloudQueue(StackPanel panel)
    {
        var jobs=store.Data.Classroom.Jobs;
        if(jobs.Count==0)return;
        int pending=jobs.Count(ClassroomModel.Pending),failed=jobs.Count(j=>j.State=="失败"),completed=jobs.Count(j=>j.State=="已完成");
        var content=Stack(Text($"整理任务 · 已完成 {completed} · 待继续 {pending} · 失败 {failed}",16,bold:true),Space(8),
            Text(cloudBusy?"任务正在运行。多份课件和材料缺失时会等待你的选择。":"上次任务进度已保存；点击继续后才会调用 AI。",12,Muted));
        var actions=Row();
        if(!cloudBusy)
        {
            if(pending>0)actions.Children.Add(Button("继续未完成任务",()=>_ = RunCloudQueue(),true));
            if(failed>0)actions.Children.Add(Button("重试失败课节",()=>{if(Commit(()=>{foreach(var j in store.Data.Classroom.Jobs.Where(j=>j.State=="失败")){j.State="等待中";j.Detail="";}}))_ = RunCloudQueue();}));
            if(pending>0)actions.Children.Add(Button("取消未完成任务",()=>{if(UiMessage.Show(this,"取消队列中尚未完成的任务？已生成笔记和已下载材料会保留。","取消任务",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)Commit(()=>{foreach(var j in jobs.Where(ClassroomModel.Pending)){j.State="已取消";j.Detail="";}});}));
            if(jobs.Any(j=>!ClassroomModel.Pending(j)))actions.Children.Add(Button("清除已结束记录",()=>Commit(()=>jobs.RemoveAll(j=>!ClassroomModel.Pending(j)))));
        }
        if(actions.Children.Count>0){content.Children.Add(Space(10));content.Children.Add(actions);}
        foreach(var job in jobs.Where(j=>ClassroomModel.Pending(j)||j.State=="失败").TakeLast(8))
        {var course=store.Data.Classroom.Courses.FirstOrDefault(c=>c.Id==job.CourseId);var lesson=course?.Lessons.FirstOrDefault(l=>l.Id==job.LessonId);content.Children.Add(Space(8));content.Children.Add(Text($"{course?.Name??"课程"} · {lesson?.Label??"课节"} · {job.State} · {job.Detail}",12,Muted));}
        panel.Children.Add(Card(content));panel.Children.Add(Space(14));
    }
    async Task RunCloudQueue()
    {
        if(cloudBusy)return;
        CloudApiSettings? settings;
        try{settings=CloudConfig.Read();if(settings==null){CloudSettings();settings=CloudConfig.Read();}if(settings==null){cloudStatus="任务已保存，配置 AI 后可继续。";RenderCloudIfVisible();return;}}
        catch(Exception ex){UiMessage.Show(this,CloudError(ex));return;}
        await CloudAction(async(c,t)=>{
            using var handler=new HttpClientHandler{AllowAutoRedirect=false};using var api=new NoteApiClient(handler);
            await new ClassroomQueue(store,c,api).Run(settings.Snapshot(),ChooseCloudFiles,ChooseCloudMissing,message=>{cloudStatus=message;RenderCloudIfVisible();},t);
        },"本轮整理结束。已生成笔记可在课堂笔记库查看，未完成任务可继续。");
    }
    Task<bool> ChooseCloudFiles(CloudCourse course,CloudLesson lesson,CloudJob job,CancellationToken token)
    {
        var dialog=Dialog(this,"选择本节课使用的课件",700,650);var p=new StackPanel{Margin=new Thickness(26)};dialog.Content=new ScrollViewer{Content=p};
        p.Children.Add(Text(course.Name+" · "+lesson.Date,22,bold:true));p.Children.Add(Space(10));p.Children.Add(Text("本节有多份课件，请明确勾选用于生成笔记的文件。所选文件会保存为笔记附件。",13,Muted));p.Children.Add(Space(16));
        var choices=lesson.Files.Select(f=>(File:f,Check:new CheckBox{Content=f.OriginalFileName??"课件.pdf",IsChecked=job.FileKeys.Contains(ClassroomModel.FileKey(f)),Margin=new Thickness(0,8,0,8)})).ToList();
        foreach(var pair in choices)p.Children.Add(pair.Check);
        bool confirmed=false;
        p.Children.Add(Space(18));p.Children.Add(Row(Button("使用所选课件",()=>{job.FileKeys=choices.Where(x=>x.Check.IsChecked==true).Select(x=>ClassroomModel.FileKey(x.File)).ToList();confirmed=true;dialog.Close();},true),Button("暂停批次",()=>dialog.Close())));
        using var registration=token.Register(()=>Dispatcher.BeginInvoke(new Action(()=>dialog.Close())));
        dialog.ShowDialog();return Task.FromResult(confirmed);
    }
    Task<CloudMissingChoice> ChooseCloudMissing(CloudCourse course,CloudLesson lesson,CloudMaterials materials,CancellationToken token)
    {
        var dialog=Dialog(this,"课堂材料缺失 · 批次已暂停",700,560);var p=Stack(Text(course.Name+" · "+lesson.Date,22,bold:true),Space(14),Text(string.Join("\n",materials.Missing),14),Space(14),Text("请选择本节如何处理。当前批次会等待你的选择。",13,Muted),Space(18));
        var choice=CloudMissingChoice.Pause;
        var available=Button("使用现有文字继续",()=>{choice=CloudMissingChoice.Continue;dialog.Close();},true);
        available.IsEnabled=!string.IsNullOrWhiteSpace(materials.Text)||!string.IsNullOrWhiteSpace(materials.Transcript);
        p.Children.Add(Row(available,Button("跳过本节，继续批次",()=>{choice=CloudMissingChoice.Skip;dialog.Close();}),Button("保持暂停",()=>dialog.Close())));
        dialog.Content=new Border{Padding=new Thickness(26),Child=p};
        using var registration=token.Register(()=>Dispatcher.BeginInvoke(new Action(()=>dialog.Close())));
        dialog.ShowDialog();return Task.FromResult(choice);
    }
    partial void CloudCandidates(Item note)
    {
        var current=store.Data.Items.FirstOrDefault(n=>n.Id==note.Id);
        if(current==null){UiMessage.Show(this,"这篇笔记已删除。");return;}
        note=current;
        var dialog=Dialog(this,"课堂待办候选",760,720);var p=new StackPanel{Margin=new Thickness(26)};dialog.Content=new ScrollViewer{Content=p};
        p.Children.Add(Text(note.Title,21,bold:true));p.Children.Add(Space(10));p.Children.Add(Text("候选来自 AI，请核对内容和日期，确认后才加入拾课待办。",13,Muted));
        if(note.ClassroomSource==null||note.ClassroomSource.Candidates.Count==0)p.Children.Add(Text("本篇没有待办候选。",13,Muted));
        foreach(var candidate in note.ClassroomSource?.Candidates??new List<CloudCandidate>())
        {
            var text=new TextBox{Text=candidate.Text,TextWrapping=TextWrapping.Wrap};var date=new DatePicker{SelectedDate=candidate.Date};
            var add=Button(candidate.TodoId==null?"确认加入待办":"已加入待办",()=>{});add.IsEnabled=candidate.TodoId==null;
            add.Click+=(_,_)=>{
                if(string.IsNullOrWhiteSpace(text.Text)||text.Text.Trim().Length>300){UiMessage.Show(dialog,"待办标题需为 1–300 字。");return;}
                // Re-generated versions can repeat the same teacher reminder: show an existing equivalent todo first.
                bool duplicate=store.Data.Items.Any(i=>i.Kind=="待办"&&i.Title==text.Text.Trim()&&(date.SelectedDate==null?i.AssistantNoDue:!i.AssistantNoDue&&i.Start.Date==date.SelectedDate.Value.Date));
                if(duplicate&&UiMessage.Show(dialog,"已有同名同日期的待办，仍然添加？","可能重复的待办",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
                if(Commit(()=>ClassroomModel.ConfirmCandidate(store.Data,note,candidate,text.Text.Trim(),date.SelectedDate))){add.Content="已加入待办";add.IsEnabled=false;}
            };
            p.Children.Add(Space(14));p.Children.Add(Card(Stack(Field("候选内容",text),Field("日期（可留空）",date),add)));
        }
        p.Children.Add(Space(16));p.Children.Add(Button("关闭",()=>dialog.Close()));dialog.ShowDialog();
    }
}
