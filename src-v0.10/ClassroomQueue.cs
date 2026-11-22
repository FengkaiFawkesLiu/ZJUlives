using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.NoteApi;
using ZJUSS.Core.Notes;

namespace Shike;

public enum CloudMissingChoice { Pause, Continue, Skip }
public sealed class CloudStorageException : IOException
{
    public CloudStorageException(Exception inner) : base("课堂任务未能保存，队列已停止。",inner) { }
}
public sealed class ClassroomQueue
{
    readonly Store store;
    readonly ClassroomCollector collector;
    readonly INoteApiClient api;
    public ClassroomQueue(Store store,ClassroomCollector collector,INoteApiClient api)
    {this.store=store;this.collector=collector;this.api=api;}

    public static int Enqueue(AppData data,CloudCourse course,IEnumerable<string> lessons)
    {
        int count=0;
        foreach(string id in lessons.Distinct())
        {
            if(!course.Lessons.Any(l=>l.Id==id&&!l.Removed) || data.Classroom.Jobs.Any(j=>j.CourseId==course.Id&&j.LessonId==id&&ClassroomModel.Pending(j)))continue;
            data.Classroom.Jobs.Add(new(){CourseId=course.Id,LessonId=id});count++;
        }
        return count;
    }

    public async Task Run(NoteApiConfigSnapshot config,
        Func<CloudCourse,CloudLesson,CloudJob,CancellationToken,Task<bool>> chooseFiles,
        Func<CloudCourse,CloudLesson,CloudMaterials,CancellationToken,Task<CloudMissingChoice>> missing,
        Action<string> progress,CancellationToken token)
    {
        var data=store.Data;
        void Checkpoint()
        {
            token.ThrowIfCancellationRequested();
            if(!ReferenceEquals(data,store.Data))throw new OperationCanceledException("本机数据已替换。");
            try{store.Save();}catch(Exception ex){throw new CloudStorageException(ex);}
        }
        foreach(var job in data.Classroom.Jobs.Where(ClassroomModel.Pending).ToList())
        {
            token.ThrowIfCancellationRequested();
            var existing=data.Items.FirstOrDefault(i=>i.ClassroomSource?.JobId==job.Id);
            if(existing!=null){job.NoteId=existing.Id;job.State="已完成";job.Detail="";Checkpoint();continue;}
            try
            {
                job.State="进行中";job.Detail="正在收集材料";Checkpoint();
                var course=data.Classroom.Courses.SingleOrDefault(c=>c.Id==job.CourseId)??throw new InvalidDataException("课程已不在本机清单中。");
                var lesson=course.Lessons.SingleOrDefault(l=>l.Id==job.LessonId)??throw new InvalidDataException("课节已不在本机清单中。");
                if(lesson.Removed)throw new InvalidDataException("平台已移除此课节，请查看已有本机材料。");
                progress(course.Name+" · "+lesson.Label+"：收集材料");
                await collector.Inspect(course,lesson,Checkpoint,token);token.ThrowIfCancellationRequested();
                if(!job.FilesConfirmed)
                {
                    if(lesson.Files.Count>1)
                    {
                        job.State="待选择材料";job.Detail="请先选择本节课使用的课件";Checkpoint();progress(job.Detail);
                        if(!await chooseFiles(course,lesson,job,token))throw new OperationCanceledException();
                        token.ThrowIfCancellationRequested();
                    }
                    else job.FileKeys=lesson.Files.Select(ClassroomModel.FileKey).ToList();
                    job.FilesConfirmed=true;Checkpoint();
                }
                job.State="进行中";job.Detail="正在下载并提取所选课件";Checkpoint();progress(course.Name+"："+job.Detail);
                var materials=await collector.Materials(course,lesson,job,Checkpoint,token);token.ThrowIfCancellationRequested();
                if(materials.Missing.Count>0)
                {
                    job.State="待选择材料";job.Detail=string.Join("；",materials.Missing);Checkpoint();progress(job.Detail);
                    var decision=await missing(course,lesson,materials,token);token.ThrowIfCancellationRequested();
                    if(decision==CloudMissingChoice.Skip){job.State="已跳过";job.Detail="使用者跳过材料不完整的课节";Checkpoint();continue;}
                    if(decision!=CloudMissingChoice.Continue)throw new OperationCanceledException();
                }
                if(string.IsNullOrWhiteSpace(materials.Text)&&string.IsNullOrWhiteSpace(materials.Transcript))throw new InvalidDataException("没有可用文字，无法生成笔记。");
                job.State="进行中";job.Detail="正在生成笔记";Checkpoint();progress(course.Name+" · "+lesson.Label+"：生成笔记");
                var response=await api.GenerateAsync(config,new(){CourseName=course.Name,Term=course.Term,LessonDateText=lesson.Date,SessionText=lesson.Session,CoursewareText=materials.Text,TranscriptText=materials.Transcript,Instructions=NotePrompt.Instructions},token);
                token.ThrowIfCancellationRequested();
                if(!response.Success)throw new InvalidOperationException(response.Detail??"AI 生成失败，请检查配置后重试。");
                var parsed=NoteParser.Parse(response.Content,new(){CourseName=course.Name,Term=course.Term,LessonDateText=lesson.Date,SessionText=lesson.Session,
                    MatchKind=course.CourseKey==null?ClassroomMatchKind.Unmatched:ClassroomMatchKind.Matched,UsedCoursewareText=!string.IsNullOrWhiteSpace(materials.Text),UsedTranscript=!string.IsNullOrWhiteSpace(materials.Transcript),CoursewarePresent=materials.Files.Count>0,
                    CoursewareTextFromOcr=materials.Ocr,CoursewareTextStatus=!string.IsNullOrWhiteSpace(materials.Text)?CoursewareTextStatus.Available:CoursewareTextStatus.NotRead,GeneratedAt=DateTimeOffset.Now});
                if(!parsed.Success)throw new InvalidDataException(parsed.FailureDetail??"笔记格式无法识别。");
                string before=JsonSerializer.Serialize(data);
                try
                {
                    var attachments=new List<Attachment>();
                    foreach(var file in materials.Files)
                    {var attachment=store.AddFile(collector.Content.ResolvePptPath(file));attachment.Name=file.OriginalFileName??"课件.pdf";attachments.Add(attachment);}
                    var note=ClassroomModel.CreateNote(data,course,lesson,job,parsed,attachments);
                    job.NoteId=note.Id;job.State="已完成";job.Detail=parsed.CandidateWarning??"已写入课堂笔记库";Checkpoint();
                }
                catch(Exception ex)
                {store.Data=JsonSerializer.Deserialize<AppData>(before)!;throw new CloudStorageException(ex);}
            }
            catch(CloudStorageException){throw;}
            catch(OperationCanceledException)
            {
                job.State="已暂停";job.Detail="可继续本次任务；已完成材料保留";
                // Cancellation must still persist the paused state before the app returns to idle.
                if(ReferenceEquals(data,store.Data))try{store.Save();}catch(Exception ex){throw new CloudStorageException(ex);}
                return;
            }
            catch(Exception ex)
            {
                job.State="失败";job.Detail=ex is InvalidOperationException or InvalidDataException?ex.Message:"材料读取或处理失败，请重试。";
                Checkpoint();progress(courseName(data,job)+"：本节失败，继续其他课节。");
            }
        }
    }
    static string courseName(AppData data,CloudJob job)=>data.Classroom.Courses.FirstOrDefault(c=>c.Id==job.CourseId)?.Name??"课堂任务";
}
