using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Shike;

public static class Tests
{
    public static void Seed(Store store)
    {
        DateTime day = DateTime.Today; var monday = Engine.Monday(day);
        store.Data.Items.AddRange(new[]
        {
            new Item { Title = "微积分 · 课堂学习", Kind = "课程", Start = day.AddHours(8), End = day.AddHours(9.5), Location = "东 1 教学楼 · 201", Note = "测试样例，非真实学校数据。复习极限与连续。" },
            new Item { Title = "社团面试", Kind = "活动", Start = day.AddHours(15), End = day.AddHours(16), Location = "学生活动中心 · 302", Note = "测试样例，非真实学校数据。\n带好报名表，准备一分钟自我介绍。", Reminders = new() { 60, 15 } },
            new Item { Title = "整理面试材料", Kind = "待办", Start = day.AddHours(13), End = day.AddHours(13).AddMinutes(1) },
            new Item { Title = "读完本周参考文献", Kind = "待办", Start = day.AddHours(21), End = day.AddHours(21).AddMinutes(1) },
            new Item { Title = "课程复习 · 知识清单", Kind = "笔记", Note = "测试样例。\n把课堂上的疑问记在这里，再逐个解决。\n1. 整理本周笔记\n2. 回顾例题\n3. 标出需要询问的问题" },
            new Item { Title = "线性代数 · 阶段考试", Kind = "考试", Start = day.AddDays(4).AddHours(9), End = day.AddDays(4).AddHours(11), Location = "西 2 教学楼 · 102" }
        });
        for (int d = 0; d < 5; d++)
            store.Data.Items.Add(new Item { Title = new[] { "程序设计", "大学物理", "线性代数", "英语交流", "学术写作" }[d], Kind = "课程", Start = monday.AddDays(d).AddHours(10), End = monday.AddDays(d).AddHours(11.5), Location = "东 2 · " + (201 + d) });
        store.Save();
    }
    public static int Run(string[] args)
    {
        var messages = new List<string>(); int failures = 0;
        void Test(string name, Action test) { try { test(); messages.Add("PASS " + name); } catch (Exception ex) { failures++; messages.Add("FAIL " + name + " :: " + ex.Message); } }
        void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
        DateTime day = new(2026, 9, 14);
        Item Event(string title = "课程") => new() { Title = title, Start = day.AddHours(9), End = day.AddHours(10) };
        SchoolRecord Record(string title = "课程", int hour = 9) => new() { Key = "c1", Title = title, Start = day.AddHours(hour), End = day.AddHours(hour + 1), Location = "东 1" };
        string temp = Path.Combine(Path.GetTempPath(), "Shike-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        Test("Versions share independent data directory", () => { var install = Path.Combine(temp, "install"); Assert(Program.DataDirectory(Path.Combine(install, "app-v0.10")) == Path.Combine(install, "data")); Assert(Program.DataDirectory(Path.Combine(install, "app-v0.11")) == Path.Combine(install, "data")); });
        Test("Weekly recurrence includes last day and skips exception", () => { var i = Event(); i.Repeat = "每周"; i.Until = day.AddDays(21); i.Excluded.Add(day.AddDays(7).ToString("yyyy-MM-dd")); var list = Engine.Between(new[] { i }, day, day.AddDays(28)).ToList(); Assert(list.Count == 3 && list[^1].Start.Date == day.AddDays(21)); });
        Test("Overnight event appears on both days", () => { var i = Event(); i.Start = day.AddHours(23); i.End = day.AddDays(1).AddHours(1); Assert(Engine.Between(new[] { i }, day.AddDays(1), day.AddDays(2)).Count() == 1); });
        Test("Touching endpoints do not conflict", () => { var a = Event(); var b = Event(); b.Start = a.End; b.End = b.Start.AddHours(1); Assert(!Engine.Overlaps(new(a, a.Start, a.End), new(b, b.Start, b.End))); });
        Test("Detaching one occurrence preserves other weeks", () => { var data = new AppData(); var i = Event(); i.Repeat = "每周"; i.Until = day.AddDays(14); data.Items.Add(i); var detached = Engine.Detach(data, new(i, i.Start.AddDays(7), i.End.AddDays(7))); detached.Start = detached.Start.AddHours(2); detached.End = detached.End.AddHours(2); Assert(Engine.Between(data.Items, day, day.AddDays(21)).Count() == 3 && i.Start.Hour == 9); });
        Test("School refresh preserves personal notes and attachments", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); data.Items[0].Note = "我的笔记"; data.Items[0].Attachments.Add(new() { Name = "a", File = "a.txt" }); SchoolMerge.Apply(data, "term", new() { Record("更名", 10) }, true); Assert(data.Items[0].Title == "更名" && data.Items[0].Note == "我的笔记" && data.Items[0].Attachments.Count == 1); });
        Test("Local schedule override triggers update conflict", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); var i = data.Items[0]; i.Start = day.AddHours(8); i.LocalOverride = true; var r = SchoolMerge.Apply(data, "term", new() { Record("课程", 11) }, true); Assert(r.Conflicts == 1 && i.Start.Hour == 8 && i.Pending?.Start.Hour == 11); });
        Test("Keeping local revision acknowledges school baseline", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); var i = data.Items[0]; i.LocalOverride = true; i.Start = day.AddHours(8); SchoolMerge.Apply(data, "term", new() { Record("课程", 11) }, true); SchoolMerge.Resolve(i, false); var r = SchoolMerge.Apply(data, "term", new() { Record("课程", 11) }, true); Assert(r.Conflicts == 0 && !i.HasConflict && i.Start.Hour == 8); });
        Test("Accepting school clears override but keeps note", () => { var i = Event(); i.Note = "keep"; i.LocalOverride = true; i.Pending = Record("new", 12); SchoolMerge.Resolve(i, true); Assert(i.Start.Hour == 12 && !i.LocalOverride && i.Note == "keep"); });
        Test("Partial sync never removes missing entries", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); SchoolMerge.Apply(data, "term", new(), false); Assert(!data.Items[0].Cancelled); });
        Test("Authoritative removal respects local edit", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); data.Items[0].LocalOverride = true; SchoolMerge.Apply(data, "term", new(), true); Assert(data.Items[0].PendingRemoval && !data.Items[0].Cancelled); });
        Test("School event can reappear after removal", () => { var data = new AppData(); SchoolMerge.Apply(data, "term", new() { Record() }, true); SchoolMerge.Apply(data, "term", new(), true); Assert(data.Items[0].Cancelled); SchoolMerge.Apply(data, "term", new() { Record() }, true); Assert(!data.Items[0].Cancelled); });
        Test("Sync is idempotent and scoped by term", () => { var data = new AppData(); SchoolMerge.Apply(data, "term1", new() { Record() }, true); SchoolMerge.Apply(data, "term1", new() { Record() }, true); SchoolMerge.Apply(data, "term2", new() { Record() }, true); Assert(data.Items.Count == 2); });
        Test("Reminders are due once and honor disabled course settings", () => { var data = new AppData(); var i = Event(); i.Reminders = new() { 60 }; data.Items.Add(i); var first = Engine.Due(data, day.AddHours(8)); Assert(first.Count == 1); data.Fired.Add(first[0].Key); Assert(Engine.Due(data, day.AddHours(8)).Count == 0); i.Kind = "课程"; i.Reminders = null; Assert(Engine.Due(data, day.AddHours(9)).Count == 0); });
        Test("Completed and cancelled items never remind", () => { var data = new AppData(); var i = Event(); i.Reminders = new() { 0 }; i.Done = true; data.Items.Add(i); Assert(Engine.Due(data, i.Start).Count == 0); i.Done = false; i.Cancelled = true; Assert(Engine.Due(data, i.Start).Count == 0); });
        Test("Negative reminder rejected", () => { try { Engine.ParseReminders("-1"); throw new Exception("Accepted negative reminder"); } catch (InvalidDataException) { } });
        Test("Atomic save and restart persistence", () => { var s = new Store(Path.Combine(temp, "persist")); s.Data.Items.Add(Event("面试")); s.Save(); s.Data.Items[0].Note = "本地笔记"; s.Save(); var fresh = new Store(s.Root); Assert(fresh.Data.Items[0].Note == "本地笔记" && File.Exists(Path.Combine(s.Root, "data.json.bak"))); });
        Test("Full backup restores attachment bytes", () => { var s = new Store(Path.Combine(temp, "backup")); var i = Event(); var source = Path.Combine(temp, "材料.txt"); File.WriteAllText(source, "面试准备材料"); i.Attachments.Add(s.AddFile(source)); s.Data.Items.Add(i); var path = Path.Combine(temp, "backup.zip"); s.Backup(path); s.Data.Items.Clear(); s.Save(); s.Restore(path); Assert(s.Data.Items.Count == 1 && File.ReadAllText(Path.Combine(s.Files, s.Data.Items[0].Attachments[0].File)) == "面试准备材料"); });
        Test("Corrupt backup leaves current data intact", () => { var s = new Store(Path.Combine(temp, "bad")); s.Data.Items.Add(Event("保留")); s.Save(); var path = Path.Combine(temp, "bad.zip"); using (var z = ZipFile.Open(path, ZipArchiveMode.Create)) { using var w = new StreamWriter(z.CreateEntry("data.json").Open()); w.Write("{\"Version\":1,\"Items\":["); } try { s.Restore(path); throw new Exception("Invalid archive accepted"); } catch (JsonException) { } Assert(s.Data.Items.Count == 1 && s.Data.Items[0].Title == "保留"); });
        Test("Malicious attachment path rejected", () => { var data = new AppData(); var i = Event(); i.Attachments.Add(new() { Name = "bad", File = "../escape" }); data.Items.Add(i); try { Engine.Validate(data); throw new Exception("Path accepted"); } catch (InvalidDataException) { } });
        Test("School parser respects weekdays, halves and parity", () => { var s = new Settings { TermMonday = day, Weeks = 16, SecondHalfWeek = 9 }; string courses = """{"kbList":[{"kcmc":"数学","xqj":"1","djj":"1","skcd":"2","xxq":"秋","dsz":"0","kch_id":"math"}]}"""; var b = SchoolParser.Parse(courses, "{\"items\":[]}", s); Assert(b.Courses.Count == 4 && b.Courses[0].Start == day.AddHours(8)); });
        Test("Exam dates and seating parsed without guessing missing dates", () => { var s = new Settings(); var b = SchoolParser.Parse("{\"kbList\":[]}", """{"items":[{"kcmc":"数学","xkkh":"m1","kssj":"2026-12-20(09:00-11:00)","jsmc":"东1","zwxh":"15"},{"kcmc":"未安排","xkkh":"m2","kssj":"待定"}]}""", s); Assert(b.Exams.Count == 1 && b.Warnings.Count == 1 && b.Exams[0].Location.Contains("15")); });
        Test("Unexpected school response fails before merge", () => { try { SchoolParser.Parse("{\"error\":true}", "{}", new()); throw new Exception("Invalid response accepted"); } catch (InvalidOperationException) { } });
        UpgradeTests.Run(Test, temp);
        CampusTests.Run(Test, temp);
        PeriodTests.Run(Test, temp);
        NotesTests.Run(Test, temp); ObsidianTests.Run(Test, temp);
        NoteToolsTests.Run(Test);
        AcademicLogoutTests.Run(Test);
        CampusAssistantTests.Run(Test);
        var report = args.Length > 1 ? args[^1] : Path.Combine(temp, "test-results.txt");
        messages.Add($"\n{messages.Count - failures}/{messages.Count} passed. Temporary fixtures: {temp}");
        File.WriteAllLines(report, messages); return failures == 0 ? 0 : 1;
    }
}

