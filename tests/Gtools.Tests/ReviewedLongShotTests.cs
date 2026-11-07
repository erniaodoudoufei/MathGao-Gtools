using Gtools.Core;
using Gtools.LongShotWorker;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class ReviewedLongShotTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void NumberedChatWithRepeatedBubblesIsReconstructedPixelForPixel(bool up)
    {
        using var chat=new SyntheticChat();var session=new ReviewedLongShotSession(up);
        var frames=chat.Frames.ToArray();if(up)Array.Reverse(frames);session.AddFirst(frames[0]);
        for(var i=1;i<frames.Length;i++)
        {
            var upper=up?frames[i]:frames[i-1];var lower=up?frames[i-1]:frames[i];
            var analysis=PairAnalyzer.Analyze(new("pair",upper,lower,Path.Combine(chat.Root,"preview.png")));
            Assert.True(analysis.CanConfirm,analysis.Message);Assert.Equal(SyntheticChat.Step,analysis.Offset);
            session.Add(frames[i],new(upper,lower,analysis.Offset,analysis.CutY,LongShotAlignmentSource.Suggested,analysis.Confidence),SyntheticChat.FrameHeight);
        }
        var export=session.Export();var output=Path.Combine(chat.Root,"output.png");
        var result=WorkerProgram.Stitch(new("chat",export.Frames,output,800,ConfirmedSeams:export.Seams));
        Assert.True(result.Success,result.Error);using var final=SKBitmap.Decode(output);
        Assert.Equal(chat.Full.Height,final.Height);Assert.True(chat.Full.Bytes.SequenceEqual(final.Bytes),"最终图必须与带编号的完整原图逐像素一致，不允许缺行、重复或重绘。");
        Assert.NotEmpty(result.SegmentPaths);
        var y=0;foreach(var segment in result.SegmentPaths)
        {
            using var part=SKBitmap.Decode(segment);
            for(var row=0;row<part.Height;row++)Assert.Equal(final.GetPixel(40,y+row),part.GetPixel(40,row));
            y+=800-80;
        }
    }

    [Fact]
    public void ManualAnchorsAndCutAreUsedExactlyAtExport()
    {
        using var chat=new SyntheticChat(2);
        var analysis=PairAnalyzer.Analyze(new("manual",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"preview.png"),SyntheticChat.Step,410));
        Assert.True(analysis.CanConfirm,analysis.Message);
        var seam=new ConfirmedLongShotSeam(chat.Frames[0],chat.Frames[1],analysis.Offset,410,LongShotAlignmentSource.ManualAnchors,0);
        var result=WorkerProgram.Stitch(new("manual",chat.Frames.ToArray(),Path.Combine(chat.Root,"out.png"),8000,ConfirmedSeams:new[]{seam}));
        Assert.True(result.Success,result.Error);Assert.Equal(410,result.Diagnostics!.Seams[0].CutY);
        using var final=SKBitmap.Decode(result.OutputPath);Assert.True(chat.Full.Bytes.SequenceEqual(final.Bytes));
    }

    [Fact]
    public void NoConfirmedSeamsMeansNoOutputEvenWithConfidentLegacyHint()
    {
        using var chat=new SyntheticChat(2);var output=Path.Combine(chat.Root,"must-not-exist.png");
        var result=WorkerProgram.Stitch(new("unsafe",chat.Frames.ToArray(),output,8000,OffsetHints:new[]{new LongShotOffsetHint(50,1,true)}));
        Assert.False(result.Success);Assert.False(File.Exists(output));
    }

    [Fact]
    public void RepeatedSameScreenAndIncorrectFiftyPixelAlignmentCannotBeConfirmed()
    {
        using var chat=new SyntheticChat(2);
        Assert.False(PairAnalyzer.Analyze(new("duplicate",chat.Frames[0],chat.Frames[0],Path.Combine(chat.Root,"a.png"),240,400)).CanConfirm);
        Assert.False(PairAnalyzer.Analyze(new("wrong",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"b.png"),50,400)).CanConfirm);
        Assert.False(PairAnalyzer.Analyze(new("reverse",chat.Frames[1],chat.Frames[0],Path.Combine(chat.Root,"c.png"))).CanConfirm);
    }

    [Theory][InlineData("blank")][InlineData("borders")][InlineData("periodic")][InlineData("noise")]
    public void BlankBordersRepeatingRowsAndUnrelatedNoiseNeverSuggestAConfirmedAlignment(string kind)
    {
        using var chat=new SyntheticChat(2);
        for(var index=0;index<2;index++)
        {
            using var bitmap=new SKBitmap(640,600);using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.White);
            using var paint=new SKPaint{Color=new SKColor(235,235,235)};
            if(kind=="borders")canvas.DrawRect(70,0,360,600,paint);
            if(kind=="periodic")for(var y=0;y<600;y+=50){paint.Color=SKColors.Black;canvas.DrawRect(40,y,320,5,paint);}
            if(kind=="noise")
            {
                var bytes=bitmap.Bytes;new Random(index+5).NextBytes(bytes);System.Runtime.InteropServices.Marshal.Copy(bytes,0,bitmap.GetPixels(),bytes.Length);
            }
            SyntheticChat.Save(bitmap,chat.Frames[index]);
        }
        var analysis=PairAnalyzer.Analyze(new("ambiguous",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"preview.png")));
        Assert.False(analysis.CanConfirm);
        if(kind=="noise")Assert.False(PairAnalyzer.Analyze(new("noise",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"manual.png"),180,400)).CanConfirm);
    }

    [Fact]
    public void FixedOverlayAndLazyLoadAtTheSeamRequireCorrection()
    {
        using var chat=new SyntheticChat(2);using(var frame=SKBitmap.Decode(chat.Frames[1]))
        {
            using var c=new SKCanvas(frame);using var p=new SKPaint{Color=SKColors.LimeGreen};c.DrawRect(100,200,400,160,p);
            SyntheticChat.Save(frame,chat.Frames[1]);
        }
        var result=PairAnalyzer.Analyze(new("changed",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"preview.png"),SyntheticChat.Step,420));
        Assert.False(result.CanConfirm);Assert.True(result.ChangedRatio>0);
    }
}

