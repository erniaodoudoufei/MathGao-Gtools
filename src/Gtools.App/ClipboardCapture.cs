using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gtools.Core;

namespace Gtools.App;

internal sealed record ClipboardSnapshot(uint Sequence,string? Text,string? Html,string? Rtf,
    byte[]? Png,byte[]? Dib,DateTimeOffset CreatedAt,long CapturedAt,bool IsOwnRestore=false);

internal sealed record ClipboardSaveState(int PendingCount,int SavedCount,int FailureCount,string? LastError,bool IsReading,long LastSavedAt)
{
    public string Message => string.Join(" · ",new[]
    {
        IsReading?"正在读取剪贴板…":null,
        PendingCount>0?$"已捕获，正在保存 {PendingCount} 条":null,
        LastError,
        !IsReading&&PendingCount==0&&LastError is null?"已捕获内容均已保存":null
    }.Where(x=>x is not null));
}

internal interface IClipboardReader
{
    uint SequenceNumber { get; }
    ClipboardSnapshot? Read();
}

/// <summary>Copies owned bytes under one clipboard lock. No codec, SQLite or UI work here.</summary>
internal sealed class NativeClipboardReader(string originToken) : IClipboardReader
{
    private const uint CfUnicode=13,CfDib=8,CfDibV5=17;
    private const int MaxFormatBytes=256*1024*1024;
    private readonly uint _png=RegisterClipboardFormat("PNG"),_html=RegisterClipboardFormat("HTML Format"),
        _rtf=RegisterClipboardFormat("Rich Text Format"),_origin=RegisterClipboardFormat("Gtools.ClipboardRestore");
    internal IntPtr OwnerWindow { get; set; }
    public uint SequenceNumber=>GetClipboardSequenceNumber();
    public ClipboardSnapshot? Read()
    {
        if(!OpenClipboard(OwnerWindow))throw new ExternalException("Clipboard busy",Marshal.GetLastWin32Error());
        try
        {
            var own=Copy(_origin);
            if(own is not null&&Encoding.UTF8.GetString(own).TrimEnd('\0')==originToken)
                return new(SequenceNumber,null,null,null,null,null,DateTimeOffset.Now,Stopwatch.GetTimestamp(),true);
            var png=Copy(_png);
            var dib=png is null?Copy(CfDibV5)??Copy(CfDib):null;
            var hasImage=png is not null||dib is not null;
            var text=Text(CfUnicode,Encoding.Unicode,hasImage);var html=Text(_html,Encoding.UTF8,hasImage||text is not null);var rtf=Text(_rtf,Encoding.UTF8,hasImage||text is not null||html is not null);
            if(png is null&&dib is null&&text is null&&html is null&&rtf is null)return null;
            return new(SequenceNumber,text,html,rtf,png,dib,DateTimeOffset.Now,Stopwatch.GetTimestamp());
        }
        finally{CloseClipboard();}
    }
    private string? Text(uint format,Encoding encoding,bool optional)
    {
        // A broken optional rich-text format must not discard an otherwise valid image.
        try{var bytes=Copy(format);var text=bytes is null?null:encoding.GetString(bytes).TrimEnd('\0');return string.IsNullOrEmpty(text)?null:text;}
        catch(Exception ex) when(optional){DiagnosticLog.Write("clipboard","format.read.failed","可选文字格式读取失败","Warning",data:new Dictionary<string,object?>{{"format",format},{"errorType",ex.GetType().Name}});return null;}
    }
    private static byte[]? Copy(uint format)
    {
        if(!IsClipboardFormatAvailable(format))return null;
        var handle=GetClipboardData(format);if(handle==IntPtr.Zero)throw new ExternalException("Clipboard format not ready",Marshal.GetLastWin32Error());
        var size=GlobalSize(handle).ToUInt64();if(size==0)return null;
        if(size>MaxFormatBytes)throw new InvalidDataException("Clipboard format exceeds memory limit");
        var pointer=GlobalLock(handle);if(pointer==IntPtr.Zero)throw new ExternalException("Clipboard memory unavailable");
        try{var bytes=new byte[(int)size];Marshal.Copy(pointer,bytes,0,bytes.Length);return bytes;}
        finally{GlobalUnlock(handle);}
    }
    internal List<(uint Format,byte[] Bytes)> PrepareRestore(ClipboardEntry entry)
    {
        var values=new List<(uint Format,byte[] Bytes)>();
        if(!string.IsNullOrEmpty(entry.Text))values.Add((CfUnicode,Encoding.Unicode.GetBytes(entry.Text+'\0')));
        if(!string.IsNullOrEmpty(entry.Html))values.Add((_html,Encoding.UTF8.GetBytes(entry.Html+'\0')));
        if(!string.IsNullOrEmpty(entry.Rtf))values.Add((_rtf,Encoding.UTF8.GetBytes(entry.Rtf+'\0')));
        if(entry.ImagePng is {} png){values.Add((_png,png));values.Add((CfDib,ClipboardImageCodec.PngToDib(png)));}
        if(values.Count==0)throw new InvalidDataException("记录没有可恢复的内容。");
        values.Insert(0,(_origin,Encoding.UTF8.GetBytes(originToken+'\0')));
        return values;
    }
    internal void Restore(IReadOnlyList<(uint Format,byte[] Bytes)> values)
    {
        if(!OpenClipboard(OwnerWindow))throw new ExternalException("Clipboard busy",Marshal.GetLastWin32Error());
        try{if(!EmptyClipboard())throw new ExternalException("Cannot empty clipboard");foreach(var value in values)SetBytes(value.Format,value.Bytes);}
        finally{CloseClipboard();}
    }
    private static void SetBytes(uint format,byte[] bytes)
    {
        var memory=GlobalAlloc(2,(UIntPtr)bytes.Length);if(memory==IntPtr.Zero)throw new OutOfMemoryException();
        try
        {
            var pointer=GlobalLock(memory);if(pointer==IntPtr.Zero)throw new ExternalException("Cannot lock clipboard memory");
            try{Marshal.Copy(bytes,0,pointer,bytes.Length);}finally{GlobalUnlock(memory);}
            if(SetClipboardData(format,memory)==IntPtr.Zero)throw new ExternalException("Cannot set clipboard data");
            memory=IntPtr.Zero; // Ownership transferred to Windows.
        }
        finally{if(memory!=IntPtr.Zero)GlobalFree(memory);}
    }
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll")]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetClipboardData(uint format,IntPtr memory);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")]private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr memory);
}

