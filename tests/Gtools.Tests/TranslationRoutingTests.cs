using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class OpenAiTranslationProviderTests
{
    private const string FakeKey="synthetic-key-not-real";
    private static GtoolsSettings Settings(string baseUrl="https://api.deepseek.com",string model="deepseek-flash")=>
        new(){OpenAiBaseUrl=baseUrl,OpenAiModel=model,OpenAiKeyProtected="stored",OpenAiPrompt="Translate. Return only the translation."};
    private static HttpResponseMessage Reply(string content)=>Json(HttpStatusCode.OK,JsonSerializer.Serialize(new{choices=new[]{new{message=new{role="assistant",content}}}}));
    private static HttpResponseMessage Json(HttpStatusCode status,string body)=>new(status){Content=new StringContent(body,Encoding.UTF8,"application/json")};

    [Fact]
    public async Task SendsKeyModelAndPromptAndTurnsOffThinkingOnlyForDeepSeek()
    {
        var seen=new List<(string Url,string? Auth,JsonDocument Body)>();
        using var client=new HttpClient(new Handler(async(request,token)=>
        {
            seen.Add((request.RequestUri!.OriginalString,request.Headers.Authorization?.ToString(),JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Reply("  合成译文 \n");
        }));

        var deepseek=new OpenAiTranslationProvider(Settings(),client,_=>FakeKey);
        var result=await deepseek.TranslateAsync(new("synthetic source","en","zh-CN"));
        Assert.Equal("合成译文",result.Text);Assert.False(result.IsUnofficial);Assert.Equal("AI 接口 · deepseek-flash",result.Provider);
        var first=seen[0];Assert.Equal("https://api.deepseek.com/chat/completions",first.Url);Assert.Equal("Bearer "+FakeKey,first.Auth);
        var body=first.Body.RootElement;Assert.Equal("deepseek-flash",body.GetProperty("model").GetString());
        // DeepSeek reasons before answering unless told not to; a one-line translation should not wait for that.
        Assert.Equal("disabled",body.GetProperty("thinking").GetProperty("type").GetString());Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("Translate. Return only the translation.",body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("en -> zh-CN\n\nsynthetic source",body.GetProperty("messages")[1].GetProperty("content").GetString());

        var other=new OpenAiTranslationProvider(Settings("https://api.openai.com/v1/","gpt-test"),client,_=>FakeKey);
        await other.TranslateAsync(new("synthetic source","en","zh-CN","Custom prompt"));
        Assert.Equal("https://api.openai.com/v1/chat/completions",seen[1].Url);
        // The switch is DeepSeek-specific; other services may reject a parameter they do not know.
        Assert.False(seen[1].Body.RootElement.TryGetProperty("thinking",out _));
        Assert.Equal("Custom prompt",seen[1].Body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void ParsesContentDropsLeadingThinkBlockAndRejectsUnusableReplies()
    {
        Assert.Equal("译文",OpenAiTranslationProvider.ParseContent("{\"choices\":[{\"message\":{\"content\":\"<think>step one\\nstep two</think>\\n译文\"}}]}"));
        foreach(var bad in new[]{"{}","{\"choices\":[]}","{\"choices\":[{\"message\":{\"content\":null}}]}","{\"choices\":[{\"message\":{\"content\":\"   \"}}]}","<html>gateway</html>"})
        {
            var error=Assert.Throws<TranslationProviderException>(()=>OpenAiTranslationProvider.ParseContent(bad));
            Assert.Equal(TranslationFailureKind.InvalidResponse,error.Kind);Assert.Equal(OpenAiTranslationProvider.ProviderId,error.ProviderId);
        }
    }

    [Fact]
    public async Task ExplainsRejectionsInChineseWithTheServersOwnWordsAndNeverTheKey()
    {
        async Task<TranslationProviderException> Fail(HttpResponseMessage response)
        {
            using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(response)));
            return await Assert.ThrowsAsync<TranslationProviderException>(()=>new OpenAiTranslationProvider(Settings(),client,_=>FakeKey).TranslateAsync(new("synthetic","en","zh-CN")));
        }
        // DeepSeek answers an invalid key with plain text rather than JSON.
        var unauthorized=await Fail(new(HttpStatusCode.Unauthorized){Content=new StringContent("Authentication Fails (governor)")});
        Assert.Equal(TranslationFailureKind.Forbidden,unauthorized.Kind);Assert.Contains("401",unauthorized.Message);Assert.Contains("API Key",unauthorized.Message);
        Assert.Contains("Authentication Fails (governor)",unauthorized.Message);Assert.DoesNotContain(FakeKey,unauthorized.Message);
        var balance=await Fail(Json(HttpStatusCode.PaymentRequired,"{\"error\":{\"message\":\"Insufficient Balance\",\"type\":\"unknown_error\"}}"));
        Assert.Contains("余额不足",balance.Message);Assert.Contains("Insufficient Balance",balance.Message);
        var missing=await Fail(Json(HttpStatusCode.NotFound,"{\"error\":\"model not found\"}"));
        Assert.Contains("模型",missing.Message);Assert.Contains("model not found",missing.Message);
        var limited=await Fail(Json(HttpStatusCode.TooManyRequests,"{}"));
        Assert.Equal(TranslationFailureKind.TooManyRequests,limited.Kind);Assert.DoesNotContain("接口原话",limited.Message);
        var server=await Fail(new(HttpStatusCode.BadGateway){Content=new StringContent("<html><body>502</body></html>")});
        Assert.Equal(TranslationFailureKind.Server,server.Kind);Assert.DoesNotContain("<html>",server.Message);
        var bad=await Fail(Json(HttpStatusCode.BadRequest,"{\"error\":{\"message\":\"Model Not Exist\"}}"));
        Assert.Equal(TranslationFailureKind.Other,bad.Kind);Assert.Contains("Model Not Exist",bad.Message);
    }

    [Fact]
    public async Task IncompleteSettingsAreReportedWithoutSendingAnythingAndNetworkFailuresAreClassified()
    {
        var calls=0;
        using var client=new HttpClient(new Handler((_,_)=>{calls++;return Task.FromResult(Reply("x"));}));
        Task<TranslationProviderException> Fail(GtoolsSettings settings,string key=FakeKey)=>
            Assert.ThrowsAsync<TranslationProviderException>(()=>new OpenAiTranslationProvider(settings,client,_=>key).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Contains("API Key",(await Fail(Settings(),"")).Message);
        Assert.Contains("模型",(await Fail(Settings(model:"  "))).Message);
        Assert.Contains("地址",(await Fail(Settings("not a url"))).Message);
        Assert.Contains("地址",(await Fail(Settings("ftp://example.invalid"))).Message);
        Assert.Equal(0,calls);

        using var timeoutClient=new HttpClient(new Handler((_,_)=>throw new TaskCanceledException("synthetic")));
        var timeout=await Assert.ThrowsAsync<TranslationProviderException>(()=>new OpenAiTranslationProvider(Settings(),timeoutClient,_=>FakeKey).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Equal(TranslationFailureKind.Timeout,timeout.Kind);
        using var downClient=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("synthetic")));
        var down=await Assert.ThrowsAsync<TranslationProviderException>(()=>new OpenAiTranslationProvider(Settings(),downClient,_=>FakeKey).TranslateAsync(new("synthetic","en","zh-CN")));
        Assert.Equal(TranslationFailureKind.Network,down.Kind);
    }

    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>respond(request,cancellationToken);
    }
}

public sealed class TranslationRouterTests
{
    private const string Google="google-web",Youdao="youdao-web",Ai=OpenAiTranslationProvider.ProviderId;

    private sealed class FakeProvider(string id,Func<TranslationRequest,TranslationResult> respond) : ITranslationProvider
    {
        public int Calls;
        public string Id=>id;
        public string DisplayName=>id;
        public Task<TranslationResult> TranslateAsync(TranslationRequest request,CancellationToken cancellationToken=default)
        {
            Calls++;
            try{return Task.FromResult(respond(request));}
            catch(Exception ex){return Task.FromException<TranslationResult>(ex);}
        }
    }

    private sealed class Rig
    {
        public GtoolsSettings Settings{get;}=new(){TranslationProvider=Google,OpenAiKeyProtected="stored"};
        public Dictionary<string,FakeProvider> Providers{get;}=new();
        public int Rechecks;
        public TranslationRouter Router{get;}
        public Rig(Func<string,TranslationRequest,TranslationResult> respond)
        {
            foreach(var id in new[]{Google,Youdao,Ai})Providers[id]=new(id,request=>respond(id,request));
            Router=new(Settings,id=>Providers[id],(request,_)=>{Rechecks++;return Task.FromResult(new TranslationResult("recheck",request.Text,TimeSpan.Zero));});
        }
        public int Calls(string id)=>Providers[id].Calls;
    }
    private static TranslationResult Ok(string id)=>new(id,"译文 by "+id,TimeSpan.FromMilliseconds(5));
    private static TranslationProviderException Limited(string id)=>new(id,TranslationFailureKind.TooManyRequests,id+" limited",HttpStatusCode.TooManyRequests);

    [Fact]
    public async Task GoogleFailureUsesTheAiInterfaceOnlyWhenTheUserHasSavedAKey()
    {
        var rig=new Rig((id,_)=>id==Google?throw Limited(id):Ok(id));
        var result=await rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default);
        Assert.Equal(Ai,result.Provider);Assert.Contains("Google",result.Note);Assert.Contains("被限流",result.Note);Assert.Contains("备用通道",result.Note);
        Assert.Equal(1,rig.Calls(Google));Assert.Equal(1,rig.Calls(Ai));Assert.Equal(0,rig.Calls(Youdao));

        rig.Settings.OpenAiKeyProtected="";
        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>rig.Router.TranslateAsync("synthetic","en","zh-CN",""));
        Assert.Equal(Google,error.ProviderId);Assert.Equal(1,rig.Calls(Ai));Assert.Equal(0,rig.Calls(Youdao));
    }

    [Fact]
    public async Task AiAndYoudaoFailuresFallBackToGoogleAndTextIsNeverSentToYoudaoAutomatically()
    {
        var rig=new Rig((id,_)=>id==Google?Ok(id):throw Limited(id));
        rig.Settings.TranslationProvider=Ai;
        var fromAi=await rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default);
        Assert.Equal(Google,fromAi.Provider);Assert.Contains("AI 接口",fromAi.Note);
        rig.Settings.TranslationProvider=Youdao;
        var fromYoudao=await rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default);
        Assert.Equal(Google,fromYoudao.Provider);Assert.Contains("有道",fromYoudao.Note);
        Assert.Equal(1,rig.Calls(Youdao));

        // Every configuration's automatic backup is Google or the user's own keyed interface.
        foreach(var primary in new[]{Google,Ai,Youdao,"unknown"})Assert.NotEqual(Youdao,rig.Router.FallbackFor(primary));
    }

    [Fact]
    public async Task SuccessHasNoNoteAndExplicitChoiceOrDisabledSettingNeverFallBack()
    {
        var healthy=new Rig((id,_)=>Ok(id));
        Assert.Null((await healthy.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default)).Note);Assert.Equal(0,healthy.Calls(Ai));

        var rig=new Rig((id,_)=>id==Ai?Ok(id):throw Limited(id));
        await Assert.ThrowsAsync<TranslationProviderException>(()=>rig.Router.TranslateAsync("synthetic","en","zh-CN",Youdao));
        Assert.Equal(1,rig.Calls(Youdao));Assert.Equal(0,rig.Calls(Google));Assert.Equal(0,rig.Calls(Ai));
        rig.Settings.TranslationAutoFallback=false;
        await Assert.ThrowsAsync<TranslationProviderException>(()=>rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default));
        Assert.Equal(1,rig.Calls(Google));Assert.Equal(0,rig.Calls(Ai));

        var recheck=await rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.GoogleRecheck);
        Assert.Equal("recheck",recheck.Provider);Assert.Equal(1,rig.Rechecks);Assert.Equal(1,rig.Calls(Google));
    }

    [Fact]
    public async Task WhenBothFailThePrimaryErrorKeepsItsTypeAndCarriesTheBackupReason()
    {
        var rig=new Rig((id,_)=>id==Google?throw Limited(id):throw new TranslationProviderException(Ai,TranslationFailureKind.Forbidden,"key rejected",HttpStatusCode.Unauthorized));
        var error=await Assert.ThrowsAsync<TranslationProviderException>(()=>rig.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default));
        Assert.Equal(Google,error.ProviderId);Assert.Equal(TranslationFailureKind.TooManyRequests,error.Kind);Assert.Equal(HttpStatusCode.TooManyRequests,error.StatusCode);
        Assert.Contains("google-web limited",error.Message);Assert.Contains("备用通道（AI 接口）也失败",error.Message);Assert.Contains("key rejected",error.Message);
        // The window still offers the Google recovery actions for the combined failure.
        var actions=TranslationRecoveryPolicy.For(error);Assert.True(actions.ShowGoogleNetworkRetry);Assert.True(actions.ShowManualYoudao);
    }

    [Fact]
    public async Task CancellationAndEmptyTextDoNotTriggerTheBackup()
    {
        var cancelled=new Rig((id,_)=>id==Google?throw new OperationCanceledException():Ok(id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled.Router.TranslateAsync("synthetic","en","zh-CN",TranslationRouter.Default));
        Assert.Equal(0,cancelled.Calls(Ai));
        var empty=new Rig((id,_)=>id==Google?throw new ArgumentException("empty"):Ok(id));
        await Assert.ThrowsAsync<ArgumentException>(()=>empty.Router.TranslateAsync(" ","en","zh-CN",TranslationRouter.Default));
        Assert.Equal(0,empty.Calls(Ai));
    }

    [Fact]
    public void RecoveryButtonsOfferTheAiInterfaceOnlyWithAKeyAndNotForItsOwnFailure()
    {
        var google=Limited(Google);var ai=new TranslationProviderException(Ai,TranslationFailureKind.Server,"synthetic");
        Assert.True(TranslationRecoveryPolicy.OfferAiInterface(google,true));Assert.False(TranslationRecoveryPolicy.OfferAiInterface(google,false));
        Assert.False(TranslationRecoveryPolicy.OfferAiInterface(ai,true));
        var actions=TranslationRecoveryPolicy.For(ai);Assert.False(actions.ShowGoogleNetworkRetry);Assert.True(actions.ShowManualYoudao);
    }
}

