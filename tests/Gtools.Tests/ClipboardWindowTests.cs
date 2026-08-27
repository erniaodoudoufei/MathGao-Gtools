using System.Windows.Threading;
using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class ClipboardWindowTests
{
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