internal static class ClipboardImageCodec
{
    internal static (byte[]? Png,int Width,int Height) Prepare(ClipboardSnapshot snapshot)
    {
        if(snapshot.Png is {} png)
        {
            if(png.Length<24||!png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("Invalid PNG");
            var image=Decode(png);
            // Force delayed decoder errors off the capture/UI threads, without encoding again.
            var stride=checked((image.PixelWidth*image.Format.BitsPerPixel+7)/8);var row=new byte[stride];
            for(var y=0;y<image.PixelHeight;y++)image.CopyPixels(new Int32Rect(0,y,image.PixelWidth,1),row,stride,0);
            return(png,image.PixelWidth,image.PixelHeight);
        }
        if(snapshot.Dib is {} dib){var image=Decode(DibToBmp(dib));return(ClipboardHistoryService.EncodePng(image),image.PixelWidth,image.PixelHeight);}
        return(null,0,0);
    }
    internal static BitmapSource Decode(byte[] encoded)
    {
        using var stream=new MemoryStream(encoded,false);
        var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad);
        var frame=decoder.Frames[0];frame.Freeze();return frame;
    }
    internal static byte[] DibToBmp(byte[] dib)
    {
        if(dib.Length<40)throw new InvalidDataException("DIB header is too short");
        var header=BinaryPrimitives.ReadUInt32LittleEndian(dib);var bits=BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression=BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));var colors=BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if(header is not (40 or 52 or 56 or 108 or 124)||header>dib.Length)throw new InvalidDataException("Unsupported DIB header");
        if(bits is not (1 or 4 or 8 or 16 or 24 or 32))throw new InvalidDataException("Unsupported DIB pixel format");
        if(colors==0&&bits<=8)colors=(uint)(1<<bits);
        var masks=header==40?(compression==3?12:compression==6?16:0):0;
        var offset=checked((int)header+masks+(int)colors*4);
        if(offset>=dib.Length)throw new InvalidDataException("DIB pixels are missing");
        var bmp=new byte[checked(dib.Length+14)];bmp[0]=(byte)'B';bmp[1]=(byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2),bmp.Length);BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10),offset+14);dib.CopyTo(bmp,14);return bmp;
    }
    internal static byte[] PngToDib(byte[] png)
    {
        var source=Decode(png);var bitmap=new FormatConvertedBitmap(source,PixelFormats.Bgr32,null,0);bitmap.Freeze();
        var stride=checked(bitmap.PixelWidth*4);var pixels=checked(stride*bitmap.PixelHeight);var dib=new byte[checked(40+pixels)];
        BinaryPrimitives.WriteInt32LittleEndian(dib,40);BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4),bitmap.PixelWidth);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8),-bitmap.PixelHeight);BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12),1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14),32);BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20),pixels);
        bitmap.CopyPixels(Int32Rect.Empty,dib,stride,40);return dib;
    }
}
