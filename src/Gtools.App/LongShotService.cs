using Gtools.Core;

namespace Gtools.App;

/// <summary>
/// Captures scrolling content with a manual-first workflow. The capture loop measures real motion,
/// but only reliable measurements are allowed to constrain the isolated stitching worker.
/// </summary>
internal sealed class LongShotService(LongShotWorkerClient worker,GtoolsSettings settings)
{
    private const int WheelNotch=120;
    private const double TargetAdvanceRatio=.62;
    private const double MaxSafeAdvanceRatio=.86;
    private const long MaxOutputPixels=170_000_000;

    public async Task<LongShotResponse?> CaptureAsync()
    {
        var region=ScreenCaptureService.SelectRegion();if(region is null)return null;
        var rect=region.Value;
        var profile=settings.LongShotProfile;
        var edgeRatio=profile==LongShotCaptureProfile.QQ9?.08:.06;
        var scrollUp=settings.LongShotScrollUp;
        var maxFrames=Math.Clamp(settings.LongShotMaxFrames,2,400);
        var session=Path.Combine(AppPaths.Temp,"longshot-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(session);

        var progress=new LongShotProgressWindow{ShowActivated=false};
        var frames=new List<string>();var hints=new List<LongShotOffsetHint>();var warnings=new List<string>();
        var cursorOwned=false;var originalCursor=default(System.Drawing.Point);string? output=null;

        DiagnosticLog.Write("longshot","capture.started",$"开始 {profile} 采集",data:new Dictionary<string,object?>
            {{"x",rect.X},{"y",rect.Y},{"width",rect.Width},{"height",rect.Height},{"scrollUp",scrollUp},{"maxFrames",maxFrames}});

        try
        {
            progress.Show();
            var mustHide=!progress.TryPlaceOutside(rect);
            progress.SetStatus(mustHide?"进度窗与采集区重叠，将在每帧短暂隐藏。":"正在等待选区画面稳定…");

            var targetAdvance=Math.Max(40,(int)(rect.Height*TargetAdvanceRatio));
            var maxSafeAdvance=Math.Max(60,(int)(rect.Height*MaxSafeAdvanceRatio));
            double pixelsPerNotch=0;var notches=3;var stalled=0;long totalHeight=rect.Height;

            if(!await WaitUntilStableAsync(rect,edgeRatio,progress.Token))
                warnings.Add("首帧画面在等待时间内仍有变化，可能包含动画或懒加载内容。");
            var first=await GrabAsync(progress,mustHide,rect,edgeRatio,progress.Token);
            var previous=first.Signature;
            frames.Add(await SaveAsync(session,0,first.Png,progress.Token));progress.SetFrame(1);
            progress.SetStatus("第一帧已采集。请滚动聊天记录后点“采集下一帧”；也可以切换自动滚动。");

            while(frames.Count<maxFrames&&!progress.Token.IsCancellationRequested)
            {
                var action=await progress.WaitForNextCaptureAsync();
                if(action is null||progress.Token.IsCancellationRequested)break;
                if(action==LongShotCaptureAction.FinishWithoutCapture)break;

                var finishAfterCapture=action==LongShotCaptureAction.CaptureAndFinish;
                CapturedFrame frame;FrameAdvanceMeasurement measurement;
                if(action is LongShotCaptureAction.ManualCapture or LongShotCaptureAction.CaptureAndFinish)
                {
                    progress.SetStatus("正在等待滚动、图片加载和界面动画稳定…");
                    if(!await WaitUntilStableAsync(rect,edgeRatio,progress.Token))
                    {
                        progress.SetStatus("画面仍在变化，本帧没有保存。请稍等后重试。");
                        continue;
                    }
                    frame=await GrabAsync(progress,mustHide,rect,edgeRatio,progress.Token);
                    measurement=Measure(previous,frame.Signature,scrollUp);
                }
                else
                {
                    if(!cursorOwned)cursorOwned=NativeMethods.GetCursorPos(out originalCursor);
                    (frame,measurement,notches,pixelsPerNotch)=await AutoStepAsync(
                        progress,mustHide,rect,edgeRatio,scrollUp,notches,pixelsPerNotch,targetAdvance,maxSafeAdvance,previous);
                }

                var advance=measurement.SignedAdvance;
                if(advance<=-6)
                {
                    stalled=0;
                    progress.SetStatus($"检测到反向滚动 {Math.Abs(advance)}px，本帧未保存。请按当前采集方向滚动后重试。");
                    continue;
                }
                if(advance<6)
                {
                    if(action==LongShotCaptureAction.AutomaticStep&&progress.Mode==LongShotCaptureMode.Automatic)
                    {
                        stalled++;
                        progress.SetStatus(stalled==1?"自动滚动未检测到新内容，正在再试一次…":"自动滚动已停止。");
                        if(stalled>=2)
                        {
                            progress.SetMode(LongShotCaptureMode.Manual,"自动模式没有检测到新内容：若已到顶请直接完成，否则手动滚动后重试。");
                            stalled=0;
                        }
                    }
                    else
                    {
                        stalled=0;progress.SetStatus("未检测到新内容，本帧未保存。请继续滚动后重试，或直接完成。");
                    }
                    continue;
                }
                stalled=0;

                if(advance>maxSafeAdvance)
                {
                    progress.SetStatus($"一次滚动了 {advance}px，超过安全范围 {maxSafeAdvance}px，本帧未保存。请回退并缩小滚动距离。");
                    if(progress.Mode==LongShotCaptureMode.Automatic)
                        progress.SetMode(LongShotCaptureMode.Manual,"自动滚动重叠不足，已切换手动。请回退一些后小幅滚动并重试。");
                    continue;
                }

                var acceptedUnreliable=false;
                if(!measurement.IsReliable)
                {
                    if(progress.Mode==LongShotCaptureMode.Automatic||action==LongShotCaptureAction.AutomaticStep)
                    {
                        progress.SetMode(LongShotCaptureMode.Manual,"这一屏无法可靠测量滚动距离，未保存并已切换手动。请稍后重试。");
                        continue;
                    }
                    if(!progress.ConfirmUnreliableCapture(measurement.Confidence))
                    {
                        progress.SetStatus("本帧未保存。请调整滚动位置后重试。");
                        continue;
                    }
                    acceptedUnreliable=true;
                    warnings.Add($"第 {frames.Count+1} 帧由用户确认保留，但位移置信度仅 {measurement.Confidence:0.00}，接缝需要检查。");
                }

                frames.Add(await SaveAsync(session,frames.Count,frame.Png,progress.Token));
                hints.Add(new LongShotOffsetHint(advance,measurement.Confidence,measurement.IsReliable&&!acceptedUnreliable));
                previous=frame.Signature;totalHeight+=advance;progress.SetFrame(frames.Count);
                progress.SetStatus($"已采集 {frames.Count} 帧 · 本帧前进 {advance}px · 累计约 {totalHeight}px");

                if(finishAfterCapture)break;
                if((long)rect.Width*totalHeight>MaxOutputPixels)
                {
                    warnings.Add("已达到单张长图的安全上限，本次在当前帧结束，可继续采集下一张。");
                    progress.NotifySafetyLimit("长图已达到安全像素上限，将完成当前图片。需要更多内容时请另开一张继续采集。");
                    break;
                }
            }

            if(progress.Token.IsCancellationRequested||progress.WasCancelled)return null;
            if(frames.Count>=maxFrames)
            {
                warnings.Add($"已达到最多 {maxFrames} 帧，本次长图在这里结束。");
                progress.NotifySafetyLimit($"已达到设置的最大帧数 {maxFrames}，将完成当前图片；后续内容请另开一张采集。");
            }
            if(frames.Count<1)return null;

            progress.SetProcessing("正在匹配接缝并生成完整图与分段图，请稍候…");
            if(scrollUp){frames.Reverse();hints.Reverse();}

            var requestId=Guid.NewGuid().ToString("N");
            output=Path.Combine(AppPaths.LongShots,LongShotOutputNaming.CreateFileName(DateTime.Now,requestId));
            var request=new LongShotRequest(requestId,frames.ToArray(),output,settings.LongShotSegmentHeight,
                Profile:profile,EdgeExclusionRatio:edgeRatio,DiagnosticsPath:Path.Combine(AppPaths.Logs,$"longshot-{requestId}.json"),
                OffsetHints:hints.ToArray());
            var result=await worker.StitchAsync(request,progress.Token);
            DiagnosticLog.Write("longshot","capture.completed",result.Success?"长截图完成":"长截图失败",result.Success?"Info":"Error",requestId,
                new Dictionary<string,object?>{{"frames",frames.Count},{"requiresReview",result.RequiresReview},{"output",result.OutputPath}});
            progress.CloseAfterCompletion();
            return warnings.Count==0?result:result with{Warnings=result.Warnings.Concat(warnings).ToArray(),RequiresReview=true};
        }
        catch(OperationCanceledException) when(progress.WasCancelled||progress.Token.IsCancellationRequested)
        {
            if(output is not null)DeletePartialOutputs(output);
            DiagnosticLog.Write("longshot","capture.cancelled","用户取消了长截图");
            return null;
        }
        finally
        {
            if(progress.IsVisible)progress.CloseAfterCompletion();
            if(cursorOwned)NativeMethods.SetCursorPos(originalCursor.X,originalCursor.Y);
            await DeleteSessionAsync(session);
        }
    }

    private static async Task<(CapturedFrame Frame,FrameAdvanceMeasurement Measurement,int Notches,double PixelsPerNotch)> AutoStepAsync(
        LongShotProgressWindow progress,bool mustHide,System.Drawing.Rectangle rect,double edgeRatio,bool scrollUp,
        int notches,double pixelsPerNotch,int targetAdvance,int maxSafeAdvance,FrameSignature previous)
    {
        CapturedFrame frame;FrameAdvanceMeasurement measurement;
        for(var attempt=0;;attempt++)
        {
            NativeMethods.SetCursorPos(rect.Left+rect.Width/2,rect.Top+rect.Height/2);
            if(attempt==0)
            {
                NativeMethods.mouse_event(0x0800,0,0,(scrollUp?1:-1)*notches*WheelNotch,UIntPtr.Zero);
                await WaitUntilStableAsync(rect,edgeRatio,progress.Token);
            }
            frame=await GrabAsync(progress,mustHide,rect,edgeRatio,progress.Token);
            measurement=Measure(previous,frame.Signature,scrollUp);

            var advance=measurement.SignedAdvance;
            if(attempt==0&&advance>0&&measurement.IsReliable)
            {
                var measured=advance/(double)Math.Max(1,notches);
                pixelsPerNotch=pixelsPerNotch<=0?measured:pixelsPerNotch*.6+measured*.4;
                notches=Math.Clamp((int)Math.Round(targetAdvance/Math.Max(1,pixelsPerNotch)),1,15);
            }
            if(attempt>=1||advance<=maxSafeAdvance||pixelsPerNotch<=0)
                return(frame,measurement,notches,pixelsPerNotch);
            var back=Math.Max(1,(int)Math.Round((advance-targetAdvance)/pixelsPerNotch));
            progress.SetStatus($"滚动过头 {advance}px，正在回退 {back} 格重采…");
            NativeMethods.mouse_event(0x0800,0,0,(scrollUp?-1:1)*back*WheelNotch,UIntPtr.Zero);
            await WaitUntilStableAsync(rect,edgeRatio,progress.Token);
        }
    }

    private static FrameAdvanceMeasurement Measure(FrameSignature previous,FrameSignature current,bool scrollUp)
    {
        var estimate=FrameAlignment.EstimateShift(previous,current);
        var signedAdvance=scrollUp?estimate.Shift:-estimate.Shift;
        return new(signedAdvance,estimate.Confidence,estimate.IsReliable);
    }

    internal static async Task<bool> WaitUntilStableAsync(System.Drawing.Rectangle rect,double edgeRatio,CancellationToken token)
    {
        await Task.Delay(120,token);
        var last=ScreenCaptureService.CaptureSignature(rect,edgeRatio);
        var deadline=Environment.TickCount64+1_200;var stableSamples=0;
        while(Environment.TickCount64<deadline)
        {
            await Task.Delay(70,token);
            var current=ScreenCaptureService.CaptureSignature(rect,edgeRatio);
            var estimate=FrameAlignment.EstimateShift(last,current);last=current;
            if(Math.Abs(estimate.Shift)<=2)
            {
                stableSamples++;
                if(stableSamples>=2)return true;
            }
            else stableSamples=0;
        }
        return false;
    }

    private static async Task<CapturedFrame> GrabAsync(LongShotProgressWindow progress,bool mustHide,System.Drawing.Rectangle rect,double edgeRatio,CancellationToken token)
    {
        if(!mustHide)return ScreenCaptureService.CaptureFrame(rect,edgeRatio);
        progress.Hide();await Task.Delay(140,token);
        try{return ScreenCaptureService.CaptureFrame(rect,edgeRatio);}
        finally{if(!progress.Token.IsCancellationRequested)progress.Show();}
    }

    private static async Task<string> SaveAsync(string session,int index,byte[] png,CancellationToken token)
    {
        var path=Path.Combine(session,$"frame-{index:000}.png");await File.WriteAllBytesAsync(path,png,token);return path;
    }

    private static async Task DeleteSessionAsync(string session)
    {
        Exception? last=null;
        for(var attempt=0;attempt<3;attempt++)
        {
            try{if(Directory.Exists(session))Directory.Delete(session,true);return;}
            catch(Exception ex){last=ex;if(attempt<2)await Task.Delay(120*(attempt+1));}
        }
        DiagnosticLog.Write("longshot","temp.cleanup.failed",last?.Message??"未知错误","Warning",data:new Dictionary<string,object?>{{"session",Path.GetFileName(session)}});
    }

    private static void DeletePartialOutputs(string output)
    {
        try
        {
            var directory=Path.GetDirectoryName(output);if(directory is null||!Directory.Exists(directory))return;
            var prefix=Path.GetFileNameWithoutExtension(output);
            foreach(var file in Directory.EnumerateFiles(directory,prefix+"*.png"))File.Delete(file);
        }
        catch(Exception ex){DiagnosticLog.Write("longshot","partial.cleanup.failed",ex.Message,"Warning");}
    }
}
