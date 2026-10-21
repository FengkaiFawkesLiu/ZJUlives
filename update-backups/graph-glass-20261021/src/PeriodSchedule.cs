using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Shike;

public static class PeriodSchedule
{
    // Undergraduate Office 2026-2027 calendar; checked against Celechron 2026-2027-1.json, 2026-09-18.
    public const string Official = "08:00-08:45;08:50-09:35;10:00-10:45;10:50-11:35;11:40-12:25;13:25-14:10;14:15-15:00;15:05-15:50;16:15-17:00;17:05-17:50;18:50-19:35;19:40-20:25;20:30-21:15";
    public const string Legacy = "08:00-08:45;08:50-09:35;09:50-10:35;10:40-11:25;11:30-12:15;13:15-14:00;14:05-14:50;14:55-15:40;15:55-16:40;16:45-17:30;18:30-19:15;19:20-20:05;20:10-20:55";
    public const string Source = "https://ugrs.zju.edu.cn/2026/0710/c28218a3187939/page.htm";
    static DateTime Fix(DateTime date, bool end)
    {
        var old = SchoolParser.Periods(Legacy); var corrected = SchoolParser.Periods(Official);
        for (int i = 0; i < old.Count; i++) if (date.TimeOfDay == (end ? old[i].End : old[i].Start)) return date.Date + (end ? corrected[i].End : corrected[i].Start);
        return date;
    }
    static void FixRecord(SchoolRecord? r)
    {
        if (r == null || r.Kind != "课程") return;
        r.Start = Fix(r.Start, false); r.End = Fix(r.End, true);
        if (r.BaseStart is DateTime start) r.BaseStart = Fix(start, false);
        if (r.BaseEnd is DateTime end) r.BaseEnd = Fix(end, true);
    }
    public static int Apply(AppData data)
    {
        // Only migrate the exact erroneous shipped table; never reinterpret a user-specified timetable.
        if (data.Settings.Periods != Legacy) return 0;
        int changed = 0;
        foreach (var item in data.Items.Where(i => i.Kind == "课程" && i.SourceKey != null))
        {
            bool sameTimes = item.Original != null && item.Start == item.Original.Start && item.End == item.Original.End;
            if (!item.LocalOverride || sameTimes)
            {
                var start = Fix(item.Start, false); var end = Fix(item.End, true);
                if (start != item.Start || end != item.End) changed++;
                item.Start = start; item.End = end;
            }
            FixRecord(item.Original); FixRecord(item.Pending);
        }
        data.Settings.Periods = Official;
        return changed;
    }
    public static int Migrate(Store store)
    {
        if (store.Data.Settings.Periods != Legacy) return 0;
        string before = JsonSerializer.Serialize(store.Data);
        store.Backup(Path.Combine(store.Root, "节次时间修复前-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".zip"));
        try { int changed = Apply(store.Data); store.Save(); return changed; }
        catch { store.Data = JsonSerializer.Deserialize<AppData>(before)!; throw; }
    }
}
