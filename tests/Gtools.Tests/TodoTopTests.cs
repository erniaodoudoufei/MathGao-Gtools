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
        var buttons=panel.Buttons(index);Assert.Equal(new[]{"编辑","更多","删除","加粗","倾斜","文字颜色","一键置顶","上移","下移"},buttons.Select(b=>b.ToolTip));
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
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));
        panel.Button(1,"更多").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.Layout();
        Assert.Equal(Visibility.Visible,panel.Tools(1).Visibility);panel.AssertRowLayout(1);
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
        // A row holds two button groups: the hover strip (编辑/更多/删除) and the collapsible tool line below it.
        public Grid Row(int row)=>(Grid)Rows.Children[row];
        public StackPanel Actions(int row)=>Row(row).Children.OfType<StackPanel>().First();
        public StackPanel Tools(int row)=>Row(row).Children.OfType<StackPanel>().Last();
        public Button[] Buttons(int row)=>Row(row).Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()).ToArray();
        public Button Button(int row,string label)=>Buttons(row).Single(b=>Equals(b.ToolTip,label));
        public TextBox? Editor(int row)=>Row(row).Children.OfType<TextBox>().SingleOrDefault();
        public void Hover(int row,bool enter=true)=>Row(row).RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0)
            {RoutedEvent=enter?System.Windows.Input.Mouse.MouseEnterEvent:System.Windows.Input.Mouse.MouseLeaveEvent});
        public static void Press(UIElement target,System.Windows.Input.Key key)=>target.RaiseEvent(
            new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,new FakeSource(),0,key){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});
        public static void LoseFocus(UIElement target)=>target.RaiseEvent(
            new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,0,(IInputElement)target,null)
            {RoutedEvent=System.Windows.Input.Keyboard.LostKeyboardFocusEvent});
        public void ClickText(int row,int clickCount)
        {
            var args=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left)
                {RoutedEvent=UIElement.MouseLeftButtonDownEvent};
            typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(args,clickCount);
            Row(row).Children.OfType<TextBlock>().Single().RaiseEvent(args);
        }
        // Visible groups keep 17px buttons in order inside the 262px dock; text never runs under the hover strip,
        // and the tool line sits below the text line instead of squeezing it.
        public void AssertRowLayout(int index)
        {
            var row=Row(index);var content=(FrameworkElement)row.Children[1];var actions=Actions(index);var tools=Tools(index);
            var contentEnd=content.TranslatePoint(new Point(content.ActualWidth,0),row).X;
            if(actions.Visibility==Visibility.Visible)Assert.True(contentEnd<=actions.TranslatePoint(new Point(),row).X+.5);
            else Assert.True(contentEnd>=row.ActualWidth-.5);
            if(tools.Visibility==Visibility.Visible)
                Assert.True(tools.TranslatePoint(new Point(),row).Y>=content.TranslatePoint(new Point(0,content.ActualHeight),row).Y-.5);
            foreach(var group in new[]{actions,tools}.Where(x=>x.Visibility==Visibility.Visible))
            {
                var buttons=group.Children.OfType<Button>().ToArray();
                for(var i=0;i<buttons.Length;i++)
                {
                    Assert.Equal(17,buttons[i].ActualWidth);Assert.Equal(17,buttons[i].ActualHeight);
                    if(i>0)Assert.True(buttons[i-1].TranslatePoint(new Point(17,0),row).X<=buttons[i].TranslatePoint(new Point(),row).X);
                    Assert.True(buttons[i].TranslatePoint(new Point(17,0),Root).X<=262);
                }
            }
        }
        private sealed class FakeSource:PresentationSource
        {
            protected override CompositionTarget GetCompositionTargetCore()=>null!;
            public override Visual RootVisual{get;set;}=null!;
            public override bool IsDisposed=>false;
        }
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
