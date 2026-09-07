using System.Windows.Threading;
using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class ClipboardWindowTests
{
    [Fact]
    public async Task ImagesAreCapturedWhileTheMainDispatcherIsBusyThenAppearWithinTwoSeconds()
    {
        if(string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))return;
        using var fixture=new ClipboardFixture();var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pngA=ClipboardFixture.Png(SkiaSharp.SKColors.Blue,2048);var pngB=ClipboardFixture.Png(SkiaSharp.SKColors.Orange,2048);
        var thread=new Thread(()=>
        {
            var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
            var window=new ClipboardWindow(fixture.Database,service,IntPtr.Zero)
            {WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,Topmost=false};
            window.ContentRendered+=async(_,_)=>
            {
                try
                {
                    var capturedWhileBusy=0;var watch=System.Diagnostics.Stopwatch.StartNew();
                    var producer=Task.Run(async()=>
                    {
                        reader.Set(ClipboardFixture.Snapshot(1,pngA));await service.NotifyChangedAsync();
                        await Task.Delay(300);reader.Set(ClipboardFixture.Snapshot(2,pngB));await service.NotifyChangedAsync();Interlocked.Exchange(ref capturedWhileBusy,reader.ReadCount);
                    });
                    // Simulate a busy screenshot/translation UI; the capture STA must keep running.
                    Thread.Sleep(700);Assert.Equal(2,Volatile.Read(ref capturedWhileBusy));await producer;
                    await ClipboardFixture.Until(()=>window.VisibleRowCount==2);
                    Assert.True(watch.Elapsed<TimeSpan.FromMilliseconds(2300));Assert.Equal(0,service.State.FailureCount);
                    var rows=fixture.Database.SearchClipboard();Assert.Equal(pngB,rows[0].ImagePng);Assert.Equal(pngA,rows[1].ImagePng);done.TrySetResult();
                }
                catch(Exception ex){done.TrySetException(ex);}
                finally{window.Close();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            };
            window.Show();Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await done.Task.WaitAsync(TimeSpan.FromSeconds(10));}
        finally{if(thread.IsAlive)Dispatcher.FromThread(thread)?.BeginInvokeShutdown(DispatcherPriority.Send);thread.Join(3000);}
    }

    [Fact]
    public async Task PendingStateAndIncrementalRefreshPreserveSelectionAndViewport()
    {
        if(string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))return;
        using var fixture=new ClipboardFixture();
        for(var i=0;i<60;i++)fixture.Database.AddClipboard(new(0,"text",$"synthetic {i}",$"synthetic {i}",null,null,null,false,DateTimeOffset.Now,$"h-{i}"),500);
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var reader=new FakeClipboardReader();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader,async(snapshot,token)=>
            {
                await release.Task.WaitAsync(token);
                return fixture.Database.AddClipboard(new(0,"text",snapshot.Text!,snapshot.Text,null,null,null,false,snapshot.CreatedAt,$"new-{snapshot.Sequence}"),500);
            });
            var window=new ClipboardWindow(fixture.Database,service,IntPtr.Zero)
            {WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false,Topmost=false};
            window.ContentRendered+=async(_,_)=>
            {
                try
                {
                    await ClipboardFixture.Until(()=>window.VisibleRowCount==60);
                    var grid=Find<System.Windows.Controls.DataGrid>(window)!;grid.SelectedIndex=30;grid.ScrollIntoView(grid.SelectedItem);grid.UpdateLayout();
                    var selected=((ClipboardViewRow)grid.SelectedItem).Id;var scroll=Find<System.Windows.Controls.ScrollViewer>(grid)!;
                    var anchor=((ClipboardViewRow)grid.Items[(int)scroll.VerticalOffset]).Id;
                    reader.Set(ClipboardFixture.Snapshot(100));await service.NotifyChangedAsync();
                    await ClipboardFixture.Until(()=>window.SaveStatusText.Contains("正在保存 1 条"));
                    release.SetResult();
                    var producer=Task.Run(async()=>
                    {
                        for(uint i=101;i<121;i++){reader.Set(ClipboardFixture.Snapshot(i));await service.NotifyChangedAsync();await Task.Delay(60);}
                    });
                    await ClipboardFixture.Until(()=>window.VisibleRowCount>61);
                    Assert.False(producer.IsCompleted); // Refresh must happen before a continuous burst finishes.
                    await producer;await ClipboardFixture.Until(()=>window.VisibleRowCount==81);
                    Assert.Equal(selected,((ClipboardViewRow)grid.SelectedItem).Id);
                    Assert.Equal(anchor,((ClipboardViewRow)grid.Items[(int)scroll.VerticalOffset]).Id);
                    await ClipboardFixture.Until(()=>window.SaveStatusText.Contains("均已保存"));
                    Assert.True(await service.StopAsync());finished.TrySetResult();
                }
                catch(Exception ex){finished.TrySetException(ex);}
                finally{window.Close();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            };
            window.Show();Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));}
        finally{if(thread.IsAlive)Dispatcher.FromThread(thread)?.BeginInvokeShutdown(DispatcherPriority.Send);thread.Join(3000);}
    }

    private static T? Find<T>(System.Windows.DependencyObject root) where T:System.Windows.DependencyObject
    {
        if(root is T value)return value;
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
        {var found=Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(root,i));if(found is not null)return found;}return null;
    }

    [Fact]
    public async Task WindowRendersLoadingStateBeforeASlowHistoryQueryCompletes()
    {
        // Hosted Windows runners do not have an interactive compositor and never raise WPF's
        // ContentRendered event. The same binaries still build and all non-visual loading tests run
        // there; this first-frame assertion is exercised on the interactive release workstation.
        if(string.Equals(Environment.GetEnvironmentVariable("CI"),"true",StringComparison.OrdinalIgnoreCase))return;
        var root=Path.Combine(Path.GetTempPath(),"Gtools-ClipboardWindow-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var result=new TaskCompletionSource<(string FirstStatus,int FirstRows,string FinalStatus,int FinalRows,bool QueryWasStillRunning)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(() =>
        {
            ClipboardHistoryService? service=null;ClipboardWindow? window=null;
            try
            {
                var db=new LocalDatabase(Path.Combine(root,"gtools.db"));db.Initialize();service=new ClipboardHistoryService(db,()=>500);
                var queryFinished=0;
                IReadOnlyList<ClipboardEntrySummary> SlowQuery(string _,int __)
                {
                    Thread.Sleep(600);Interlocked.Exchange(ref queryFinished,1);
                    return [new ClipboardEntrySummary(1,"text","合成记录",false,DateTimeOffset.Now,false)];
                }
                window=new ClipboardWindow(db,service,IntPtr.Zero,SlowQuery)
                {
                    WindowStartupLocation=System.Windows.WindowStartupLocation.Manual,Left=-20_000,Top=-20_000,
                    ShowInTaskbar=false,ShowActivated=false,Topmost=false
                };
                string firstStatus="";var firstRows=-1;var queryWasStillRunning=false;
                window.ContentRendered+=(_,_)=>
                {
                    firstStatus=window.StatusText;firstRows=window.VisibleRowCount;queryWasStillRunning=Volatile.Read(ref queryFinished)==0;
                };
                var timer=new DispatcherTimer(TimeSpan.FromMilliseconds(20),DispatcherPriority.Background,(_,_)=>
                {
                    if(window.VisibleRowCount!=1)return;
                    result.TrySetResult((firstStatus,firstRows,window.StatusText,window.VisibleRowCount,queryWasStillRunning));
                    window.Close();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                },Dispatcher.CurrentDispatcher);
                timer.Start();window.Show();Dispatcher.Run();timer.Stop();
            }
            catch(Exception ex){result.TrySetException(ex);Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);}
            finally{try{window?.Close();}catch{}service?.Dispose();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try
        {
            var completed=await Task.WhenAny(result.Task,Task.Delay(TimeSpan.FromSeconds(8)));Assert.Same(result.Task,completed);
            var state=await result.Task;Assert.Equal("正在加载历史记录…",state.FirstStatus);Assert.Equal(0,state.FirstRows);Assert.True(state.QueryWasStillRunning);
            Assert.Equal(1,state.FinalRows);Assert.Contains("显示 1 条",state.FinalStatus);
        }
        finally
        {
            if(thread.IsAlive)Dispatcher.FromThread(thread)?.BeginInvokeShutdown(DispatcherPriority.Send);
            thread.Join(TimeSpan.FromSeconds(2));try{Directory.Delete(root,true);}catch{}
        }
    }
}