internal sealed class SyntheticChat : IDisposable
{
    internal const int FrameHeight=600,Step=240;
    internal string Root{get;}=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
    internal List<string> Frames{get;}=new();
    internal SKBitmap Full{get;}
    internal SyntheticChat(int frameCount=11)
    {
        Directory.CreateDirectory(Root);Full=new SKBitmap(640,FrameHeight+Step*(frameCount-1));
        using(var c=new SKCanvas(Full))
        {
            c.Clear(new SKColor(250,250,250));using var text=new SKPaint{Color=SKColors.Black,TextSize=17,IsAntialias=true};
            using var bubble=new SKPaint{Color=new SKColor(235,235,235)};using var avatar=new SKPaint{Color=SKColors.SlateGray};
            for(int y=10,index=1;y<Full.Height;y+=193,index++)
            {
                var x=index%3==0?210:62;c.DrawRect(x,y,380,182,bubble);c.DrawRect(x-46,y,32,32,avatar);
                c.DrawText($"MSG {index:000} - numbered chat",x+12,y+25,text);
                c.DrawText("Repeated long message / same sender",x+12,y+50,text);
                c.DrawText($"Unique value: {index*379:000000}",x+12,y+75,text);
                // Large quiet portions mimic the user's troublesome gray message cards.
                c.DrawText("Repeated footer text",x+12,y+166,text);
                if(index%4==0)
                {
                    using var color=new SKPaint{Color=new SKColor((byte)(index*19%240),90,170)};
                    c.DrawCircle(x+90,y+116,15+index%7,color);c.DrawRect(x+120,y+101,28+index,22,color);
                }
            }
        }
        for(var i=0;i<frameCount;i++)
        {
            var path=Path.Combine(Root,$"frame-{i:00}.png");
            using var bitmap=new SKBitmap(640,FrameHeight);using(var c=new SKCanvas(bitmap))c.DrawBitmap(Full,0,-i*Step);
            Save(bitmap,path);Frames.Add(path);
        }
    }
    internal static void Save(SKBitmap image,string path)
    {using var data=image.Encode(SKEncodedImageFormat.Png,100);using var file=File.Create(path);data.SaveTo(file);}
    public void Dispose()
    {
        Full.Dispose();
        // Antivirus/indexing can briefly hold a just-written PNG even after our streams close.
        // A bounded cleanup retry must still fail the test if the handle never becomes available.
        for(var attempt=0;Directory.Exists(Root);attempt++)
        {
            try{Directory.Delete(Root,true);return;}
            catch(IOException) when(attempt<4){Thread.Sleep(40*(attempt+1));}
        }
    }
}
