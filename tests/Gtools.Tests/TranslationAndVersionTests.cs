using System.Net;
using System.Text;
using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class TranslationProviderTests
{
    [Fact]
    public async Task GoogleProvider_ParsesSyntheticTranslationWithoutSendingRealText()
    {
        using var client=new HttpClient(new StubHandler(_=>new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content=new StringContent("[[[\"合成译文\",\"synthetic source\",null,null,1]],null,\"en\"]",Encoding.UTF8,"application/json")
        }));
        var provider=new GoogleWebTranslationProvider(client);

        var result=await provider.TranslateAsync(new TranslationRequest("synthetic source","en","zh-CN"));

        Assert.Equal("合成译文",result.Text);Assert.True(result.IsUnofficial);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden,"403")]
    [InlineData(HttpStatusCode.TooManyRequests,"429")]
    [InlineData(HttpStatusCode.InternalServerError,"500")]
    public async Task GoogleProvider_ExplainsHttpFailuresInChinese(HttpStatusCode status,string marker)
    {
        using var client=new HttpClient(new StubHandler(_=>new HttpResponseMessage(status)));
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>new GoogleWebTranslationProvider(client).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Contains(marker,error.Message);Assert.Contains("Google",error.Message);
    }

    [Fact]
    public async Task GoogleProvider_ExplainsTimeout()
    {
        using var client=new HttpClient(new StubHandler(_=>throw new TaskCanceledException("synthetic timeout")));
        var error=await Assert.ThrowsAsync<TimeoutException>(()=>new GoogleWebTranslationProvider(client).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Contains("超时",error.Message);Assert.Contains("网络",error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("[[[]]]")]
    public void GoogleProvider_RejectsChangedOrEmptyPayload(string payload)
    {
        var error=Assert.Throws<InvalidOperationException>(()=>GoogleWebTranslationProvider.ParsePayload(payload));
        Assert.Contains("返回格式已经变化",error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>Task.FromResult(response(request));
    }
}

public sealed class TranslationVisualStyleTests
{
    [Fact]
    public void TranslationBodyUsesReadableTypeAndSpacing()
    {
        Assert.Equal(17,TranslationVisualStyle.BodyFontSize);Assert.Equal(25,TranslationVisualStyle.BodyLineHeight);
        Assert.Equal("Segoe UI",TranslationVisualStyle.FontFor("en").Source);
        Assert.Equal("Microsoft YaHei UI",TranslationVisualStyle.FontFor("zh-CN").Source);
        Assert.Equal("English",TranslationVisualStyle.LanguageName("en"));Assert.Equal("中文",TranslationVisualStyle.LanguageName("zh-CN"));
    }
}

public sealed class ApplicationIdentityTests
{
    [Fact]
    public void FileVersionWinsAndDisplayVersionDropsZeroRevision()
    {
        var path=Path.Combine(Path.GetTempPath(),"Gtools.exe");
        var info=Gtools.App.ApplicationIdentity.Resolve(path,"1.1.7.0","9.9.9.9");
        Assert.Equal("1.1.7",info.DisplayVersion);Assert.Equal("1.1.7.0",info.FileVersion);Assert.Equal(Path.GetFullPath(path),info.ExecutablePath);Assert.Equal("当前版本 v1.1.7",info.DisplayLabel);
    }

    [Fact]
    public void AssemblyVersionIsUsedWhenFileVersionCannotBeRead()
    {
        var info=Gtools.App.ApplicationIdentity.Resolve(null,null,"1.1.7.0");
        Assert.Equal("1.1.7",info.DisplayVersion);Assert.Equal("1.1.7.0",info.FileVersion);Assert.Equal("无法确定当前程序位置",info.ExecutablePath);
    }
}
