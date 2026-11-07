using System.Globalization;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class TodoCalendarTests
{
    [Theory]
    [InlineData(2026,10,15,"九月初六·Thu.","农历九月初六，星期四")]
    [InlineData(2026,2,16,"腊月廿九·Mon.","农历腊月廿九，星期一")]
    [InlineData(2026,2,17,"正月初一·Tue.","农历正月初一，星期二")]
    [InlineData(2026,3,18,"正月三十·Wed.","农历正月三十，星期三")]
    [InlineData(2026,3,19,"二月初一·Thu.","农历二月初一，星期四")]
    [InlineData(2025,7,24,"六月三十·Thu.","农历六月三十，星期四")]
    [InlineData(2025,7,25,"闰六月初一·Fri.","农历闰六月初一，星期五")]
    [InlineData(2025,8,22,"闰六月廿九·Fri.","农历闰六月廿九，星期五")]
    [InlineData(2025,8,23,"七月初一·Sat.","农历七月初一，星期六")]
    public void FormatsKnownLunarDates(int year,int month,int day,string text,string tip)
    {
        Assert.Equal(new TodoCalendarText(text,tip),TodoCalendarFormatter.Format(new DateTime(year,month,day)));
    }

    [Theory]
    [InlineData(12,"Mon.","一")][InlineData(13,"Tue.","二")][InlineData(14,"Wed.","三")]
    [InlineData(15,"Thu.","四")][InlineData(16,"Fri.","五")][InlineData(17,"Sat.","六")][InlineData(18,"Sun.","日")]
    public void WeekdaysAlwaysUseTheChosenEnglishAbbreviations(int day,string english,string chinese)
    {
        var result=TodoCalendarFormatter.Format(new DateTime(2026,10,day));
        Assert.EndsWith("·"+english,result.Text);Assert.EndsWith("星期"+chinese,result.ToolTip);
        Assert.DoesNotContain("2026",result.Text);Assert.DoesNotContain("2026",result.ToolTip);
    }

    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ar-SA")]
    public void FormattingDoesNotDependOnSystemCulture(string culture)
    {
        var before=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
            Assert.Equal("九月初六·Thu.",TodoCalendarFormatter.Format(new DateTime(2026,10,15)).Text);
        }
        finally{CultureInfo.CurrentCulture=before;}
    }

    [Fact]
    public void SupportedBoundariesWorkAndOutOfRangeFallsBackToWeekdayOnly()
    {
        var calendar=new ChineseLunisolarCalendar();
        Assert.Contains("·",TodoCalendarFormatter.Format(calendar.MinSupportedDateTime).Text);
        Assert.Contains("·",TodoCalendarFormatter.Format(calendar.MaxSupportedDateTime).Text);
        foreach(var date in new[]{DateTime.MinValue,calendar.MinSupportedDateTime.AddDays(-1),calendar.MaxSupportedDateTime.AddDays(1),DateTime.MaxValue})
        {
            var text=TodoCalendarFormatter.Format(date);Assert.DoesNotContain("·",text.Text);
            Assert.Contains("农历日期超出支持范围",text.ToolTip);
        }
    }

    [Fact]
    public void StateChangesOnlyWhenTheLocalDateChangesInEitherDirection()
    {
        var clock=new DateTime(2026,10,15,23,59,59);var state=new TodoCalendarState(()=>clock);
        Assert.True(state.Refresh());var original=state.Current;
        Assert.False(state.Refresh());Assert.Same(original,state.Current);
        clock=clock.AddSeconds(2);Assert.True(state.Refresh());Assert.EndsWith("Fri.",state.Current!.Text);
        clock=clock.AddHours(4);Assert.False(state.Refresh());
        clock=clock.AddDays(-2);Assert.True(state.Refresh());Assert.EndsWith("Wed.",state.Current!.Text);
    }
}
