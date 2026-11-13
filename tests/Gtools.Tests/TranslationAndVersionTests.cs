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

    private const string Website="translate.google.com",Apis="translate.googleapis.com";
    private static HttpResponseMessage Limited(TimeSpan? retryAfter=null)
    {
        var limited=new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if(retryAfter is {} wait)limited.Headers.RetryAfter=new RetryConditionHeaderValue(wait);
        return limited;
    }

    [Fact]
    public async Task RateLimitedHostFailsOverToTheOtherGoogleHostAndIsSkippedWhileCooling()
    {
        var clock=new FakeClock();var hosts=new List<string>();
        using var client=new HttpClient(new StubHandler(request=>
        {
            hosts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host==Website?Limited(TimeSpan.FromMinutes(2)):Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        var first=await provider.TranslateAsync(new("rate-limited synthetic one","en","zh-CN"));
        Assert.Equal("合成译文",first.Text);Assert.Equal(new[]{Website,Apis},hosts);
        // One usable host means translation is not blocked, so there is no cooldown to report to the user.
        Assert.Equal(TimeSpan.Zero,provider.CooldownRemaining());

        hosts.Clear();await provider.TranslateAsync(new("rate-limited synthetic two","en","zh-CN"));
        Assert.Equal(new[]{Apis},hosts);

        hosts.Clear();clock.Advance(TimeSpan.FromMinutes(3));
        await provider.TranslateAsync(new("rate-limited synthetic three","en","zh-CN"));
        Assert.Equal(Website,hosts[0]);
    }

    [Fact]
    public async Task BothHostsRateLimitedCreatesCooldownAndBlocksRepeatedTrafficUntilUserChangesNetwork()
    {
        var clock=new FakeClock();var calls=0;
        using var client=new HttpClient(new StubHandler(_=>++calls<=2?Limited(TimeSpan.FromMinutes(2)):Success()));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        var request=new TranslationRequest("rate-limited synthetic","en","zh-CN");

        var first=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(request));
        var blocked=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(request));
        Assert.Equal(TranslationFailureKind.TooManyRequests,first.Kind);Assert.Equal(TranslationFailureKind.TooManyRequests,blocked.Kind);
        Assert.Contains("429",first.Message);Assert.Contains("秒后可重试",first.Message);
        // Each host is asked once; while both cool down nothing more is sent.
        Assert.Equal(2,calls);Assert.Equal(TimeSpan.FromMinutes(2),provider.CooldownRemaining());

        var recovered=await provider.TranslateAfterNetworkChangeAsync(request);
        Assert.Equal("合成译文",recovered.Text);Assert.Equal(3,calls);
    }

    [Fact]
    public async Task ForbiddenAsksEachHostOnceAndServerFailureMovesToTheOtherHost()
    {
        var clock=new FakeClock();var forbiddenHosts=new List<string>();
        using(var forbiddenClient=new HttpClient(new StubHandler(request=>{forbiddenHosts.Add(request.RequestUri!.Host);return new(HttpStatusCode.Forbidden);})))
        using(var forbiddenProvider=new GoogleWebTranslationProvider(forbiddenClient,clock.UtcNow,clock.Delay))
        {
            var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>forbiddenProvider.TranslateAsync(new("forbidden","en","zh-CN")));
            Assert.Equal(TranslationFailureKind.Forbidden,error.Kind);Assert.Equal(new[]{Website,Apis},forbiddenHosts);
        }

        var serverHosts=new List<string>();
        using var serverClient=new HttpClient(new StubHandler(request=>{serverHosts.Add(request.RequestUri!.Host);return serverHosts.Count==1?new(HttpStatusCode.ServiceUnavailable):Success();}));
        using var serverProvider=new GoogleWebTranslationProvider(serverClient,clock.UtcNow,clock.Delay);
        var result=await serverProvider.TranslateAsync(new("server retry","en","zh-CN"));
        Assert.Equal("合成译文",result.Text);Assert.Equal(new[]{Website,Apis},serverHosts);
    }

    [Fact]
    public async Task TimeoutAsksEachHostOnlyOnce()
    {
        var clock=new FakeClock();var hosts=new List<string>();
        using var client=new HttpClient(new AsyncStubHandler((request,_)=>{hosts.Add(request.RequestUri!.Host);throw new TaskCanceledException("synthetic timeout");}));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("timeout","en","zh-CN")));

        Assert.Equal(TranslationFailureKind.Timeout,error.Kind);Assert.Equal(new[]{Website,Apis},hosts);
    }

    [Fact]
    public async Task ConnectionFailureMovesToTheOtherHost()
    {
        var clock=new FakeClock();var hosts=new List<string>();
        using var client=new HttpClient(new StubHandler(request=>
        {
            hosts.Add(request.RequestUri!.Host);
            return request.RequestUri.Host==Website?throw new HttpRequestException("synthetic connection reset"):Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);

        var result=await provider.TranslateAsync(new("network","en","zh-CN"));

        Assert.Equal("合成译文",result.Text);Assert.Equal(new[]{Website,Apis},hosts);
    }

    [Fact]
    public async Task OnlyUsableHostRetriesATransientFailureOnceButNeverARejection()
    {
        var clock=new FakeClock();var apiCalls=0;var failApi=false;var rejectApi=false;
        using var client=new HttpClient(new StubHandler(request=>
        {
            if(request.RequestUri!.Host==Website)return Limited(TimeSpan.FromMinutes(10));
            apiCalls++;
            if(rejectApi)return new(HttpStatusCode.Forbidden);
            if(failApi){failApi=false;return new(HttpStatusCode.BadGateway);}
            return Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        await provider.TranslateAsync(new("warm up so the website host is cooling","en","zh-CN"));

        apiCalls=0;failApi=true;
        var result=await provider.TranslateAsync(new("transient on the only usable host","en","zh-CN"));
        Assert.Equal("合成译文",result.Text);Assert.Equal(2,apiCalls);

        apiCalls=0;rejectApi=true;
        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("rejected on the only usable host","en","zh-CN")));
        Assert.Equal(TranslationFailureKind.Forbidden,error.Kind);Assert.Equal(1,apiCalls);
    }

    [Fact]
    public async Task ChangedPayloadIsReportedWithoutAskingTheOtherHost()
    {
        var calls=0;
        using var client=new HttpClient(new StubHandler(_=>{calls++;return Success("{}");}));
        using var provider=new GoogleWebTranslationProvider(client);

        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("format","en","zh-CN")));

        Assert.Equal(TranslationFailureKind.InvalidResponse,error.Kind);Assert.Equal(1,calls);
    }

    [Fact]
    public async Task LongTextIsSentAsAPostFormAndShortTextStaysInTheUrl()
    {
        var seen=new List<(HttpMethod Method,string Url,string? Body)>();
        using var client=new HttpClient(new AsyncStubHandler(async(request,token)=>
        {
            seen.Add((request.Method,request.RequestUri!.OriginalString,request.Content is null?null:await request.Content.ReadAsStringAsync(token)));
            return Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client);
        // 2,500 CJK characters would be a 22 KB URL; the service answers HTTP 400 from roughly 16 KB.
        var longText=string.Concat(Enumerable.Repeat("这是一段用于测试长度的合成文字。",160))[..2500];

        await provider.TranslateAsync(new("short synthetic","en","zh-CN"));
        await provider.TranslateAsync(new(longText,"zh-CN","en"));

        Assert.Equal(HttpMethod.Get,seen[0].Method);Assert.Contains("q=short%20synthetic",seen[0].Url);Assert.Null(seen[0].Body);
        Assert.Equal(HttpMethod.Post,seen[1].Method);Assert.DoesNotContain("q=",seen[1].Url);
        Assert.Contains("client=gtx",seen[1].Url);Assert.Contains("dj=1",seen[1].Url);Assert.Contains("sl=zh-cn",seen[1].Url);Assert.Contains("tl=en",seen[1].Url);
        Assert.True(seen[1].Url.Length<GoogleWebTranslationProvider.MaxGetUrlLength);
        Assert.StartsWith("q=",seen[1].Body);Assert.Equal(longText,Uri.UnescapeDataString(seen[1].Body![2..].Replace('+',' ')));
    }

    [Fact]
    public async Task ConnectionTestSendsOnlyFixedSyntheticTextToBothHostsAndCanProbeDuringCooldown()
    {
        var clock=new FakeClock();var urls=new List<string>();var calls=0;
        using var client=new HttpClient(new StubHandler(request=>
        {
            urls.Add(request.RequestUri!.OriginalString);calls++;
            return calls<=2?Limited():Success();
        }));
        using var provider=new GoogleWebTranslationProvider(client,clock.UtcNow,clock.Delay);
        await Assert.ThrowsAsync<TranslationProviderException>(()=>provider.TranslateAsync(new("private-placeholder","en","zh-CN")));
        Assert.True(provider.CooldownRemaining()>TimeSpan.Zero);

        var test=await provider.TestConnectionAsync();

        Assert.True(test.Success);Assert.Equal(4,calls);
        Assert.Equal(TimeSpan.Zero,provider.CooldownRemaining());
        Assert.Contains(Website,urls[2]);Assert.Contains(Apis,urls[3]);
        Assert.Contains(Website,test.Message);Assert.Contains(Apis,test.Message);Assert.DoesNotContain("暂不可用",test.Message);
        foreach(var url in urls.Skip(2)){Assert.Contains("q=Gtools%20connectivity%20test",url);Assert.DoesNotContain("private-placeholder",url);}
    }

    [Fact]
    public async Task ConnectionTestReportsAUsableChannelAndNamesTheHostThatIsDown()
    {
        var clock=new FakeClock();
        using var partialClient=new HttpClient(new StubHandler(request=>request.RequestUri!.Host==Website?Limited():Success()));
        using var partial=new GoogleWebTranslationProvider(partialClient,clock.UtcNow,clock.Delay);
        var usable=await partial.TestConnectionAsync();
        Assert.True(usable.Success);Assert.Contains("暂不可用",usable.Message);Assert.Contains($"{Website}（HTTP 429）",usable.Message);
        Assert.Null(usable.CooldownRemaining);

        using var downClient=new HttpClient(new StubHandler(_=>Limited(TimeSpan.FromSeconds(90))));
        using var down=new GoogleWebTranslationProvider(downClient,clock.UtcNow,clock.Delay);
        var failed=await down.TestConnectionAsync();
        Assert.False(failed.Success);Assert.Equal(TranslationFailureKind.TooManyRequests,failed.FailureKind);
        Assert.Equal(TimeSpan.FromSeconds(90),failed.CooldownRemaining);
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

    [Fact]
    public async Task YoudaoSendsTheTextInAPostFormAndParsesTheTranslation()
    {
        string? url=null,body=null;HttpMethod? method=null;
        using var client=new HttpClient(new AsyncStubHandler(async(request,token)=>
        {
            url=request.RequestUri!.OriginalString;method=request.Method;body=await request.Content!.ReadAsStringAsync(token);
            return Success("{\"errorCode\":\"0\",\"translation\":[\"合成\",\"译文\"]}");
        }));
        var provider=new YoudaoWebTranslationProvider(client);

        var result=await provider.TranslateAsync(new("synthetic source","en","zh-CN"));

        Assert.Equal("合成"+Environment.NewLine+"译文",result.Text);Assert.True(result.IsUnofficial);
        Assert.Equal(HttpMethod.Post,method);Assert.Equal("https://aidemo.youdao.com/trans",url);
        Assert.Contains("from=en",body);Assert.Contains("to=zh-CHS",body);Assert.Contains("q=synthetic+source",body);
    }

    [Fact]
    public async Task YoudaoExplainsFailuresInChineseInsteadOfRawExceptions()
    {
        async Task<TranslationProviderException> Fail(Func<HttpRequestMessage,HttpResponseMessage> respond)
        {
            using var client=new HttpClient(new StubHandler(respond));
            return await Assert.ThrowsAsync<TranslationProviderException>(()=>new YoudaoWebTranslationProvider(client).TranslateAsync(new("synthetic","en","zh-CN")));
        }
        var tooLong=await Fail(_=>Success("{\"errorCode\":\"103\"}"));
        Assert.Equal("youdao-web",tooLong.ProviderId);Assert.Contains("长度上限",tooLong.Message);Assert.Contains("103",tooLong.Message);
        var server=await Fail(_=>new(HttpStatusCode.BadGateway));
        Assert.Equal(TranslationFailureKind.Server,server.Kind);Assert.Contains("502",server.Message);
        var html=await Fail(_=>Success("<html>blocked</html>"));
        Assert.Equal(TranslationFailureKind.InvalidResponse,html.Kind);
        var empty=await Fail(_=>Success("{\"errorCode\":\"0\"}"));
        Assert.Equal(TranslationFailureKind.InvalidResponse,empty.Kind);
        var network=await Fail(_=>throw new HttpRequestException("synthetic"));
        Assert.Equal(TranslationFailureKind.Network,network.Kind);Assert.Contains("有道",network.Message);
        var timeout=await Fail(_=>throw new TaskCanceledException("synthetic"));
        Assert.Equal(TranslationFailureKind.Timeout,timeout.Kind);
        // A Youdao failure must not offer the Google-only recovery buttons.
        var actions=TranslationRecoveryPolicy.For(tooLong);Assert.False(actions.ShowGoogleNetworkRetry);Assert.False(actions.ShowManualYoudao);
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
