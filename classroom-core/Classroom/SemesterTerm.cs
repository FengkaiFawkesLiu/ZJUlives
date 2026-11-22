using System.Globalization;
using System.Text.RegularExpressions;

namespace ZJUSS.Core.Classroom;

/// <summary>
/// D25 规范学期号：把教务网 <c>CourseView.Term</c> 文本和智云课堂的 year/season
/// 转成 <c>YYYY-YYYY-N</c>。只识别已确认的写法，无法确认时返回 null；
/// 不做大小写、全半角、相似度或别名匹配。
/// </summary>
internal static class SemesterTerm
{
    private static readonly Regex CanonicalPattern = new(
        @"^(?<y1>\d{4})\s*-\s*(?<y2>\d{4})\s*-\s*(?<n>[123])$",
        RegexOptions.Compiled);

    private static readonly Regex ChinesePattern = new(
        @"^(?<y1>\d{4})\s*-\s*(?<y2>\d{4})\s*(?:学年)?\s*(?:(?:第\s*)?(?<num>[123])\s*学期|(?:第\s*)?(?<cn>[一二三])\s*学期|(?<season>秋冬|春夏|秋|冬|春|夏)(?:\s*学期)?)$",
        RegexOptions.Compiled);

    private static readonly Regex YearPattern = new(
        @"^(?<y1>\d{4})\s*-\s*(?<y2>\d{4})$",
        RegexOptions.Compiled);

    /// <summary>把学期文本转成规范学期号；无法确认时返回 null。</summary>
    public static string? TryNormalize(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var canonical = CanonicalPattern.Match(text);
        if (canonical.Success)
        {
            return Build(
                canonical.Groups["y1"].Value,
                canonical.Groups["y2"].Value,
                canonical.Groups["n"].Value);
        }

        var chinese = ChinesePattern.Match(text);
        if (!chinese.Success)
        {
            return null;
        }

        var semester = chinese.Groups["num"].Success
            ? chinese.Groups["num"].Value
            : chinese.Groups["cn"].Success
                ? chinese.Groups["cn"].Value switch
                {
                    "一" => "1",
                    "二" => "2",
                    "三" => "3",
                    _ => null
                }
                : chinese.Groups["season"].Value switch
                {
                    "秋冬" or "秋" or "冬" => "1",
                    "春夏" or "春" => "2",
                    "夏" => "3",
                    _ => null
                };

        return semester is null
            ? null
            : Build(chinese.Groups["y1"].Value, chinese.Groups["y2"].Value, semester);
    }

    /// <summary>把智云课堂的 year（YYYY-YYYY）和 season（1/2/3）转成规范学期号。</summary>
    public static string? FromYearAndSeason(string? year, string? season)
    {
        var yearText = year?.Trim();
        var seasonText = season?.Trim();
        if (string.IsNullOrEmpty(yearText) || string.IsNullOrEmpty(seasonText))
        {
            return null;
        }

        var match = YearPattern.Match(yearText);
        if (!match.Success)
        {
            return null;
        }

        var semester = seasonText switch
        {
            "1" or "秋冬" or "秋" or "冬" => "1",
            "2" or "春夏" or "春" => "2",
            "3" or "夏" => "3",
            _ => null
        };

        return semester is null
            ? null
            : Build(match.Groups["y1"].Value, match.Groups["y2"].Value, semester);
    }

    private static string? Build(string y1Text, string y2Text, string semester)
    {
        if (!int.TryParse(y1Text, NumberStyles.None, CultureInfo.InvariantCulture, out var y1)
            || !int.TryParse(y2Text, NumberStyles.None, CultureInfo.InvariantCulture, out var y2)
            || y2 != y1 + 1
            || semester is not ("1" or "2" or "3"))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{y1:D4}-{y2:D4}-{semester}");
    }
}
