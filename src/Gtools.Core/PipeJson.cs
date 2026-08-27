using System.Buffers.Binary;
using System.Text.Json;

namespace Gtools.Core;

public static class PipeJson
{
    public static async Task WriteAsync<T>(Stream stream,T value,CancellationToken token=default)
    {
        var payload=JsonSerializer.SerializeToUtf8Bytes(value,GtoolsJson.Options);var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
        await stream.WriteAsync(header,token);await stream.WriteAsync(payload,token);await stream.FlushAsync(token);
    }
    public static async Task<T?> ReadAsync<T>(Stream stream,CancellationToken token=default)
    {
        var header=new byte[4];await ReadExactlyAsync(stream,header,token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length<=0||length>256*1024*1024)throw new InvalidDataException($"无效的管道消息长度：{length}");
        var payload=new byte[length];await ReadExactlyAsync(stream,payload,token);return JsonSerializer.Deserialize<T>(payload,GtoolsJson.Options);
    }
    private static async Task ReadExactlyAsync(Stream stream,byte[] buffer,CancellationToken token)
    {
        var offset=0;while(offset<buffer.Length){var read=await stream.ReadAsync(buffer.AsMemory(offset),token);if(read==0)throw new EndOfStreamException("管道在消息完成前关闭。");offset+=read;}
    }
}
