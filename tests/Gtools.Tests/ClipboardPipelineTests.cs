using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gtools.App;
using Gtools.Core;
using SkiaSharp;
using Xunit.Abstractions;

namespace Gtools.Tests;

public sealed class ClipboardPipelineTests(ITestOutputHelper output)
{
    [Theory][InlineData(100)][InlineData(300)][InlineData(1000)]
    public async Task TwoDifferentImagesKeepCaptureOrderAndSurviveReopening(int delay)
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();
        using(var service=new ClipboardHistoryService(fixture.Database,()=>500,reader))
        {
            var first=ClipboardFixture.Png(SKColors.Blue,2048);var second=ClipboardFixture.Png(SKColors.Orange,2048);
            reader.Set(ClipboardFixture.Snapshot(1,first));await service.NotifyChangedAsync();
            await Task.Delay(delay);reader.Set(ClipboardFixture.Snapshot(2,second));var started=Stopwatch.StartNew();await service.NotifyChangedAsync();
            await ClipboardFixture.Until(()=>service.State.SavedCount==2);
            output.WriteLine($"two 2048px images / interval {delay}ms / final saved {started.Elapsed.TotalMilliseconds:F1}ms");
            Assert.True(started.Elapsed<TimeSpan.FromSeconds(2));Assert.Equal(0,service.State.FailureCount);
            Assert.All(reader.Apartments,a=>Assert.Equal(ApartmentState.STA,a));
            Assert.DoesNotContain(Environment.CurrentManagedThreadId,reader.Threads);
            var rows=fixture.Database.SearchClipboard();Assert.Equal(second,rows[0].ImagePng);Assert.Equal(first,rows[1].ImagePng);
            Assert.True(await service.StopAsync());
        }
        var reopened=new LocalDatabase(fixture.File);Assert.Equal(2,reopened.SearchClipboard().Count);
    }

    [Fact]
    public async Task SlowWriterDoesNotDelayCaptureAndShowsPendingCount()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var saved=new ConcurrentQueue<uint>();
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader,async(snapshot,token)=>
        {await release.Task.WaitAsync(token);saved.Enqueue(snapshot.Sequence);return true;});
        var states=new ConcurrentQueue<ClipboardSaveState>();service.StateChanged+=(_,_)=>states.Enqueue(service.State);
        reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();
        reader.Set(ClipboardFixture.Snapshot(2));await service.NotifyChangedAsync();
        Assert.Equal(2,reader.ReadCount);Assert.Equal(2,service.State.PendingCount);Assert.Empty(saved);
        Assert.Contains(states,x=>x.PendingCount==2&&x.Message.Contains("正在保存 2 条"));
        release.SetResult();Assert.True(await service.StopAsync());Assert.Equal(new uint[]{1,2},saved.ToArray());
    }

    [Fact]
    public async Task DuplicateNotificationsDoNotReadAgainAndOwnRestoresAreExcluded()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        reader.Set(ClipboardFixture.Snapshot(10));for(var i=0;i<20;i++)await service.NotifyChangedAsync();
        Assert.Equal(1,reader.ReadCount);
        reader.Set(ClipboardFixture.Snapshot(11) with{IsOwnRestore=true});await service.NotifyChangedAsync();
        Assert.True(await service.StopAsync());Assert.Single(fixture.Database.SearchClipboard());
    }

    [Fact]
    public async Task SamePixelsCopiedTwiceAreDeduplicatedButDifferentPixelsAreNot()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        var png=ClipboardFixture.Png(SKColors.Blue,48);
        foreach(var snapshot in new[]{ClipboardFixture.Snapshot(1,png),ClipboardFixture.Snapshot(2,png),ClipboardFixture.Snapshot(3,ClipboardFixture.Png(SKColors.Red,48))})
        {reader.Set(snapshot);await service.NotifyChangedAsync();}
        Assert.True(await service.StopAsync());Assert.Equal(2,fixture.Database.SearchClipboard().Count);
    }

    [Fact]
    public async Task ClipboardBusyIsRetriedWithoutAnotherNotification()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader{FailuresRemaining=2};using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();Assert.Contains("正在读取",service.State.Message);await ClipboardFixture.Until(()=>service.State.SavedCount==1);
        Assert.Equal(3,reader.ReadCount);Assert.Equal(0,service.State.FailureCount);
    }

    [Fact]
    public async Task NewClipboardContentDuringRetryNeverUsesTheOldSequence()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader{FailuresRemaining=1};
        var captured=new ConcurrentQueue<uint>();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader,(snapshot,_)=>{captured.Enqueue(snapshot.Sequence);return Task.FromResult(true);});
        reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();reader.Set(ClipboardFixture.Snapshot(2));
        await ClipboardFixture.Until(()=>service.State.SavedCount==1);Assert.Equal(new uint[]{2},captured.ToArray());
    }

    [Fact]
    public async Task DelayedRenderingSequenceIsUsedAndRetriesEventuallyReportFailure()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        reader.Set(ClipboardFixture.Snapshot(2));reader.AdvertisedSequence=1;await service.NotifyChangedAsync();
        reader.AdvertisedSequence=2;await service.NotifyChangedAsync();Assert.Equal(1,reader.ReadCount);
        reader.Set(ClipboardFixture.Snapshot(3));reader.FailuresRemaining=20;await service.NotifyChangedAsync();
        await ClipboardFixture.Until(()=>service.State.FailureCount==1);
        Assert.Contains("重新复制",service.State.Message);Assert.False(await service.StopAsync());
    }

    [Fact]
    public async Task BrokenImageDoesNotBlockTheFollowingRecord()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        reader.Set(ClipboardFixture.Snapshot(1,new byte[]{1,2,3}));await service.NotifyChangedAsync();
        reader.Set(ClipboardFixture.Snapshot(2));await service.NotifyChangedAsync();Assert.False(await service.StopAsync());
        Assert.Equal(1,service.State.FailureCount);Assert.Single(fixture.Database.SearchClipboard());Assert.Contains("保存失败",service.State.Message);
    }

    [Fact]
    public async Task RichTextAndImagesKeepFormatsWhilePausedEventsAreNotRecorded()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);
        service.Paused=true;reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();Assert.Equal(0,reader.ReadCount);
        service.Paused=false;var rich=ClipboardFixture.Snapshot(2) with{Text="synthetic plain",Html="<b>synthetic</b>",Rtf="{\\rtf1 synthetic}"};
        reader.Set(rich);await service.NotifyChangedAsync();reader.Set(ClipboardFixture.Snapshot(3,ClipboardFixture.Png(SKColors.Green,128)));await service.NotifyChangedAsync();
        Assert.True(await service.StopAsync());var rows=fixture.Database.SearchClipboard();Assert.Equal(2,rows.Count);
        Assert.Equal("image",rows[0].Kind);Assert.Equal(rich.Html,rows[1].Html);Assert.Equal(rich.Rtf,rows[1].Rtf);Assert.Equal(rich.Text,rows[1].Text);
    }

    [Fact]
    public async Task DatabaseWriteFailureIsVisibleAndDoesNotStopLaterWrites()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader,(snapshot,_)=>
            snapshot.Sequence==1?throw new IOException("synthetic write failure"):Task.FromResult(true));
        reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();reader.Set(ClipboardFixture.Snapshot(2));await service.NotifyChangedAsync();
        Assert.False(await service.StopAsync());Assert.Equal(1,service.State.SavedCount);Assert.Equal(1,service.State.FailureCount);Assert.Equal(0,service.State.PendingCount);
    }

    [Fact]
    public void BitmapFallbackAndTransparentPngKeepOriginalContent()
    {
        var png=ClipboardFixture.Png(new SKColor(80,120,180,110),128);var prepared=ClipboardImageCodec.Prepare(ClipboardFixture.Snapshot(1,png));Assert.Equal(png,prepared.Png);
        var decoded=ClipboardImageCodec.Decode(png);var encoder=new BmpBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(decoded));using var stream=new MemoryStream();encoder.Save(stream);
        var dib=stream.ToArray()[14..];var result=ClipboardImageCodec.Prepare(ClipboardFixture.Snapshot(2) with{Text=null,Dib=dib});Assert.Equal(128,result.Width);Assert.Equal(128,result.Height);
        using var bitmap=SKBitmap.Decode(result.Png);Assert.NotNull(bitmap);
    }

    [Fact]
    public async Task StopDrainsExistingItemsAndHasBoundedTimeout()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader();
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader,async(_,token)=>{await Task.Delay(10000,token);return true;});
        reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();var timer=Stopwatch.StartNew();
        Assert.False(await service.StopAsync(TimeSpan.FromMilliseconds(80)));Assert.True(timer.Elapsed<TimeSpan.FromSeconds(1));Assert.Contains("超时",service.State.Message);
    }

    [Fact]
    public async Task AReadAlreadyInProgressIsIncludedInNormalShutdown()
    {
        using var fixture=new ClipboardFixture();var reader=new FakeClipboardReader{ReadDelay=180};
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,reader);reader.Set(ClipboardFixture.Snapshot(1));
        var capture=service.NotifyChangedAsync();await ClipboardFixture.Until(()=>reader.ReadCount==1);
        Assert.True(await service.StopAsync());await capture;Assert.Single(fixture.Database.SearchClipboard());
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void DibAndDibV5PreserveDimensionsAndPixels(bool v5)
    {
        var png=ClipboardFixture.Png(SKColors.CornflowerBlue,256);var dib=ClipboardImageCodec.PngToDib(png);
        if(v5)
        {
            var larger=new byte[dib.Length+84];Array.Copy(dib,larger,40);Array.Copy(dib,40,larger,124,dib.Length-40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(larger,124);dib=larger;
        }
        var encoded=ClipboardImageCodec.Prepare(ClipboardFixture.Snapshot(1) with{Text=null,Dib=dib});
        using var original=SKBitmap.Decode(png);using var decoded=SKBitmap.Decode(encoded.Png);
        Assert.Equal(256,encoded.Width);Assert.Equal(256,encoded.Height);Assert.Equal(original.Bytes,decoded.Bytes);
    }

    [Fact]
    public void EncodedPngFastPathPreservesBytesAndMeasuresAgainstLegacyReencoding()
    {
        var png=ClipboardFixture.Png(SKColors.SlateBlue,2048);var source=ClipboardImageCodec.Decode(png);
        // Both operate on the same synthetic bytes; warm each path before comparing repeated medians.
        byte[] Legacy(){var clone=new WriteableBitmap(source);clone.Freeze();return ClipboardHistoryService.EncodePng(clone);}
        var snapshot=ClipboardFixture.Snapshot(1,png);Legacy();ClipboardImageCodec.Prepare(snapshot);
        var oldMs=new List<double>();var newMs=new List<double>();
        for(var i=0;i<5;i++)
        {
            var timer=Stopwatch.StartNew();Legacy();oldMs.Add(timer.Elapsed.TotalMilliseconds);timer.Restart();var result=ClipboardImageCodec.Prepare(snapshot);newMs.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Same(png,result.Png);
        }
        output.WriteLine($"2048px legacy copy+encode median={oldMs.Order().ElementAt(2):F1}ms; PNG validate+reuse median={newMs.Order().ElementAt(2):F1}ms");
        Assert.True(newMs.Order().ElementAt(2)<oldMs.Order().ElementAt(2),"PNG reuse should remove the repeated encoding work");
    }
}