// Hosted Windows runners initialise WPF slowly when STA fixtures run side by side; share the serial collection the todo fixtures use.
[Collection(TodoUiTestCollection.Name)]
public sealed class TranslationWindowTests
{
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Private)!.GetValue(target)!;
    private static Task Call(object target,string name,params object[] args)=>(Task)target.GetType().GetMethod(name,Private)!.Invoke(target,args)!;
    private static TranslationResult Result(string text)=>new("fake",text,TimeSpan.Zero);

    private sealed class Calls
    {
        public List<(string Text,string Source,string Target,string Mode,TaskCompletionSource<TranslationResult> Completion)> Items{get;}=new();
        public Task<TranslationResult> Run(string text,string source,string target,string mode)
        {
            var completion=new TaskCompletionSource<TranslationResult>();Items.Add((text,source,target,mode,completion));return completion.Task;
        }
    }

    private static TranslationWindow Window(Calls calls,string source="He tried to get him to leave him alone",string? raw=null,bool hasKey=false)=>
        new(source,"en","zh-CN",calls.Run,(_,_,_)=>Task.FromResult<IReadOnlyList<ProviderOutcome>>(Array.Empty<ProviderOutcome>()),(_,_,_)=>{},raw,()=>hasKey);

    [Fact]
    public Task AnOlderRequestFinishingLateDoesNotOverwriteTheNewerResult()=>OnSta(async()=>
    {
        var calls=new Calls();var window=Window(calls);var target=Field<TextBox>(window,"_target");
        var first=Call(window,"RetryAsync",TranslationRouter.Default);var second=Call(window,"RetryAsync",TranslationRouter.Default);
        calls.Items[1].Completion.SetResult(Result("newer"));await second;Assert.Equal("newer",target.Text);
        calls.Items[0].Completion.SetResult(Result("older"));await first;Assert.Equal("newer",target.Text);

        // The same holds for a stale failure: it must not replace a good result with an error banner.
        var meta=Field<TextBlock>(window,"_meta").Text;
        var third=Call(window,"RetryAsync",TranslationRouter.Default);var fourth=Call(window,"RetryAsync",TranslationRouter.Default);
        calls.Items[3].Completion.SetResult(Result("newest"));await fourth;
        calls.Items[2].Completion.SetException(new InvalidOperationException("stale failure"));await third;
        Assert.Equal("newest",target.Text);Assert.DoesNotContain("stale failure",Field<TextBlock>(window,"_meta").Text);Assert.NotEqual("",meta);
    });

    [Fact]
    public Task SwappingLanguagesBeforeATranslationExistsKeepsTheSourceText()=>OnSta(async()=>
    {
        var calls=new Calls();var window=Window(calls);var source=Field<TextBox>(window,"_source");var target=Field<TextBox>(window,"_target");
        var swap=Call(window,"SwapLanguagesAsync");
        Assert.Equal("He tried to get him to leave him alone",source.Text);Assert.Equal("",target.Text);
        Assert.Equal(("zh-CN","en"),(calls.Items[0].Source,calls.Items[0].Target));
        calls.Items[0].Completion.SetResult(Result("译文"));await swap;

        // Once a translation exists the two panes trade places as before.
        var again=Call(window,"SwapLanguagesAsync");
        Assert.Equal("译文",source.Text);Assert.Equal("He tried to get him to leave him alone",target.Text);
        Assert.Equal(("译文","en","zh-CN"),(calls.Items[1].Text,calls.Items[1].Source,calls.Items[1].Target));
        calls.Items[1].Completion.SetResult(Result("back"));await again;
    });

    [Fact]
    public Task LayoutButtonSwitchesBetweenMergedParagraphsAndOcrLinesAndTranslatesAgain()=>OnSta(async()=>
    {
        var raw="He tried to get him to leave"+Environment.NewLine+"him alone";
        var calls=new Calls();var window=Window(calls,raw:raw);var source=Field<TextBox>(window,"_source");var toggle=Field<Button>(window,"_layoutToggle");
        Assert.Equal(Visibility.Visible,toggle.Visibility);Assert.Equal(TranslationWindow.RawLinesLabel,toggle.Content);
        var toRaw=Call(window,"ToggleLayoutAsync");
        Assert.Equal(raw,source.Text);Assert.Equal(TranslationWindow.ParagraphLabel,toggle.Content);Assert.Equal(raw,calls.Items[0].Text);
        calls.Items[0].Completion.SetResult(Result("逐行"));await toRaw;
        var back=Call(window,"ToggleLayoutAsync");
        Assert.Equal("He tried to get him to leave him alone",source.Text);Assert.Equal(TranslationWindow.RawLinesLabel,toggle.Content);
        calls.Items[1].Completion.SetResult(Result("整句"));await back;Assert.Equal("整句",Field<TextBox>(window,"_target").Text);

        // Nothing to switch when OCR produced a single line, or only the line endings differ.
        Assert.Equal(Visibility.Collapsed,Field<Button>(Window(new Calls()),"_layoutToggle").Visibility);
        Assert.False(TranslationWindow.HasDistinctRawLines("a\nb","a\r\nb"));Assert.True(TranslationWindow.HasDistinctRawLines("a b","a\r\nb"));
    });

    [Fact]
    public Task FailureShowsRecoveryButtonsAndEmptySourceIsReportedWithoutCallingAProvider()=>OnSta(async()=>
    {
        var limited=new TranslationProviderException("google-web",TranslationFailureKind.TooManyRequests,"synthetic 429",HttpStatusCode.TooManyRequests);
        foreach(var hasKey in new[]{true,false})
        {
            var calls=new Calls();var window=Window(calls,hasKey:hasKey);
            var attempt=Call(window,"RetryAsync",TranslationRouter.Default);calls.Items[0].Completion.SetException(limited);await attempt;
            Assert.Contains("synthetic 429",Field<TextBlock>(window,"_meta").Text);
            Assert.Equal(Visibility.Visible,Field<Button>(window,"_googleRetry").Visibility);Assert.Equal(Visibility.Visible,Field<Button>(window,"_youdaoRetry").Visibility);
            Assert.Equal(hasKey?Visibility.Visible:Visibility.Collapsed,Field<Button>(window,"_aiRetry").Visibility);
            // The note from an automatic backup is shown next to the provider that answered.
            var retry=Call(window,"RetryAsync",TranslationRouter.Default);
            Assert.Equal(Visibility.Collapsed,Field<Button>(window,"_aiRetry").Visibility);
            calls.Items[1].Completion.SetResult(Result("译文") with{Note="Google 被限流，已自动改用备用通道"});await retry;
            Assert.Contains("已自动改用备用通道",Field<TextBlock>(window,"_meta").Text);
        }
        var none=new Calls();var blank=Window(none,source:"   ");
        await Call(blank,"RetryAsync",TranslationRouter.Default);
        Assert.Empty(none.Items);Assert.Contains("没有可翻译",Field<TextBlock>(blank,"_meta").Text);
    });

    [Fact]
    public Task SettingsFillTheDeepSeekPresetAndTestWithTypedValuesBeforeSaving()=>OnSta(()=>
    {
        var folder=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
        try
        {
            var settings=new GtoolsSettings{OpenAiKeyProtected=SecretProtector.Protect("stored-synthetic-key")};
            var window=new SettingsWindow(settings,new SettingsStore(Path.Combine(folder,"settings.json")),
                _=>Task.FromResult(new GoogleChannelTestResult(true,"ok",HttpStatusCode.OK,null,TimeSpan.Zero,null)),(_,_)=>Task.FromResult("ok"));
            var preset=Descendants<Button>((DependencyObject)window.Content).Single(b=>b.Content is string text&&text.Contains("DeepSeek"));
            preset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var candidate=window.AiCandidate();
            Assert.Equal("https://api.deepseek.com",candidate.OpenAiBaseUrl);Assert.Equal("deepseek-flash",candidate.OpenAiModel);
            // Without retyping, the saved key is used; nothing is written to the real settings until Save.
            Assert.Equal("stored-synthetic-key",SecretProtector.Unprotect(candidate.OpenAiKeyProtected));
            Assert.Equal("https://api.openai.com/v1",settings.OpenAiBaseUrl);
            Field<TextBox>(window,"_key").Text="  typed-synthetic-key ";
            Assert.Equal("typed-synthetic-key",SecretProtector.Unprotect(window.AiCandidate().OpenAiKeyProtected));
            Assert.Contains(Descendants<CheckBox>((DependencyObject)window.Content),c=>c.Content is string text&&text.Contains("自动改用备用通道")&&c.IsChecked==true);
        }
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
        return Task.CompletedTask;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if(child is T match)yield return match;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }

    // Windows are constructed but never shown: the tests drive the same private handlers the buttons call.
    private static async Task OnSta(Func<Task> test)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async()=>
            {
                try{await test();done.TrySetResult();}
                catch(Exception ex){done.TrySetException(ex);}
                finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}
            }));
            Dispatcher.Run();
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        // Bounded, but without assuming a hosted runner is as fast as a local machine.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));Assert.True(thread.Join(5000));
    }
}
