using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class TodoTopTests
{
    [Theory][InlineData(1)][InlineData(11)]
    public Task ButtonMovesTheOriginalToTopAndSupportsLaterMovesDeleteAndUndo(int index)=>OnSta(panel=>
    {
        var before=panel.Db.ListTodo();var target=before[index];
        var oldWidth=panel.Window.Width;var oldLeft=panel.Settings.MonitorLeft;var oldTop=panel.Settings.MonitorTop;
        panel.Scroll.ScrollToBottom();panel.Layout();Assert.True(panel.Scroll.VerticalOffset>0);
        var buttons=panel.Buttons(index);Assert.Equal(new[]{"加粗","倾斜","文字颜色","一键置顶","上移","下移","删除"},buttons.Select(b=>b.ToolTip));
        panel.Button(index,"一键置顶").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.Layout();
        var expected=new[]{target.Id}.Concat(before.Where(x=>x.Id!=target.Id).Select(x=>x.Id));
        Assert.Equal(expected,panel.Db.ListTodo().Select(x=>x.Id));Assert.Equal(0,panel.Scroll.VerticalOffset);
        Assert.Equal(target.Text,panel.FirstText());
        foreach(var item in panel.Db.ListTodo())Assert.Equal(before.Single(x=>x.Id==item.Id) with{SortOrder=item.SortOrder},item);
        Assert.Equal(0,panel.Undo.Count);Assert.Equal(oldWidth,panel.Window.Width);
        Assert.Equal(oldLeft,panel.Settings.MonitorLeft);Assert.Equal(oldTop,panel.Settings.MonitorTop);
        var reopened=new LocalDatabase(panel.File);Assert.Equal(expected,reopened.ListTodo().Select(x=>x.Id));
        var disabled=panel.Button(0,"一键置顶");Assert.False(disabled.IsHitTestVisible);Assert.Equal(.25,disabled.Opacity);
        disabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(expected,panel.Db.ListTodo().Select(x=>x.Id));
        // Top is an ordinary sort operation: moving down and then up still works.
        panel.Button(0,"下移").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(target.Id,panel.Db.ListTodo()[1].Id);
        panel.Button(1,"上移").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(target.Id,panel.Db.ListTodo()[0].Id);
        var moved=panel.Db.ListTodo()[0];panel.Button(0,"删除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.DoesNotContain(panel.Db.ListTodo(),x=>x.Id==target.Id);Assert.Equal(1,panel.Undo.Count);
        panel.UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(moved,panel.Db.ListTodo()[0]);Assert.Equal(expected,panel.Db.ListTodo().Select(x=>x.Id));
        var newId=panel.Db.AddTodo("合成新增事项");Assert.Equal(newId,panel.Db.ListTodo().Last().Id);
    });

    [Fact]
    public Task StaleRowReReadsCurrentOrderAndDeletedRowCannotChangeOtherItems()=>OnSta(panel=>
    {
        var initial=panel.Db.ListTodo();var staleButton=panel.Button(6,"一键置顶");var chosen=initial[6];
        var added=panel.Db.AddTodo("合成稍后新增");
        panel.Db.ReorderTodo(panel.Db.ListTodo().Select(x=>x.Id).Reverse().ToArray());
        var latest=panel.Db.ListTodo();staleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[]{chosen.Id}.Concat(latest.Where(x=>x.Id!=chosen.Id).Select(x=>x.Id)),panel.Db.ListTodo().Select(x=>x.Id));
        Assert.Contains(panel.Db.ListTodo(),x=>x.Id==added);
        var removedButton=panel.Button(3,"一键置顶");var removed=panel.Db.ListTodo()[3];panel.Db.DeleteTodo(removed.Id);
        var remaining=panel.Db.ListTodo();removedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(remaining,panel.Db.ListTodo());
    });

    [Theory][InlineData(1d)][InlineData(1.25d)][InlineData(1.5d)][InlineData(2d)]
    public Task ButtonLayoutDoesNotOverlapAtSupportedDpiAndRendersForReview(double scale)=>OnSta(panel=>
    {
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));panel.Layout();
        var buttons=panel.Buttons(1);var row=(Grid)panel.Rows.Children[1];
        var text=row.Children.OfType<TextBlock>().Single();var end=text.TranslatePoint(new Point(text.ActualWidth,0),row).X;
        var first=buttons[0].TranslatePoint(new Point(),row).X;Assert.True(end<=first+.5);
        for(var i=0;i<buttons.Length;i++)
        {
            var button=buttons[i];Assert.Equal(17,button.ActualWidth);Assert.Equal(17,button.ActualHeight);
            if(i>0)Assert.True(buttons[i-1].TranslatePoint(new Point(17,0),row).X<=button.TranslatePoint(new Point(),row).X);
            Assert.True(button.TranslatePoint(new Point(17,0),panel.Root).X<=262);
        }
        var top=panel.Button(1,"一键置顶");Assert.Equal("⤒",top.Content);Assert.Equal("一键置顶",System.Windows.Automation.AutomationProperties.GetName(top));
        var destination=Environment.GetEnvironmentVariable("GTOOLS_TODO_PREVIEW_ROOT");
        if(!string.IsNullOrWhiteSpace(destination))
        {
            Directory.CreateDirectory(destination);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(262*scale),(int)Math.Ceiling(panel.Root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(panel.Root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(destination,$"todo-top-{scale*100:0}.png"));encoder.Save(file);
        }
    });

    internal static async Task OnSta(Action<TodoPanel> test,MonitorTheme theme=MonitorTheme.Light,Func<DateTime>? today=null)
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try{using var panel=new TodoPanel(theme,today);test(panel);completed.TrySetResult();}
            catch(Exception ex){completed.TrySetException(ex);}
            finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.True(thread.Join(3000));
    }

    // Exercise the real row buttons/content, without showing a native window or loading/saving formal settings.
    internal sealed class TodoPanel:IDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
        public string File=>Path.Combine(_root,"todo.db");
        public LocalDatabase Db{get;}
        public GtoolsSettings Settings{get;}=new(){TodoVisibleRows=3,MonitorLeft=338,MonitorTop=500};
        public FloatingDockWindow Window{get;}
        public StackPanel Root{get;}
        public StackPanel Rows=>Field<StackPanel>("_todoRows");
        public ScrollViewer Scroll=>Field<ScrollViewer>("_todoScroll");
        public TodoUndoHistory Undo=>Field<TodoUndoHistory>("_undo");
        public Button UndoButton=>Field<Button>("_undoButton");
        public TodoPanel(MonitorTheme theme=MonitorTheme.Light,Func<DateTime>? today=null)
        {
            Settings.MonitorTheme=theme;
            Db=new LocalDatabase(File);Db.Initialize();
            for(var i=0;i<12;i++){var id=Db.AddTodo($"合成事项 {i+1:00}");if(i is 1 or 11)Db.SetTodoDone(id,true);}
            Window=new FloatingDockWindow(Settings,new SettingsStore(),Db,today);Root=(StackPanel)Window.Content;Window.Content=null;
            Field<Border>("_todoBlock").Visibility=Visibility.Visible;
            typeof(FloatingDockWindow).GetMethod("RefreshTodo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,null);
            Layout();
        }
        public T Field<T>(string name)=>(T)typeof(FloatingDockWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Window)!;
        public void Invoke(string name,params object[] args)=>typeof(FloatingDockWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,args);
        public Button[] Buttons(int row)=>((Grid)Rows.Children[row]).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
        public Button Button(int row,string label)=>Buttons(row).Single(b=>Equals(b.ToolTip,label));
        public void Refresh()=>typeof(FloatingDockWindow).GetMethod("RefreshTodo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,null);
        public Border Palette(long id)=>(Border)typeof(FloatingDockWindow).GetMethod("BuildTodoColorPalette",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Window,new object[]{id})!;
        public string FirstText()=>((Grid)Rows.Children[0]).Children.OfType<TextBlock>().Single().Text;
        public void Layout(){Root.Measure(new Size(262,double.PositiveInfinity));Root.Arrange(new Rect(0,0,262,Root.DesiredSize.Height));Root.UpdateLayout();}
        public void Dispose()
        {
            Invoke("DisposeTodoCalendar");
            Field<DispatcherTimer>("_noticeTimer").Stop();Field<DispatcherTimer>("_timer").Stop();
            Field<DesktopLayerHost>("_desktopLayer").Dispose();Directory.Delete(_root,true);
        }
    }
}
