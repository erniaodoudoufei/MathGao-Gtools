using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using Gtools.Core;

namespace Gtools.App;

internal sealed class AcceptanceRunner(LocalDatabase database,DesktopModuleRegistry modules,OcrWorkerClient ocr)
{
    public async Task<AcceptanceReport> RunAsync(string? reportPath,CancellationToken token=default)
    {
        var started=DateTimeOffset.Now;var checks=new List<AcceptanceCheck>();
        await Check("profile.isolation","测试配置隔离",()=>
        {
            var actual=Path.GetFullPath(AppPaths.Root);var production=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Gtools"));
            return Task.FromResult(!actual.Equals(production,StringComparison.OrdinalIgnoreCase)&&!actual.Equals(Path.GetPathRoot(actual),StringComparison.OrdinalIgnoreCase));
        },checks);
        await Check("diagnostics.write","结构化诊断日志",()=>{DiagnosticLog.Write("acceptance","write.probe","诊断写入探针");return Task.FromResult(DiagnosticLog.CurrentPath is not null&&File.Exists(DiagnosticLog.CurrentPath));},checks);
        await Check("database.persistence","生词与剪贴板持久化",()=>Task.FromResult(TestDatabase()),checks);
        await Check("clipboard.recency","剪贴板最近使用排序与去重",()=>Task.FromResult(TestClipboardRecency()),checks);
        await Check("todo.style.persistence","待办样式持久化与撤销",()=>Task.FromResult(TestTodoStyle()),checks);
        await Check("todo.calendar.formatting","离线农历、闰月与英文星期",()=>Task.FromResult(
            TodoCalendarFormatter.Format(new DateTime(2026,10,15))==new TodoCalendarText("九月初六·Thu.","农历九月初六，星期四")&&
            TodoCalendarFormatter.Format(new DateTime(2025,7,25)).Text=="闰六月初一·Fri."),checks);
        await Check("workers.available","隔离 Worker 可定位",()=>Task.FromResult(new[]{"Gtools.OcrWorker","Gtools.LongShotWorker"}.All(x=>File.Exists(WorkerLocator.Find(x)))),checks);
        await Check("modules.health","模块注册表健康状态",async()=>{var health=await modules.GetHealthAsync(token);return modules.Modules.Count==7&&health.Values.All(x=>x.Health!=ModuleHealth.Failed);},checks);
        await Check("ocr.ipc","PaddleOCR 命名管道与中英模型",async()=>
        {
            var response=await ocr.RecognizeAsync(CreateOcrProbe(),token);return response.Success&&response.Text.Contains("12345",StringComparison.OrdinalIgnoreCase);
        },checks);
        var version=Assembly.GetExecutingAssembly().GetName().Version?.ToString()??"unknown";
        var report=new AcceptanceReport(version,AppPaths.Root,started,DateTimeOffset.Now,checks.All(x=>x.Passed),checks.ToArray(),DiagnosticLog.CurrentPath,
        ["剪贴板合成与WPF自动测试通过不代表真实浏览器或聊天软件复制已验收","本自动验收不代表真实微信通过；手动逐帧校对需在用户预先打开的不敏感聊天中另行验收","QQ与Generic尚未验收","OpenAI Provider 仅在用户已配置 Key 时联网测试","网页、PDF、OneNote 自动长截图不在本轮范围"]);
        var target=reportPath??Path.Combine(AppPaths.Logs,$"acceptance-{DateTime.Now:yyyyMMdd-HHmmss}.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await File.WriteAllTextAsync(target,JsonSerializer.Serialize(report,GtoolsJson.Options),token);return report;
    }

    private bool TestDatabase()
    {
        var marker="acceptance-"+Guid.NewGuid().ToString("N");var id=database.AddVocabulary(marker,"验收","Gtools acceptance","test","isolated");
        var vocab=database.SearchVocabulary(marker).Any(x=>x.Id==id&&x.Meaning=="验收");database.DeleteVocabulary(id);
        var clip=new ClipboardEntry(0,"text",marker,marker,"<b>Gtools</b>","{\\rtf1 Gtools}",null,false,DateTimeOffset.Now,marker);
        var first=database.AddClipboard(clip,500);var duplicate=database.AddClipboard(clip,500);var search=database.SearchClipboard(marker).Any(x=>x.Hash==marker);
        foreach(var x in database.SearchClipboard(marker))database.DeleteClipboard(x.Id);return vocab&&first&&!duplicate&&search;
    }

    private bool TestClipboardRecency()
    {
        var marker="recency"+Guid.NewGuid().ToString("N");
        var a=new ClipboardEntry(0,"text",marker+" old",marker+" old",null,null,null,false,DateTimeOffset.Now,marker+"a");
        var b=a with{Preview=marker+" new",Text=marker+" new",Hash=marker+"b",IsFavorite=true};
        try
        {
            if(!database.AddClipboard(a,500)||!database.AddClipboard(b,500))return false;
            var rows=database.SearchClipboard(marker);var older=rows.Single(x=>x.Hash==a.Hash);var newer=rows.Single(x=>x.Hash==b.Hash);
            if(!database.RecordClipboardUse(older.Id)||database.SearchClipboardSummaries(marker).First().Id!=older.Id)return false;
            if(database.GetClipboard(older.Id)!=older)return false;
            var duplicate=database.AddClipboard(b,500,out var changed);
            return !duplicate&&changed&&database.SearchClipboard(marker).Count==2&&database.SearchClipboardSummaries(marker).First().Id==newer.Id;
        }
        finally{foreach(var row in database.SearchClipboard(marker))database.DeleteClipboard(row.Id);}
    }

    private bool TestTodoStyle()
    {
        var id=database.AddTodo("合成样式验收");
        try
        {
            database.UpdateTodoStyle(id,true,true,TodoTextColor.Purple);database.SetTodoDone(id,true);
            var before=database.ListTodo().Single(x=>x.Id==id);
            var history=new TodoUndoHistory(database);history.Delete(id);history.Undo();
            var reopened=new LocalDatabase();reopened.Initialize();
            return reopened.ListTodo().Single(x=>x.Id==id)==before&&before.IsBold&&before.IsItalic&&before.TextColor==TodoTextColor.Purple;
        }
        finally{database.DeleteTodo(id);}
    }

    private static byte[] CreateOcrProbe()
    {
        using var bitmap=new Bitmap(960,240,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.White);
        using var font=new System.Drawing.Font("Arial",64,System.Drawing.FontStyle.Bold,System.Drawing.GraphicsUnit.Pixel);graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("Gtools OCR 12345",font,Brushes.Black,new PointF(28,70));using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);return stream.ToArray();
    }

    private static async Task Check(string id,string name,Func<Task<bool>> action,List<AcceptanceCheck> output)
    {
        var sw=Stopwatch.StartNew();try{var passed=await action();output.Add(new(id,name,passed,passed?"通过":"返回未通过",sw.Elapsed));DiagnosticLog.Write("acceptance","check.completed",$"{name}: {(passed?"PASS":"FAIL")}",passed?"Info":"Error",data:new Dictionary<string,object?>{{"id",id},{"elapsedMs",sw.ElapsedMilliseconds}});}
        catch(Exception ex){output.Add(new(id,name,false,ex.ToString(),sw.Elapsed));DiagnosticLog.Write("acceptance","check.failed",ex.ToString(),"Error",data:new Dictionary<string,object?>{{"id",id}});}
    }
}
