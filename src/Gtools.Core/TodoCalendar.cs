using System.Globalization;

namespace Gtools.Core;

internal sealed record TodoCalendarText(string Text, string ToolTip);

internal static class TodoCalendarFormatter
{
    private static readonly string[] Weeks = ["Sun.", "Mon.", "Tue.", "Wed.", "Thu.", "Fri.", "Sat."];
    private static readonly string[] ChineseWeeks = ["日", "一", "二", "三", "四", "五", "六"];
    private static readonly string[] Months = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊"];
    private static readonly string[] Days = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];

    public static TodoCalendarText Format(DateTime date)
    {
        date = date.Date;
        var weekday = (int)date.DayOfWeek;
        var chineseWeek = "星期" + ChineseWeeks[weekday];
        // No network, locale-sensitive formatting, or approximation outside the calendar's range.
        var calendar = new ChineseLunisolarCalendar();
        if (date < calendar.MinSupportedDateTime.Date || date > calendar.MaxSupportedDateTime.Date)
            return new(Weeks[weekday], chineseWeek + "；农历日期超出支持范围");
        var month = calendar.GetMonth(date);
        var leapMonth = calendar.GetLeapMonth(calendar.GetYear(date));
        var isLeap = leapMonth != 0 && month == leapMonth;
        if (leapMonth != 0 && month >= leapMonth) month--;
        var lunar = (isLeap ? "闰" : "") + Months[month - 1] + "月" + Days[calendar.GetDayOfMonth(date) - 1];
        return new(lunar + "·" + Weeks[weekday], "农历" + lunar + "，" + chineseWeek);
    }
}

/// <summary>Refresh only on local date changes, including backwards clock/time-zone adjustments.</summary>
internal sealed class TodoCalendarState(Func<DateTime> today)
{
    private DateTime? _date;
    public TodoCalendarText? Current { get; private set; }
    public bool Refresh()
    {
        var date = today().Date;
        if (_date == date) return false;
        _date = date;
        Current = TodoCalendarFormatter.Format(date);
        return true;
    }
}
