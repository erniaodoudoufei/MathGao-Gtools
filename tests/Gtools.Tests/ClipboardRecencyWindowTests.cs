using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Gtools.App;

namespace Gtools.Tests;

public sealed class ClipboardRecencyWindowTests
{
    [Theory]
    [InlineData("text","button")][InlineData("text","enter")][InlineData("text","double-click")]
    [InlineData("rich-text","button")][InlineData("rich-text","enter")][InlineData("rich-text","double-click")]
    [InlineData("image","button")][InlineData("image","enter")][InlineData("image","double-click")]
    public Task AllPasteEntrypointsMoveTheOriginalAndReopenAtTheTop(string kind,string entrypoint)=>RunSta(async windows=>
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var old=ClipboardRecencyTests.Add(db,"old",kind);var favorite=ClipboardRecencyTests.Add(db,"favorite",favorite:true);
        var restores=0;var pastes=0;var warnings=new List<string>();
        using var service=new ClipboardHistoryService(db,()=>500,new FakeClipboardReader(),restore:entry=>
        {
            ClipboardRecencyTests.SamePayload(old,entry);restores++;return Task.CompletedTask;
        });
        var window=Create(windows,new ClipboardWindow(db,service,IntPtr.Zero,pasteToTarget:_=>{pastes++;return Task.CompletedTask;},showWarning:warnings.Add));
        await ClipboardFixture.Until(()=>window.VisibleRowCount==2);var grid=Find<DataGrid>(window);
        grid.SelectedItem=grid.Items.OfType<ClipboardViewRow>().Single(x=>x.Id==old.Id);
        Assert.Equal(favorite.Id,db.SearchClipboard().First().Id); // Selection alone is not usage.
        Activate(window,grid,entrypoint);Activate(window,grid,entrypoint); // Guard against repeated clicks/keys while restoring.
        await ClipboardFixture.Until(()=>pastes==1);Assert.Equal(1,restores);Assert.False(window.IsVisible);Assert.Empty(warnings);
        Assert.Equal(old.Id,db.SearchClipboard().First().Id);Assert.Equal(2,db.SearchClipboard().Count);
        ClipboardRecencyTests.SamePayload(old,db.GetClipboard(old.Id)!);
        var reopened=Create(windows,new ClipboardWindow(db,service,IntPtr.Zero));
        await ClipboardFixture.Until(()=>reopened.VisibleRowCount==2);
        Assert.Equal(old.Id,((ClipboardViewRow)Find<DataGrid>(reopened).Items[0]).Id);
        Assert.Contains("最近复制或使用优先",reopened.StatusText);
        Assert.Equal(0,Find<ScrollViewer>(Find<DataGrid>(reopened)).VerticalOffset);
        reopened.Close();
    });

    [Theory][InlineData(false)][InlineData(true)]
    public Task RestoreAndSortFailuresHaveDifferentVisibleOutcomes(bool failSort)=>RunSta(async windows=>
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var old=ClipboardRecencyTests.Add(db,"old");var newest=ClipboardRecencyTests.Add(db,"newest");
        var pastes=0;var warnings=new List<string>();
        using var service=new ClipboardHistoryService(db,()=>500,new FakeClipboardReader(),
            restore:_=>failSort?Task.CompletedTask:Task.FromException(new IOException("synthetic restore error")),
            recordUse:_=>throw new IOException("synthetic sort error"));
        var window=Create(windows,new ClipboardWindow(db,service,IntPtr.Zero,pasteToTarget:_=>{pastes++;return Task.CompletedTask;},showWarning:warnings.Add));
        await ClipboardFixture.Until(()=>window.VisibleRowCount==2);var grid=Find<DataGrid>(window);
        grid.SelectedItem=grid.Items.OfType<ClipboardViewRow>().Single(x=>x.Id==old.Id);Activate(window,grid,"button");
        if(failSort)
        {
            await ClipboardFixture.Until(()=>warnings.Count==1);Assert.Contains("顺序保存失败",warnings[0]);
            Assert.Equal(1,pastes);Assert.False(window.IsVisible);
        }
        else
        {
            await ClipboardFixture.Until(()=>window.StatusText.StartsWith("恢复失败"));Assert.Equal(0,pastes);
            Assert.True(window.IsVisible);Assert.Empty(warnings);window.Close();
        }
        Assert.Equal(newest.Id,db.SearchClipboard().First().Id);Assert.Equal(2,db.SearchClipboard().Count);
    });

    private static ClipboardWindow Create(List<Window> windows,ClipboardWindow window)
    {
        windows.Add(window);window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-20000;window.Top=-20000;
        window.ShowInTaskbar=false;window.ShowActivated=false;window.Topmost=false;window.Show();return window;
    }

    private static void Activate(ClipboardWindow window,DataGrid grid,string entrypoint)
    {
        switch(entrypoint)
        {
            case "button":FindAll<Button>(window).Single(x=>Equals(x.Content,"粘贴")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));break;
            case "enter":grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(grid),Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});break;
            case "double-click":grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Control.MouseDoubleClickEvent});break;
            default:throw new ArgumentOutOfRangeException(nameof(entrypoint));
        }
    }

    private static T Find<T>(DependencyObject root) where T:DependencyObject=>FindAll<T>(root).First();
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match)yield return match;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var nested in FindAll<T>(VisualTreeHelper.GetChild(root,i)))yield return nested;
    }

    private static async Task RunSta(Func<List<Window>,Task> test)
    {
        // Routed-event integration tests for our own off-screen windows; no external app or system clipboard writes.
        if(string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))return;
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;var windows=new List<Window>();
            dispatcher.BeginInvoke(new Action(async()=>
            {
                try{await test(windows);done.TrySetResult();}
                catch(Exception ex){done.TrySetException(ex);}
                finally{foreach(var window in windows)window.Close();dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally{if(thread.IsAlive)Dispatcher.FromThread(thread)?.BeginInvokeShutdown(DispatcherPriority.Send);thread.Join(3000);}
    }
}
