using Gtools.Core;

namespace Gtools.Tests;

[Collection("Process-wide diagnostics")]
public sealed class CoreTests
{
    [Fact]
    public void RuntimeOptions_ParseIsolatedDiagnosticProfile()
    {
        var options=AppRuntimeOptions.Parse(["--test-profile",@"D:\\tmp\\gtools-test","--diagnostics","--self-test","--report","report.json","--capture-profile","QQ9"]);
        Assert.True(options.IsTestProfile);Assert.True(options.Diagnostics);Assert.True(options.SelfTest);Assert.Equal(@"D:\\tmp\\gtools-test",options.DataRoot);Assert.Equal("report.json",options.ReportPath);Assert.Equal(LongShotCaptureProfile.QQ9,options.CaptureProfile);
    }

    [Fact]
    public void AcceptanceReport_RoundTripsStructuredChecks()
    {
        var now=DateTimeOffset.Now;var report=new AcceptanceReport("1.1.0","test",now,now,true,[new("build","Release 构建",true,"0 warnings",TimeSpan.FromSeconds(1))],"log.jsonl",["known"]);
        var json=System.Text.Json.JsonSerializer.Serialize(report,GtoolsJson.Options);var restored=System.Text.Json.JsonSerializer.Deserialize<AcceptanceReport>(json,GtoolsJson.Options);
        Assert.NotNull(restored);Assert.True(restored!.Passed);Assert.Equal("build",restored.Checks[0].Id);Assert.Equal("known",restored.KnownLimitations[0]);
    }

    [Fact]
    public void Settings_RoundTripsUnplacedMonitorCoordinates()
    {
        var settings=new GtoolsSettings{MonitorEnabled=true,MonitorLeft=double.NaN,MonitorTop=double.NaN};
        var json=System.Text.Json.JsonSerializer.Serialize(settings,GtoolsJson.Options);var restored=System.Text.Json.JsonSerializer.Deserialize<GtoolsSettings>(json,GtoolsJson.Options);
        Assert.NotNull(restored);Assert.True(double.IsNaN(restored!.MonitorLeft));Assert.True(double.IsNaN(restored.MonitorTop));Assert.True(restored.MonitorEnabled);
    }

    [Fact]
    public void DiagnosticLog_WritesOneParsableJsonObjectPerLine()
    {
        // 文件名是 .jsonl，就必须真的一行一条：之前用带 WriteIndented 的公共选项写日志，
        // 每条记录跨了七八行，文件根本没法按行解析，排查问题时一条都读不出来。
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file=Path.Combine(root,"diag.jsonl");
        try
        {
            DiagnosticLog.Initialize(true,file);
            DiagnosticLog.Write("acceptance","probe.started","诊断探针已启动","Info");
            DiagnosticLog.Write("acceptance","probe.warning","诊断探针警告","Warning",
                data:new Dictionary<string,object?>{{"attempt",1}});

            var lines=System.IO.File.ReadAllLines(file);
            Assert.Equal(2,lines.Length);
            foreach(var line in lines)
            {
                var restored=System.Text.Json.JsonSerializer.Deserialize<DiagnosticEvent>(line,GtoolsJson.Options);
                Assert.NotNull(restored);
                Assert.Equal("acceptance",restored!.Component);
            }
            Assert.Contains("probe.started",lines[0]);
            Assert.Contains("probe.warning",lines[1]);
        }
        finally{DiagnosticLog.Initialize(false);Directory.Delete(root,true);}
    }

    [Fact]
    public void Settings_LoadsLegacyWindowFieldsAndDropsThemWhenSaved()
    {
        const string legacy="""
            {
              "ocrEnabled": false,
              "clipboardLimit": 777,
              "windowControlEnabled": true,
              "windowSnapEnabled": true,
              "windowSnapThreshold": 14,
              "windowRestoreMaximizedOnDrag": true
            }
            """;
        var restored=System.Text.Json.JsonSerializer.Deserialize<GtoolsSettings>(legacy,GtoolsJson.Options);
        Assert.NotNull(restored);Assert.False(restored!.OcrEnabled);Assert.Equal(777,restored.ClipboardLimit);

        var saved=System.Text.Json.JsonSerializer.Serialize(restored,GtoolsJson.Options);
        using var document=System.Text.Json.JsonDocument.Parse(saved);var root=document.RootElement;
        Assert.False(root.TryGetProperty("windowControlEnabled",out _));
        Assert.False(root.TryGetProperty("windowSnapEnabled",out _));
        Assert.False(root.TryGetProperty("windowSnapThreshold",out _));
        Assert.False(root.TryGetProperty("windowRestoreMaximizedOnDrag",out _));
        Assert.False(root.GetProperty("ocrEnabled").GetBoolean());Assert.Equal(777,root.GetProperty("clipboardLimit").GetInt32());
    }

    [Theory]
    [InlineData("Hello world","en","zh-CN")]
    [InlineData("你好，欢迎使用 Gtools","zh-CN","en")]
    public void DetectPair_SelectsExpectedDirection(string text,string source,string target)
    {
        Assert.Equal((source,target),LanguageDetector.DetectPair(text));
    }

    [Fact]
    public void Database_PersistsVocabularyAndDeduplicatesClipboard()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var db=new LocalDatabase(Path.Combine(root,"test.db"));db.Initialize();
        try
        {
            var id=db.AddVocabulary("focus","专注","Please focus.","class","note");Assert.True(id>0);Assert.Single(db.SearchVocabulary("focus"));
            var clip=new ClipboardEntry(0,"text","same","same",null,null,null,false,DateTimeOffset.Now,"HASH");Assert.True(db.AddClipboard(clip,50));Assert.False(db.AddClipboard(clip,50));Assert.True(db.AddClipboard(clip with{Hash="OTHER",Preview="other",Text="other"},50));Assert.True(db.AddClipboard(clip,50));Assert.Equal(3,db.SearchClipboard().Count);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Database_ClipboardSummariesKeepLargePayloadsOutOfTheHistoryList()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var db=new LocalDatabase(Path.Combine(root,"test.db"));db.Initialize();
        try
        {
            var image=Enumerable.Range(0,4*1024*1024).Select(i=>(byte)(i%251)).ToArray();
            Assert.True(db.AddClipboard(new(0,"image","图片 2048×1024",null,null,null,image,false,DateTimeOffset.Now,"IMAGE-HASH"),50));
            Assert.True(db.AddClipboard(new(0,"text","searchable lesson text","searchable lesson text",null,null,null,false,DateTimeOffset.Now,"TEXT-HASH"),50));

            var summaries=db.SearchClipboardSummaries();Assert.Equal(2,summaries.Count);
            var imageSummary=Assert.Single(summaries.Where(x=>x.Kind=="image"));Assert.True(imageSummary.HasImage);
            Assert.Equal("图片 2048×1024",imageSummary.Preview);
            Assert.Single(db.SearchClipboardSummaries("searchable"));

            Assert.Equal(image,db.GetClipboardImage(imageSummary.Id));
            var restored=db.GetClipboard(imageSummary.Id);Assert.NotNull(restored);Assert.Equal(image,restored!.ImagePng);
            Assert.Null(db.GetClipboard(long.MaxValue));Assert.Null(db.GetClipboardImage(long.MaxValue));
        }
        finally{Directory.Delete(root,true);}
    }
}