internal sealed class ClipboardFixture:IDisposable
{
    internal string Root{get;}=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
    internal string File=>Path.Combine(Root,"gtools.db");
    internal LocalDatabase Database{get;}
    public ClipboardFixture(){Database=new LocalDatabase(File);Database.Initialize();}
    internal static ClipboardSnapshot Snapshot(uint sequence,byte[]? png=null)=>new(sequence,png is null?$"synthetic-{sequence}":null,null,null,png,null,DateTimeOffset.Now,Stopwatch.GetTimestamp());
    internal static byte[] Png(SKColor color,int size)
    {
        using var bitmap=new SKBitmap(size,size);using(var canvas=new SKCanvas(bitmap))
        {
            canvas.Clear(color);using var paint=new SKPaint{Color=SKColors.White,StrokeWidth=3};
            for(var y=20;y<size;y+=37)canvas.DrawLine(10,y,size-10,y+13,paint);
        }
        using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,100);return data.ToArray();
    }
    internal static async Task Until(Func<bool> ready,int milliseconds=5000)
    {
        var clock=Stopwatch.StartNew();while(!ready()&&clock.ElapsedMilliseconds<milliseconds)await Task.Delay(10);Assert.True(ready(),"Timed out waiting for clipboard pipeline");
    }
    public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
}

internal sealed class FakeClipboardReader:IClipboardReader
{
    private ClipboardSnapshot _snapshot=ClipboardFixture.Snapshot(1);private int _reads;
    internal int FailuresRemaining,ReadDelay;internal uint? AdvertisedSequence;
    internal ConcurrentBag<int> Threads{get;}=new();internal ConcurrentBag<ApartmentState> Apartments{get;}=new();
    internal int ReadCount=>Volatile.Read(ref _reads);
    internal void Set(ClipboardSnapshot snapshot){Volatile.Write(ref _snapshot,snapshot);AdvertisedSequence=null;}
    public uint SequenceNumber=>AdvertisedSequence??Volatile.Read(ref _snapshot).Sequence;
    public ClipboardSnapshot Read()
    {
        Interlocked.Increment(ref _reads);Threads.Add(Environment.CurrentManagedThreadId);Apartments.Add(Thread.CurrentThread.GetApartmentState());
        if(ReadDelay>0)Thread.Sleep(ReadDelay);
        if(Interlocked.Decrement(ref FailuresRemaining)>=0)throw new ExternalException("Synthetic clipboard busy");return Volatile.Read(ref _snapshot);
    }
}
