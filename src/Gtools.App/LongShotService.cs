using Gtools.Core;

namespace Gtools.App;

/// <summary>Manual capture, explicit pair review, immutable confirmed seams, then lossless composition.</summary>
internal sealed class LongShotService(LongShotWorkerClient worker,GtoolsSettings settings)
{
    private const long MaxOutputPixels=170_000_000;
    public async Task<LongShotResponse?> CaptureAsync()
    {
        var root=Path.Combine(AppPaths.Temp,"longshot-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var progress=new LongShotProgressWindow(settings.LongShotScrollUp);
        var session=new ReviewedLongShotSession(settings.LongShotScrollUp);
        string? output=null;
        try
        {
            System.Drawing.Rectangle region;
            LongShotTargetGuard guard;
            while(true)
            {
                var selected=ScreenCaptureService.SelectRegion();if(selected is null)return null;
                region=selected.Value;
                if(region.Width<100||region.Height<100){MessageBox.Show("请框选足够大的消息区域。","Gtools");continue;}
                await Task.Delay(180,progress.Token);guard=new LongShotTargetGuard(region);
                var first=await CaptureStableAsync(region,guard,progress,progress.Token);
                if(first.Png is null){MessageBox.Show(first.Error,"Gtools · 第一帧未采集");continue;}
                var firstPath=Path.Combine(root,"first.png");await File.WriteAllBytesAsync(firstPath,first.Png,progress.Token);
                var firstReview=new LongShotFirstFrameWindow(firstPath);
                using var cancelFirst=progress.Token.Register(()=>firstReview.Dispatcher.BeginInvoke(new Action(()=>{if(firstReview.IsVisible)firstReview.Close();})));
                if(firstReview.ShowDialog()==true){session.AddFirst(firstPath);break;}
                if(!firstReview.Reselect)return null;
            }
            progress.Show();progress.Place(region);
            string? status=null;
            var maxFrames=Math.Clamp(settings.LongShotMaxFrames,2,400);
            while(true)
            {
                var action=await progress.WaitAsync(session.Frames.Count,status);status=null;
                if(action==LongShotCaptureAction.Finish)break;
                if(action==LongShotCaptureAction.Undo)
                {
                    var removed=session.Undo();if(removed is not null)File.Delete(removed);
                    status="已撤销上一帧。请参照最后一帧的位置恢复聊天滚动，再继续采集。";continue;
                }
                if(session.Frames.Count>=maxFrames){status="已达到最大帧数。请完成当前图片，或撤销最后一帧。";continue;}
                var captured=await CaptureStableAsync(region,guard,progress,progress.Token);
                if(captured.Png is null){status=captured.Error;continue;}
                var candidate=Path.Combine(root,"frame-"+Guid.NewGuid().ToString("N")+".png");
                await File.WriteAllBytesAsync(candidate,captured.Png,progress.Token);
                var upper=settings.LongShotScrollUp?candidate:session.LastFrame!;
                var lower=settings.LongShotScrollUp?session.LastFrame!:candidate;
                var review=new LongShotSeamWindow(upper,lower,Path.Combine(root,"pair.png"),worker){Owner=progress};
                var accepted=review.ShowDialog()==true;
                await review.WaitForIdleAsync();
                if(!accepted||review.Seam is null)
                {
                    File.Delete(candidate);
                    if(review.Cancelled)return null;
                    status="本帧已丢弃。请调整滚动位置，保留半屏重叠后再采集。";continue;
                }
                if((session.OutputHeight(region.Height)+review.Seam.Offset)*region.Width>MaxOutputPixels)
                {
                    File.Delete(candidate);status="本帧超过长图安全上限，尚未加入。请完成当前图片。";continue;
                }
                try{session.Add(candidate,review.Seam,region.Height);}
                catch(InvalidOperationException ex){File.Delete(candidate);status=ex.Message;continue;}
                DiagnosticLog.Write("longshot","seam.confirmed","接缝已确认",data:new Dictionary<string,object?>
                    {{"frame",session.Frames.Count},{"offset",review.Seam.Offset},{"cutY",review.Seam.CutY},{"source",review.Seam.Source.ToString()}});
            }
            progress.SetStatus("正在按已确认接缝生成完整图与分段图…");
            var export=session.Export();var id=Guid.NewGuid().ToString("N");
            output=Path.Combine(AppPaths.LongShots,LongShotOutputNaming.CreateFileName(DateTime.Now,id));
            var result=await worker.StitchAsync(new(id,export.Frames,output,settings.LongShotSegmentHeight,
                Profile:settings.LongShotProfile,DiagnosticsPath:Path.Combine(AppPaths.Logs,"longshot-"+id+".json"),
                ConfirmedSeams:export.Seams),progress.Token);
            return result;
        }
        catch(OperationCanceledException) when(progress.Token.IsCancellationRequested)
        {
            if(output is not null)DeletePartialOutputs(output);return null;
        }
        catch
        {
            if(output is not null)DeletePartialOutputs(output);throw;
        }
        finally
        {
            progress.Complete();
            await LongShotTempCleanup.DeleteAsync(root);
        }
    }

    private static async Task<(byte[]? Png,string? Error)> CaptureStableAsync(System.Drawing.Rectangle region,
        LongShotTargetGuard guard,LongShotProgressWindow progress,CancellationToken token)
    {
        var wasVisible=progress.IsVisible;
        if(wasVisible)progress.Hide();
        try
        {
            await Task.Delay(160,token);
            var tracker=new FrameStabilityTracker();var started=Environment.TickCount64;
            while(Environment.TickCount64-started<3000)
            {
                token.ThrowIfCancellationRequested();
                if(guard.Check() is {} problem)return(null,problem);
                var frame=ScreenCaptureService.CaptureStableSample(region);
                if(tracker.Observe(frame.Sample,Environment.TickCount64))
                {
                    if(guard.Check() is {} changed)return(null,changed);
                    return(frame.Png,null);
                }
                await Task.Delay(80,token);
            }
            return(null,"画面在三秒内仍未稳定，本帧未采集。请等待图片加载，关闭固定提示或避开动画后重试。");
        }
        finally{if(wasVisible&&!token.IsCancellationRequested)progress.Show();}
    }

    private static void DeletePartialOutputs(string output)
    {
        var directory=Path.GetDirectoryName(output);if(directory is null||!Directory.Exists(directory))return;
        var prefix=Path.GetFileNameWithoutExtension(output);
        foreach(var file in Directory.EnumerateFiles(directory,prefix+"*.png"))
            try{File.Delete(file);}catch(Exception ex){DiagnosticLog.Write("longshot","partial.cleanup.failed",ex.Message,"Warning");}
    }
}

internal static class LongShotTempCleanup
{
    internal static async Task DeleteAsync(string session)
    {
        for(var attempt=0;attempt<4;attempt++)
        {
            try{if(Directory.Exists(session))Directory.Delete(session,true);return;}
            catch(Exception ex)
            {
                if(attempt==3)DiagnosticLog.Write("longshot","temp.cleanup.failed",ex.Message,"Warning");
                else await Task.Delay(150);
            }
        }
    }
}
