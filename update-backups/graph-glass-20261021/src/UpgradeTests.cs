using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Shike;

public static class UpgradeTests
{
    sealed class FakeProvider : ISchoolProvider
    {
        readonly bool fail; public bool Disposed;
        public FakeProvider(bool fail = false) => this.fail = fail;
        public Task Login(string user, string password) => fail ? Task.FromException(new HttpRequestException("offline")) : Task.CompletedTask;
        public Task<SchoolBatch> Fetch(Settings settings, string captcha) => Task.FromResult(new SchoolBatch(new() { R(new(2026, 10, 6)) }, new(), new()));
        public Task<byte[]> Captcha() => Task.FromResult(Array.Empty<byte>());
        public void Dispose() => Disposed = true;
    }
    static SchoolRecord R(DateTime day, string key = "c") => new() { Key = key, Title = "测试课程", Start = day.AddHours(9), End = day.AddHours(9).AddMinutes(45), Location = "紫金港东1B-310" };
    public static void Run(Action<string, Action> test, string temp)
    {
        void Assert(bool ok) { if (!ok) throw new Exception("Assertion failed"); }
        test("DPAPI credentials roundtrip without plaintext on disk", () =>
        {
            string root = Path.Combine(temp, "vault"); Directory.CreateDirectory(root); var vault = new CredentialVault(root);
            vault.Save(new("test-student-123", "local-only-password")); var saved = vault.Read();
            Assert(saved?.Username == "test-student-123" && saved.Password == "local-only-password");
            var bytes = File.ReadAllBytes(Path.Combine(root, "school-credentials.dpapi"));
            Assert(!Encoding.UTF8.GetString(bytes).Contains("local-only-password") && !Encoding.UTF8.GetString(bytes).Contains("test-student-123"));
            vault.Forget(); Assert(!vault.Exists && vault.Read() == null);
        });
        test("DPAPI tampering fails closed", () =>
        {
            string root = Path.Combine(temp, "tamper"); Directory.CreateDirectory(root); var vault = new CredentialVault(root); vault.Save(new("test", "secret"));
            string path = Path.Combine(root, "school-credentials.dpapi"); byte[] bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(path, bytes);
            try { vault.Read(); throw new Exception("Tampered secret accepted"); } catch (CryptographicException) { }
        });
        test("Export never includes saved credentials", () =>
        {
            var s = new Store(Path.Combine(temp, "vault-backup")); File.WriteAllBytes(Path.Combine(s.Root, "school-credentials.dpapi"), RandomNumberGenerator.GetBytes(128));
            string zip = Path.Combine(temp, "no-secrets.zip"); s.Backup(zip); using var archive = ZipFile.OpenRead(zip);
            Assert(archive.Entries.All(e => !e.FullName.Contains("credential")));
            using var reader = new StreamReader(archive.GetEntry("data.json")!.Open()); Assert(!reader.ReadToEnd().Contains("backup-excluded-password"));
        });
        test("Relative update age advances and never goes negative", () =>
        {
            var now = new DateTime(2026, 9, 17, 12, 0, 0);
            Assert(Freshness.Format(now.AddMinutes(-7), now).Contains("7 分钟"));
            Assert(Freshness.Format(now.AddMinutes(2), now).Contains("刚刚")); Assert(Freshness.Format(null, now).Contains("尚未"));
        });
        test("Every launch fetch can refresh through the provider contract", () =>
        {
            var provider = new FakeProvider(); var batch = SchoolSync.Fetch(new("fake", "fake"), new(), () => provider).GetAwaiter().GetResult();
            var data = new AppData(); var now = DateTime.Now; SchoolSync.Apply(data, batch, "fake", new(), now, true);
            Assert(provider.Disposed && data.Settings.LastSync == now && data.Items[0].Start.Date == new DateTime(2026, 9, 20));
        });
        test("Offline startup refresh leaves previous timestamp and data", () =>
        {
            var data = new AppData(); data.Settings.LastSync = new DateTime(2026, 9, 16); string before = JsonSerializer.Serialize(data); var provider = new FakeProvider(true);
            try { SchoolSync.Fetch(new("fake", "fake"), data.Settings, () => provider).GetAwaiter().GetResult(); throw new Exception("Offline accepted"); } catch (HttpRequestException) { }
            Assert(provider.Disposed && JsonSerializer.Serialize(data) == before);
        });
        test("Holiday course exchanges operate in both directions", () =>
        {
            Assert(AcademicCalendar.Adjust(R(new(2026, 10, 6)), true).Start.Date == new DateTime(2026, 9, 20));
            Assert(AcademicCalendar.Adjust(R(new(2026, 9, 20)), true).Start.Date == new DateTime(2026, 10, 6));
            Assert(AcademicCalendar.Adjust(R(new(2026, 10, 7)), true).Start.Date == new DateTime(2026, 10, 10));
            Assert(AcademicCalendar.Adjust(R(new(2026, 10, 10)), true).Start.Date == new DateTime(2026, 10, 7));
        });
        test("Holiday application is idempotent and reversible", () =>
        {
            var raw = R(new(2026, 10, 6)); var first = AcademicCalendar.Adjust(raw, true); var second = AcademicCalendar.Adjust(first, true);
            Assert(first.Same(second)); Assert(AcademicCalendar.Adjust(second, false).Start == raw.Start);
        });
        test("Makeup dates preserve time, location and stable source ID", () =>
        {
            var a = AcademicCalendar.Adjust(R(new(2026, 10, 2), "oct2"), true); var b = AcademicCalendar.Adjust(R(new(2026, 12, 31)), true);
            Assert(a.Start == new DateTime(2026, 10, 17, 9, 0, 0) && a.Key == "oct2" && a.Location == "紫金港东1B-310");
            Assert(b.Start.Date == new DateTime(2027, 1, 4));
        });
        test("Explicit suspended days disappear from calendar and reminders", () =>
        {
            var data = new AppData(); var r = AcademicCalendar.Adjust(R(new(2026, 9, 25)), true); SchoolMerge.Apply(data, "school", new() { r }, false); data.Items[0].Reminders = new() { 0 };
            Assert(r.Suspended && !Engine.Between(data.Items, r.Start.Date, r.End.Date.AddDays(1)).Any() && !Engine.Due(data, r.Start).Any());
        });
        test("Holiday weekends are not blanket-cancelled", () =>
        {
            Assert(!AcademicCalendar.Adjust(R(new(2026, 9, 26)), true).Suspended);
            Assert(!AcademicCalendar.Adjust(R(new(2026, 10, 4)), true).Suspended);
            Assert(AcademicCalendar.Day(new(2026, 10, 4))?.Holiday == true);
        });
        test("Holiday adjustment never changes personal activities or exams", () =>
        {
            var data = new AppData(); var exam = R(new(2026, 10, 1)); exam.Kind = "考试";
            Assert(AcademicCalendar.Adjust(exam, true).Same(exam));
            var i = new Item { Title = "社团活动", Start = exam.Start, End = exam.End }; data.Items.Add(i); AcademicCalendar.ApplyCached(data); Assert(!i.SchoolSuspended && i.Start == exam.Start);
        });
        test("Applying holidays to existing cached courses preserves notes", () =>
        {
            var data = new AppData(); SchoolMerge.Apply(data, "cached", new() { R(new(2026, 10, 6)) }, false); data.Items[0].Note = "keep";
            AcademicCalendar.ApplyCached(data); AcademicCalendar.ApplyCached(data);
            Assert(data.Items.Count == 1 && data.Items[0].Start.Date == new DateTime(2026, 9, 20) && data.Items[0].Note == "keep");
        });
        test("First-run migration backs up original schedule exactly once", () =>
        {
            var s = new Store(Path.Combine(temp, "migration")); SchoolMerge.Apply(s.Data, "cached", new() { R(new(2026, 10, 6)) }, false); s.Save();
            AcademicCalendar.MigrateCached(s); AcademicCalendar.MigrateCached(s);
            var files = Directory.GetFiles(s.Root, "校历调整前-*.zip"); Assert(files.Length == 1);
            using var archive = ZipFile.OpenRead(files[0]); using var reader = new StreamReader(archive.GetEntry("data.json")!.Open());
            var before = JsonSerializer.Deserialize<AppData>(reader.ReadToEnd())!;
            Assert(before.Items[0].Start.Date == new DateTime(2026, 10, 6)); Assert(new Store(s.Root).Data.Items[0].Start.Date == new DateTime(2026, 9, 20));
        });
        test("Local overrides conflict instead of disappearing for a holiday", () =>
        {
            var data = new AppData(); SchoolMerge.Apply(data, "cached", new() { R(new(2026, 9, 25)) }, false); var i = data.Items[0]; i.LocalOverride = true; i.Location = "自定义地点";
            AcademicCalendar.ApplyCached(data); Assert(i.Pending?.Suspended == true && i.Location == "自定义地点");
            SchoolMerge.Resolve(i, false); Assert(Engine.Between(data.Items, i.Start.Date, i.End.Date.AddDays(1)).Any());
        });
        test("Encoded timetable HTML and structured campus room fields parse", () =>
        {
            var settings = new Settings { TermMonday = new(2026, 9, 14), Weeks = 1 };
            var json = """{"kbList":[{"kcb":"数学&lt;br/&gt;课程号&lt;br/&gt;测试教师&lt;br/&gt;紫金港东1B-310zwf","xqj":"1","djj":"1","skcd":"1"},{"kcmc":"物理","xqj":"2","djj":"1","skcd":"1","xqmc":"玉泉校区","cdmc":"教室：教7-202","xm":"教师B"}]}""";
            var batch = SchoolParser.Parse(json, "{\"items\":[]}", settings);
            Assert(batch.Courses[0].Location == "紫金港东1B-310" && batch.Courses[0].Teacher == "测试教师");
            Assert(batch.Courses[1].Location == "玉泉校区 · 教7-202");
        });
        test("Unknown official notice does not count as verified", () => Assert(!AcademicCalendar.MatchesKnownNotice("2026年国庆：放假安排已变更")));
        test("Empty automatic responses do not remove cached schedules", () =>
        {
            var data = new AppData(); var term = new Settings(); SchoolSync.Apply(data, new(new() { R(new(2026, 9, 14)) }, new(), new()), "fake", term, DateTime.Now, true);
            var last = data.Settings.LastSync;
            try { SchoolSync.Apply(data, new(new(), new(), new()), "fake", term, DateTime.Now, true); throw new Exception("Empty refresh accepted"); } catch (InvalidDataException) { }
            Assert(!data.Items[0].Cancelled && data.Settings.LastSync == last);
        });
    }
}
