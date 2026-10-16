using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shike;
public static class PeriodTests
{
    public static void Run(Action<string, Action> test, string temp)
    {
        void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
        test("All 13 official periods match downloaded current Celechron config", () => {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "celechron-current.json")));
            var value = string.Join(';', doc.RootElement.GetProperty("sessionTime").EnumerateArray().Skip(1).Take(13).Select(a => a[0].GetString() + "-" + a[1].GetString())); Assert(value == PeriodSchedule.Official);
        });
        test("Parser uses official boundaries for every class period", () => {
            var times = SchoolParser.Periods(PeriodSchedule.Official);
            for (int n = 1; n <= 13; n++) {
                var json = "{\"kbList\":[{\"kcmc\":\"测试\",\"xqj\":\"1\",\"djj\":\"" + n + "\",\"skcd\":\"1\"}]}";
                var batch = SchoolParser.Parse(json, "{\"items\":[]}", new Settings { Weeks = 1, TermMonday = new DateTime(2026, 9, 14) });
                Assert(batch.Courses.Single().Start.TimeOfDay == times[n - 1].Start && batch.Courses.Single().End.TimeOfDay == times[n - 1].End);
            }
        });
        test("Cached correction preserves holiday date, notes and deletion; is idempotent", () => {
            var data = new AppData(); data.Settings.Periods = PeriodSchedule.Legacy;
            var source = new SchoolRecord { Key = "p", Title = "课程", Start = new DateTime(2026, 10, 6, 15, 55, 0), End = new DateTime(2026, 10, 6, 17, 30, 0) };
            SchoolMerge.Apply(data, "term", new() { AcademicCalendar.Adjust(source, true) }, false);
            var item = data.Items.Single(); item.Note = "keep"; item.Cancelled = true; item.LocalOverride = true;
            Assert(PeriodSchedule.Apply(data) == 1 && item.Start == new DateTime(2026, 9, 20, 16, 15, 0) && item.End.Hour == 17 && item.End.Minute == 50);
            Assert(item.Original!.BaseStart == new DateTime(2026, 10, 6, 16, 15, 0) && item.Note == "keep" && item.Cancelled && item.LocalOverride);
            Assert(PeriodSchedule.Apply(data) == 0); AcademicCalendar.ApplyCached(data); Assert(item.Start.Day == 20 && item.Start.Minute == 15);
        });
        test("Correction leaves exams, personal events and manually overridden times intact", () => {
            var data = new AppData(); data.Settings.Periods = PeriodSchedule.Legacy;
            var start = new DateTime(2026, 9, 21, 9, 50, 0);
            data.Items.Add(new() { Title = "考试", Kind = "考试", SourceKey = "e", Start = start, End = start.AddHours(1) });
            data.Items.Add(new() { Title = "个人", Start = start, End = start.AddHours(1) });
            data.Items.Add(new() { Title = "手动改期", Kind = "课程", SourceKey = "c", LocalOverride = true, Start = start, End = start.AddHours(1), Original = new() { Start = start.AddDays(1), End = start.AddDays(1).AddHours(1) } });
            PeriodSchedule.Apply(data); Assert(data.Items.All(i => i.Start == start));
        });
        test("Migration backs up before correction and keeps custom time settings", () => {
            var store = new Store(Path.Combine(temp, "periods")); store.Data.Settings.Periods = PeriodSchedule.Legacy; store.Save(); PeriodSchedule.Migrate(store);
            Assert(Directory.GetFiles(store.Root, "节次时间修复前-*.zip").Length == 1 && new Store(store.Root).Data.Settings.Periods == PeriodSchedule.Official);
            PeriodSchedule.Migrate(store); Assert(Directory.GetFiles(store.Root, "节次时间修复前-*.zip").Length == 1);
            store.Data.Settings.Periods = "09:00-09:45"; PeriodSchedule.Migrate(store); Assert(store.Data.Settings.Periods == "09:00-09:45");
        });
    }
}
