using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gtools.Core;
using Microsoft.Win32;

namespace Gtools.Tests;

[Collection(TodoUiTestCollection.Name)]
public sealed class TodoCalendarUiTests
{
    private static void Drain()
    {
        var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public async Task DateRefreshDoesNotReloadRowsAndSamplerCanRemainStopped()
    {
        var date=new DateTime(2026,10,15);
        await TodoTopTests.OnSta(panel=>
        {
            var label=panel.Field<TextBlock>("_todoCalendar");Assert.Equal("九月初六·Thu.",label.Text);
            Assert.Equal("12项",panel.Field<TextBlock>("_todoCount").Text);Assert.Equal("12 项未完成",panel.Field<TextBlock>("_todoCount").ToolTip);
            var rows=panel.Rows.Children.Cast<object>().ToArray();var before=panel.Db.ListTodo();
            panel.Scroll.ScrollToBottom();panel.Layout();var offset=panel.Scroll.VerticalOffset;
            panel.Invoke("SetCalendarVisible",true);
            Assert.Equal(TimeSpan.FromSeconds(30),panel.Field<DispatcherTimer>("_calendarTimer").Interval);
            Assert.True(panel.Field<DispatcherTimer>("_calendarTimer").IsEnabled);Assert.False(panel.Field<DispatcherTimer>("_timer").IsEnabled);
            date=date.AddDays(1);panel.Invoke("RefreshTodoCalendar");panel.Layout();
            Assert.EndsWith("Fri.",label.Text);Assert.Equal(rows,panel.Rows.Children.Cast<object>().ToArray());
            Assert.Equal(before,panel.Db.ListTodo());Assert.Equal(offset,panel.Scroll.VerticalOffset);
            Assert.Equal(0,panel.Undo.Count);Assert.Equal(338,panel.Settings.MonitorLeft);Assert.Equal(500,panel.Settings.MonitorTop);
            Assert.False(panel.Field<bool>("_calendarEventsAttached")); // Detached-content tests never subscribe to desktop events.
        },today:()=>date);
    }

    [Fact]
    public async Task ClockChangeResumeHideReopenAndDisposeUseCurrentLocalDate()
    {
        var date=new DateTime(2026,10,15);
        await TodoTopTests.OnSta(panel=>
        {
            var label=panel.Field<TextBlock>("_todoCalendar");var timer=panel.Field<DispatcherTimer>("_calendarTimer");
            panel.Invoke("SetCalendarVisible",true);date=date.AddDays(1);
            panel.Invoke("CalendarTimeChanged",null!,EventArgs.Empty);Drain();Assert.EndsWith("Fri.",label.Text);
            date=date.AddDays(-2);panel.Invoke("CalendarPowerModeChanged",new object(),new PowerModeChangedEventArgs(PowerModes.Resume));
            Drain();Assert.EndsWith("Wed.",label.Text);
            date=date.AddDays(3);panel.Invoke("CalendarPowerModeChanged",new object(),new PowerModeChangedEventArgs(PowerModes.Suspend));
            Drain();Assert.EndsWith("Wed.",label.Text);
            panel.Invoke("SetCalendarVisible",false);Assert.False(timer.IsEnabled);
            panel.Invoke("CalendarTimeChanged",null!,EventArgs.Empty);Drain();Assert.EndsWith("Wed.",label.Text);
            panel.Invoke("SetCalendarVisible",true);Assert.EndsWith("Sat.",label.Text);Assert.True(timer.IsEnabled);
            panel.Invoke("AttachCalendarEvents");Assert.True(panel.Field<bool>("_calendarEventsAttached"));
            panel.Invoke("DisposeTodoCalendar");Assert.False(timer.IsEnabled);Assert.False(panel.Field<bool>("_calendarEventsAttached"));
            date=date.AddDays(1);panel.Invoke("CalendarTimeChanged",null!,EventArgs.Empty);panel.Invoke("SetCalendarVisible",true);Drain();
            Assert.EndsWith("Sat.",label.Text);Assert.False(timer.IsEnabled);
        },today:()=>date);
    }

    [Theory]
    [InlineData(1d,MonitorTheme.Light)][InlineData(1.25d,MonitorTheme.Light)][InlineData(1.5d,MonitorTheme.Light)][InlineData(2d,MonitorTheme.Light)]
    [InlineData(1d,MonitorTheme.Dark)][InlineData(1.25d,MonitorTheme.Dark)][InlineData(1.5d,MonitorTheme.Dark)][InlineData(2d,MonitorTheme.Dark)]
    public Task HeaderStaysOneLineInAllStatesAndSupportedDpi(double scale,MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        var label=panel.Field<TextBlock>("_todoCalendar");var count=panel.Field<TextBlock>("_todoCount");var header=(Grid)label.Parent;
        var actions=header.Children.OfType<StackPanel>().Single();
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));panel.Layout();var height=header.ActualHeight;
        Assert.Equal(11,label.FontSize);Assert.Same(count.Foreground,label.Foreground);Assert.Equal("闰六月初六·Wed.",label.Text);
        void Check()
        {
            panel.Layout();Assert.Equal(height,header.ActualHeight);
            Assert.True(count.TranslatePoint(new Point(count.ActualWidth,0),header).X<=label.TranslatePoint(new Point(),header).X);
            Assert.True(label.TranslatePoint(new Point(label.ActualWidth,0),header).X<=actions.TranslatePoint(new Point(),header).X);
            Assert.True(actions.TranslatePoint(new Point(actions.ActualWidth,0),panel.Root).X<=262);
            Assert.True(label.ActualWidth>=label.DesiredSize.Width-label.Margin.Left-label.Margin.Right-.5);
        }
        Check();
        panel.Button(0,"删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.Layout();
        Assert.Equal(Visibility.Collapsed,label.Visibility);Assert.Equal("已删除，可撤销",count.Text);
        var rows=panel.Rows.Children.Cast<object>().ToArray();
        panel.Invoke("ClearTodoNotice");Assert.Equal(Visibility.Visible,label.Visibility);Check();
        Assert.Equal(rows,panel.Rows.Children.Cast<object>().ToArray());Assert.Equal(Visibility.Visible,panel.UndoButton.Visibility);
        foreach(var item in panel.Db.ListTodo())panel.Db.SetTodoDone(item.Id,true);
        panel.Refresh();Assert.Equal("全部完成",count.Text);Check();
        foreach(var item in panel.Db.ListTodo())panel.Db.DeleteTodo(item.Id);
        panel.Refresh();Assert.Equal("",count.Text);Assert.Equal("暂无待办",count.ToolTip);Check();
        typeof(Gtools.App.FloatingDockWindow).GetField("_todoCountText",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(panel.Window,"999999项");
        panel.Invoke("UpdateTodoHeader");Check();
        var destination=Environment.GetEnvironmentVariable("GTOOLS_TODO_CALENDAR_PREVIEW_ROOT");
        if(string.IsNullOrWhiteSpace(destination))return;
        // Repopulate synthetic rows only, including styles, for the visual acceptance image.
        for(var i=0;i<7;i++){var id=panel.Db.AddTodo("合成待办 "+(i+1));panel.Db.UpdateTodoStyle(id,i%2==0,i%3==0,(TodoTextColor)(i%6));}
        panel.Settings.TodoVisibleRows=7;panel.Refresh();panel.Layout();Directory.CreateDirectory(destination);
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(262*scale),(int)Math.Ceiling(panel.Root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        bitmap.Render(panel.Root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(destination,$"todo-calendar-{theme}-{scale*100:0}.png"));encoder.Save(file);
    },theme,()=>new DateTime(2025,7,30));
}
