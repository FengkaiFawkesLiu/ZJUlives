using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZJUSS.Core.Classroom;
using ZJUSS.Core.NoteApi;

namespace Shike;
public static class ClassroomQueueTests
{
    static readonly NoteApiConfigSnapshot Config=new("https://example.test/chat/completions","fixture","fixture",false);
    public static void Run(Action<string,Action> test,string temp)
    {
        void Check(bool yes){if(!yes)throw new Exception("Queue assertion failed");}
        (Store,CloudCourse) Fixture(string name,int lessons=1)
        {
            var store=new Store(Path.Combine(temp,name));var course=new CloudCourse{Id="course",Name="合成测试课程",Term="2026-2027-1"};
            for(int i=0;i<lessons;i++)course.Lessons.Add(new(){Id="l"+i,Date="2026-10-24",Title="测试课节"});
            store.Data.Classroom.Courses.Add(course);ClassroomQueue.Enqueue(store.Data,course,course.Lessons.Select(l=>l.Id));store.Save();return(store,course);
        }
        test("Queue uses only explicitly selected courseware and attaches it to Shike note",()=>{
            var(store,course)=Fixture("queue-selection");var source=new FakeSource{FileCount=2};var api=new FakeApi();
            using var collector=new ClassroomCollector(store.Root,source,new FakeExtractor());
            new ClassroomQueue(store,collector,api).Run(Config,(c,l,j,t)=>{j.FileKeys=new(){"f1"};return Task.FromResult(true);},(c,l,m,t)=>throw new Exception("Unexpected missing material"),_=>{},default).GetAwaiter().GetResult();
            Check(source.Downloads.SequenceEqual(new[]{"f1"})&&api.Requests.Single().CoursewareText!.Contains("selected text"));
            Check(store.Data.Items.Single().Attachments.Count==1&&store.Data.Classroom.Jobs.Single().State=="已完成");
            var saved=new Store(store.Root);Check(saved.Data.Items.Single().ClassroomSource!.Candidates.Count==1);
        });
        test("Missing material pauses whole batch until user chooses, then resumes without duplicates",()=>{
            var(store,course)=Fixture("queue-missing",2);var source=new FakeSource{FileCount=0};var api=new FakeApi();
            using var collector=new ClassroomCollector(store.Root,source,new FakeExtractor());
            var queue=new ClassroomQueue(store,collector,api);
            queue.Run(Config,(c,l,j,t)=>throw new Exception("Unexpected file prompt"),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Pause),_=>{},default).GetAwaiter().GetResult();
            Check(api.Requests.Count==0&&store.Data.Classroom.Jobs[0].State=="已暂停"&&store.Data.Classroom.Jobs[1].State=="等待中");
            var restart=new Store(store.Root);
            new ClassroomQueue(restart,collector,api).Run(Config,(c,l,j,t)=>Task.FromResult(true),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Continue),_=>{},default).GetAwaiter().GetResult();
            Check(api.Requests.Count==2&&restart.Data.Items.Count==2);
            restart.Data.Classroom.Jobs[0].State="进行中";restart.Save();
            new ClassroomQueue(restart,collector,api).Run(Config,(c,l,j,t)=>Task.FromResult(true),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Continue),_=>{},default).GetAwaiter().GetResult();
            Check(api.Requests.Count==2&&restart.Data.Items.Count==2);
        });
        test("Failed lesson continues remaining queue and retry does not overwrite edited note",()=>{
            var(store,course)=Fixture("queue-failure",2);var api=new FakeApi{FailFirst=true};using var collector=new ClassroomCollector(store.Root,new FakeSource(),new FakeExtractor());
            var queue=new ClassroomQueue(store,collector,api);
            queue.Run(Config,(c,l,j,t)=>Task.FromResult(true),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Continue),_=>{},default).GetAwaiter().GetResult();
            Check(store.Data.Classroom.Jobs[0].State=="失败"&&store.Data.Classroom.Jobs[1].State=="已完成");
            var note=store.Data.Items.Single();note.NoteBody="用户修改";ClassroomQueue.Enqueue(store.Data,course,new[]{"l1"});
            queue.Run(Config,(c,l,j,t)=>Task.FromResult(true),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Continue),_=>{},default).GetAwaiter().GetResult();
            Check(note.NoteBody=="用户修改"&&ClassroomModel.Versions(store.Data,course.Id,"l1").Count()==2);
        });
        test("Cancellation saves paused job and never writes cancelled API result",()=>{
            var(store,course)=Fixture("queue-cancel");using var cancel=new CancellationTokenSource();var api=new FakeApi{OnCall=()=>cancel.Cancel()};
            using var collector=new ClassroomCollector(store.Root,new FakeSource(),new FakeExtractor());
            new ClassroomQueue(store,collector,api).Run(Config,(c,l,j,t)=>Task.FromResult(true),(c,l,m,t)=>Task.FromResult(CloudMissingChoice.Continue),_=>{},cancel.Token).GetAwaiter().GetResult();
            var restart=new Store(store.Root);Check(restart.Data.Items.Count==0&&restart.Data.Classroom.Jobs.Single().State=="已暂停");
        });
    }
    sealed class FakeExtractor:ICoursewareTextExtractor
    {
        public Task<CoursewareTextResult> ExtractAsync(string path,string? originalFileName=null,CancellationToken cancellationToken=default)=>Task.FromResult(new CoursewareTextResult{Status=CoursewareTextStatus.Available,Text="selected text",Format="pdf"});
    }
    sealed class FakeApi:INoteApiClient
    {
        public bool FailFirst;public Action? OnCall;public List<NoteApiRequest> Requests=new();
        public Task<NoteApiResult> GenerateAsync(NoteApiConfigSnapshot config,NoteApiRequest request,CancellationToken cancellationToken=default)
        {
            Requests.Add(request);OnCall?.Invoke();
            if(FailFirst&&Requests.Count==1)return Task.FromResult(NoteApiResult.Fail(NoteApiFailureKind.ServerError,"合成失败"));
            return Task.FromResult(NoteApiResult.Ok("## 课堂要点\n合成测试内容\n## 待办事项\n预习\n```json\n{\"candidates\":[{\"text\":\"预习\",\"date\":null}]}\n```"));
        }
    }
    sealed class FakeSource:IClassroomSource
    {
        public int FileCount=1;public List<string> Downloads=new();
        public Task<ClassroomSourceResult<IReadOnlyList<ClassroomCourse>>> GetCoursesAsync(CancellationToken cancellationToken=default)=>Task.FromResult(ClassroomSourceResult<IReadOnlyList<ClassroomCourse>>.Ok(Array.Empty<ClassroomCourse>()));
        public Task<ClassroomSourceResult<IReadOnlyList<ClassroomLesson>>> GetLessonsAsync(string course,CancellationToken cancellationToken=default)=>Task.FromResult(ClassroomSourceResult<IReadOnlyList<ClassroomLesson>>.Ok(Array.Empty<ClassroomLesson>()));
        public Task<ClassroomSourceResult<string>> GetTranscriptAsync(string course,string lesson,CancellationToken cancellationToken=default)=>Task.FromResult(ClassroomSourceResult<string>.Ok("synthetic transcript"));
        public Task<ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>> GetPptInfoAsync(string course,string lesson,CancellationToken cancellationToken=default)=>Task.FromResult(ClassroomSourceResult<IReadOnlyList<ClassroomPptFile>>.Ok(Enumerable.Range(0,FileCount).Select(i=>new ClassroomPptFile{FileId="f"+i,OriginalFileName="材料"+i+".pdf"}).ToList()));
        public Task<ClassroomSourceResult<ClassroomPptDownload>> DownloadPptAsync(string course,string lesson,ClassroomPptFile file,CancellationToken cancellationToken=default)
        {Downloads.Add(file.FileId!);return Task.FromResult(ClassroomSourceResult<ClassroomPptDownload>.Ok(new(){OriginalFileName=file.OriginalFileName!,Content=new MemoryStream(new byte[]{1,2,3})}));}
    }
}
