using System.Net;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task GoogleProvider_ParsesStructuredSentencePayload()
    {
        using var client=new HttpClient(new StubHandler(_=>Success("{\"sentences\":[{\"trans\":\"合成\"},{\"trans\":\"译文\"}],\"src\":\"en\"}")));
        using var provider=new GoogleWebTranslationProvider(client);

        var result=await provider.TranslateAsync(new("synthetic","en","zh-CN"));

        Assert.Equal("合成译文",result.Text);
    }

    [Fact]
    public async Task GoogleProvider_UsesWebsiteEndpointStructuredParametersAndBrowserHeaders()
    {
        string? url=null,userAgent=null,acceptLanguage=null,accept=null;
        using var client=new HttpClient(new StubHandler(request=>
        {
            url=request.RequestUri?.OriginalString;userAgent=request.Headers.UserAgent.ToString();
            acceptLanguage=string.Join(',',request.Headers.AcceptLanguage);accept=string.Join(',',request.Headers.Accept);
            return Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client);

        await provider.TranslateAsync(new("synthetic source","EN","zh-CN"));

        Assert.StartsWith("https://translate.google.com/translate_a/single?",url);
        Assert.Contains("client=gtx",url);Assert.Contains("dj=1",url);Assert.Contains("ie=UTF-8",url);Assert.Contains("oe=UTF-8",url);
        Assert.Contains("sl=en",url);Assert.Contains("tl=zh-cn",url);Assert.Contains("q=synthetic%20source",url);
        Assert.Contains("Mozilla/5.0",userAgent);Assert.Contains("zh-CN",acceptLanguage);Assert.Contains("application/json",accept);
    }

    [Fact]
    public void DefaultGoogleHandlerFollowsSystemProxyDiscovery()
    {
        using var handler=GoogleWebTranslationProvider.CreateDefaultHandler();

        Assert.True(handler.UseProxy);Assert.Null(handler.Proxy);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsShareOneNetworkOperation()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls=0;using var client=new HttpClient(new AsyncStubHandler((_,_)=>{Interlocked.Increment(ref calls);entered.TrySetResult();return release.Task;}));
        using var provider=new GoogleWebTranslationProvider(client);

        var first=provider.TranslateAsync(new("same synthetic text","en","zh-CN"));await entered.Task;
        var second=provider.TranslateAsync(new("same synthetic text","en","zh-CN"));
        Assert.Equal(1,calls);Assert.Equal(1,provider.InflightRequestCount);
        release.SetResult(Success());await Task.WhenAll(first,second);

        Assert.Equal(1,calls);Assert.Equal(0,provider.InflightRequestCount);
    }

    [Fact]
    public async Task SuccessfulResultIsCachedInMemoryForFifteenMinutes()
    {
        var clock=new FakeClock();var calls=0;
        using var client=new HttpClient(new StubHandler(_=>{calls++;return Success();}));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        await provider.TranslateAsync(new("cached synthetic text","en","zh-CN"));
        await provider.TranslateAsync(new("cached synthetic text","en","zh-CN"));
        Assert.Equal(1,calls);Assert.Equal(1,provider.CachedEntryCount);

        clock.Advance(TimeSpan.FromMinutes(16));
        await provider.TranslateAsync(new("cached synthetic text","en","zh-CN"));
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task CacheKeepsAtMostSixtyFourEntriesAndDisposeClearsIt()
    {
        var clock=new FakeClock();var calls=0;
        using var client=new HttpClient(new StubHandler(_=>{calls++;return Success();}));
        var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        for(var i=0;i<65;i++)await provider.TranslateAsync(new($"synthetic-{i}","en","zh-CN"));

        Assert.Equal(64,provider.CachedEntryCount);
        await provider.TranslateAsync(new("synthetic-0","en","zh-CN"));
        Assert.Equal(66,calls);
        provider.Dispose();Assert.Equal(0,provider.CachedEntryCount);
    }

    [Fact]
    public async Task DistinctRequestsAreSpacedAtLeastNineHundredMilliseconds()
    {
        var clock=new FakeClock();var sentAt=new List<DateTimeOffset>();
        using var client=new HttpClient(new StubHandler(_=>{sentAt.Add(clock.UtcNow());return Success();}));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        await provider.TranslateAsync(new("first","en","zh-CN"));
        await provider.TranslateAsync(new("second","en","zh-CN"));

        Assert.Equal(2,sentAt.Count);Assert.True(sentAt[1]-sentAt[0]>=TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task RateLimitCreatesCooldownAndBlocksRepeatedTrafficUntilUserChangesNetwork()
    {
        var clock=new FakeClock();var calls=0;
        using var client=new HttpClient(new StubHandler(_=>
        {
            calls++;if(calls==1){var limited=new HttpResponseMessage(HttpStatusCode.TooManyRequests);limited.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));return limited;}
            return Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        var request=new TranslationRequest("rate-limited synthetic","en","zh-CN");

        var first=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(request));
        var blocked=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(request));
        Assert.Equal(TranslationFailureKind.TooManyRequests,first.Kind);Assert.Equal(TranslationFailureKind.TooManyRequests,blocked.Kind);
        Assert.Equal(1,calls);Assert.Equal(TimeSpan.FromMinutes(2),provider.CooldownRemaining());

        var recovered=await provider.TranslateAfterNetworkChangeAsync(request);
        Assert.Equal("合成译文",recovered.Text);Assert.Equal(2,calls);
    }

    [Fact]
    public async Task ForbiddenIsNotRetriedButServerFailureIsRetriedOnce()
    {
        var clock=new FakeClock();var forbiddenCalls=0;
        using(var forbiddenClient=new HttpClient(new StubHandler(_=>{forbiddenCalls++;return new(HttpStatusCode.Forbidden);})))
        using(var forbiddenProvider=new GoogleWebTranslationProvider(forbiddenClient,clock.UtcNow,clock.Delay))
        {
            var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>forbiddenProvider.TranslateAsync(new("forbidden","en","zh-CN")));
            Assert.Equal(TranslationFailureKind.Forbidden,error.Kind);Assert.Equal(1,forbiddenCalls);
        }

        var serverCalls=0;
        using var serverClient=new HttpClient(new StubHandler(_=>++serverCalls==1?new(HttpStatusCode.ServiceUnavailable):Success()));
        using var serverProvider=new GoogleWebTranslationProvider(serverClient,clock.UtcNow,clock.Delay);
        var result=await serverProvider.TranslateAsync(new("server retry","en","zh-CN"));
        Assert.Equal("合成译文",result.Text);Assert.Equal(2,serverCalls);
    }

    [Fact]
    public async Task TimeoutIsRetriedOnlyOnce()
    {
        var clock=new FakeClock();var calls=0;
        using var client=new HttpClient(new AsyncStubHandler((_,_)=>{calls++;throw new TaskCanceledException("synthetic timeout");}));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("timeout","en","zh-CN")));

        Assert.Equal(TranslationFailureKind.Timeout,error.Kind);Assert.Equal(2,calls);
    }

    [Fact]
    public async Task ConnectionTestSendsOnlyFixedSyntheticTextAndCanProbeDuringCooldown()
    {
        var clock=new FakeClock();var urls=new List<string>();var calls=0;
        using var client=new HttpClient(new StubHandler(request=>
        {
            urls.Add(request.RequestUri!.OriginalString);calls++;
            return calls==1?new(HttpStatusCode.TooManyRequests):Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("private-placeholder","en","zh-CN")));

        var test=await provider.TestConnectionAsync();

        Assert.True(test.Success);Assert.Equal(2,calls);
        Assert.Equal(TimeSpan.Zero,provider.CooldownRemaining());
        Assert.Contains("q=Gtools%20connectivity%20test",urls[1]);Assert.DoesNotContain("private-placeholder",urls[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden,"403")]
    [InlineData(HttpStatusCode.TooManyRequests,"429")]
    [InlineData(HttpStatusCode.InternalServerError,"500")]
    public async Task GoogleProvider_ExplainsHttpFailuresInChinese(HttpStatusCode status,string marker)
    {
        using var client=new HttpClient(new StubHandler(_=>new HttpResponseMessage(status)));
        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>new GoogleWebTranslationProvider(client).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Contains(marker,error.Message);Assert.Contains("Google",error.Message);
    }

    [Fact]
    public async Task GoogleProvider_ExplainsTimeout()
    {
        using var client=new HttpClient(new StubHandler(_=>throw new TaskCanceledException("synthetic timeout")));
        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>new GoogleWebTranslationProvider(client).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Contains("超时",error.Message);Assert.Contains("网络",error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("[[[]]]")]
    public void GoogleProvider_RejectsChangedOrEmptyPayload(string payload)
    {
        var error=Assert.Throws<TranslationProviderException>(()=>GoogleWebTranslationProvider.ParsePayload(payload));
        Assert.Contains("返回格式已经变化",error.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>Task.FromResult(response(request));
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>response(request,cancellationToken);
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now=new(2026,8,30,0,0,0,TimeSpan.Zero);
        internal DateTimeOffset UtcNow()=>_now;
        internal Task Delay(TimeSpan delay,CancellationToken token){token.ThrowIfCancellationRequested();_now+=delay;return Task.CompletedTask;}
        internal void Advance(TimeSpan value)=>_now+=value;
    }

    private static HttpResponseMessage Success(string payload="{\"sentences\":[{\"trans\":\"合成译文\"}],\"src\":\"en\"}")=>new(HttpStatusCode.OK)
    { Content=new StringContent(payload,Encoding.UTF8,"application/json") };
}

public sealed class TranslationRecoveryPolicyTests
{
    [Fact]
    public void GoogleFailureOffersExplicitNetworkRetryAndManualYoudaoOnly()
    {
        var error=new TranslationProviderException("google-web",TranslationFailureKind.TooManyRequests,"synthetic",HttpStatusCode.TooManyRequests);
        var actions=TranslationRecoveryPolicy.For(error);
        Assert.True(actions.ShowGoogleNetworkRetry);Assert.True(actions.ShowManualYoudao);
    }

    [Fact]
    public void NonGoogleFailureDoesNotOfferGoogleRecoveryActions()
    {
        var actions=TranslationRecoveryPolicy.For(new InvalidOperationException("synthetic"));
        Assert.False(actions.ShowGoogleNetworkRetry);Assert.False(actions.ShowManualYoudao);
    }

    [Fact]
    public void GoogleResponseFormatChangeOffersManualProviderWithoutMisleadingNetworkRetry()
    {
        var error=new TranslationProviderException("google-web",TranslationFailureKind.InvalidResponse,"synthetic");
        var actions=TranslationRecoveryPolicy.For(error);
        Assert.False(actions.ShowGoogleNetworkRetry);Assert.True(actions.ShowManualYoudao);
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
