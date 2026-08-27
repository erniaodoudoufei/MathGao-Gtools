using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gtools.Core;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace Gtools.App;

internal static class Ui
{
    public static TextBlock Text(string value, double size=14, FontWeight? weight=null) => new() { Text=value,FontSize=size,FontWeight=weight??FontWeights.Normal,Margin=new Thickness(4),TextWrapping=TextWrapping.Wrap };
    public static Button Button(string label, RoutedEventHandler click) { var b=new Button{Content=label};b.Click+=click;return b; }
    public static Window Window(string title,double w,double h) => new(){Title=$"Gtools · {title}",Width=w,Height=h,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=new SolidColorBrush(Color.FromRgb(248,248,250))};
}

internal sealed class DashboardWindow : Window
{
    private readonly StackPanel _status = new();private readonly TextBlock _health=Ui.Text("正在检查模块健康状态…",12);
    public DashboardWindow(GtoolsSettings s, Action settings, Action vocab, Action clipboard, Action ocr, Action translate, Action longshot)
    {
        Title="Gtools";Width=660;Height=650;MinWidth=560;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel{Margin=new Thickness(20)};
        var head=new StackPanel();head.Children.Add(Ui.Text("Gtools",32,FontWeights.Bold));head.Children.Add(Ui.Text("轻量主入口，功能按模块独立运行。",14));
        DockPanel.SetDock(head,Dock.Top);root.Children.Add(head);
        var actions=new WrapPanel{Margin=new Thickness(0,14,0,14)};
        actions.Children.Add(Ui.Button("截图 OCR  Alt+B",(_,_)=>ocr()));actions.Children.Add(Ui.Button("截图翻译  Alt+S",(_,_)=>translate()));
        actions.Children.Add(Ui.Button("聊天长截图",(_,_)=>longshot()));actions.Children.Add(Ui.Button("生词本",(_,_)=>vocab()));actions.Children.Add(Ui.Button("剪贴板",(_,_)=>clipboard()));actions.Children.Add(Ui.Button("设置",(_,_)=>settings()));
        DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);_status.Children.Add(_health);root.Children.Add(new ScrollViewer{Content=_status,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Content=root;
        Refresh(s);
    }
    public void Refresh(GtoolsSettings s)
    {
        _status.Children.Clear();_status.Children.Add(Ui.Text("模块状态",19,FontWeights.SemiBold));
        Add("OCR / 翻译 / 生词本",s.OcrEnabled&&s.TranslationEnabled,"Alt+B / Alt+S；OCR Worker 首次运行会准备本地模型");
        Add("聊天长截图",s.LongShotEnabled,$"Alt+Shift+S；{s.LongShotProfile}；{(s.LongShotScrollUp?"向上":"向下")}滚动，步长自动校准");
        Add("历史剪贴板",s.ClipboardEnabled,"Ctrl+~；SQLite FTS5，文字/富文本/图片");
        Add("性能悬浮条",s.MonitorEnabled,"上/下行、CPU、内存，每秒刷新");
        Add("待办清单",s.TodoEnabled,s.TodoExpanded?"拼在流量条上方，可勾选、删除、调顺序":"已折叠，点流量条右侧箭头或托盘菜单展开");
        _status.Children.Add(_health);
    }
    public void SetHealth((string Name,ModuleStatus Status)[] statuses)=>_health.Text="健康检查："+string.Join(" · ",statuses.Select(x=>$"{x.Name} {HealthMark(x.Status.Health)}"));
    private static string HealthMark(ModuleHealth health)=>health switch{ModuleHealth.Healthy=>"●",ModuleHealth.Degraded=>"▲",ModuleHealth.Failed=>"×",_=>"○"};
    private void Add(string name,bool enabled,string detail)
    {
        var border=new Border{Background=Brushes.White,CornerRadius=new CornerRadius(8),Margin=new Thickness(3),Padding=new Thickness(10)};
        var row=new StackPanel();row.Children.Add(Ui.Text((enabled?"●  ":"○  ")+name,15,FontWeights.SemiBold));row.Children.Add(Ui.Text(detail,12));border.Child=row;_status.Children.Add(border);
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e){e.Cancel=true;Hide();}
}

internal sealed class DiagnosticsWindow : Window
{
    private readonly TextBox _output=new(){AcceptsReturn=true,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly Func<Task<IReadOnlyDictionary<string,ModuleStatus>>> _health;
    public DiagnosticsWindow(string dataRoot,string? logPath,Func<Task<IReadOnlyDictionary<string,ModuleStatus>>> health)
    {
        _health=health;Title="Gtools · 桌面测试工具";Width=760;Height=520;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel{Margin=new Thickness(12)};var top=new WrapPanel();top.Children.Add(Ui.Button("刷新模块健康",async(_,_)=>await RefreshHealth()));top.Children.Add(Ui.Button("写入剪贴板格式探针",(_,_)=>ClipboardProbe()));top.Children.Add(Ui.Button("打开测试数据目录",(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",dataRoot){UseShellExecute=true})));
        DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);root.Children.Add(_output);Content=root;Loaded+=async(_,_)=>{Append($"数据目录：{dataRoot}\n诊断日志：{logPath??"未启用"}");await RefreshHealth();};
    }
    private async Task RefreshHealth(){var health=await _health();Append(string.Join(Environment.NewLine,health.Select(x=>$"{x.Key}: {x.Value.Health} · {x.Value.Message}")));}
    private void ClipboardProbe()
    {
        var data=new DataObject();data.SetText("Gtools Unicode 剪贴板验收 12345",TextDataFormat.UnicodeText);data.SetData(DataFormats.Html,"Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000144\r\nStartFragment:00000131\r\nEndFragment:00000136\r\n<html><body><!--StartFragment--><b>Gtools</b><!--EndFragment--></body></html>");data.SetData(DataFormats.Rtf,"{\\rtf1\\ansi Gtools RTF}");Clipboard.SetDataObject(data,true);Append("已写入 Unicode + HTML + RTF 探针；可用 Ctrl+~ 验证记录与恢复。 ");
    }
    private void Append(string text){_output.AppendText((string.IsNullOrEmpty(_output.Text)?"":Environment.NewLine+Environment.NewLine)+text);_output.ScrollToEnd();}
}

internal sealed class TranslationWindow : Window
{
    private readonly TextBox _source=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBox _target=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock _meta=Ui.Text("",12);
    private readonly StackPanel _compare=new();
    private readonly ScrollViewer _compareHost;
    private string _sl="auto",_tl="zh-CN";
    private readonly Func<string,string,string,string,Task<TranslationResult>> _run;
    private readonly Func<string,string,string,Task<IReadOnlyList<ProviderOutcome>>> _compareAll;

    public TranslationWindow(string source, TranslationResult result, string sl,string tl,
        Func<string,string,string,string,Task<TranslationResult>> run,
        Func<string,string,string,Task<IReadOnlyList<ProviderOutcome>>> compareAll,
        Action<string,string,string> saveWord)
    {
        _run=run;_compareAll=compareAll;_sl=sl;_tl=tl;Title="Gtools · 截图翻译";Width=760;Height=560;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _source.Text=source;_target.Text=result.Text;_meta.Text=Meta(result);
        _compareHost=new ScrollViewer{Content=_compare,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Visibility=Visibility.Collapsed,MaxHeight=210};

        var grid=new Grid{Margin=new Thickness(12)};
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
        Grid.SetColumnSpan(_meta,2);grid.Children.Add(_meta);
        Grid.SetRow(_source,1);Grid.SetColumn(_source,0);grid.Children.Add(_source);
        Grid.SetRow(_target,1);Grid.SetColumn(_target,1);grid.Children.Add(_target);
        Grid.SetRow(_compareHost,2);Grid.SetColumnSpan(_compareHost,2);grid.Children.Add(_compareHost);

        var bar=new WrapPanel();
        bar.Children.Add(Ui.Button("复制译文",(_,_)=>Clipboard.SetText(_target.Text)));
        bar.Children.Add(Ui.Button("交换语言",async(_,_)=>{(_sl,_tl)=(_tl,_sl);(_source.Text,_target.Text)=(_target.Text,_source.Text);await Retry();}));
        bar.Children.Add(Ui.Button("重试",async(_,_)=>await Retry()));
        bar.Children.Add(Ui.Button("多引擎对照",async(_,_)=>await CompareAsync()));
        bar.Children.Add(Ui.Button("加入生词本",(_,_)=>saveWord(_source.SelectedText.Length>0?_source.SelectedText:_source.Text,_target.Text,_source.Text)));
        Grid.SetRow(bar,3);Grid.SetColumnSpan(bar,2);grid.Children.Add(bar);Content=grid;
    }

    private string Meta(TranslationResult r)=>$"{_sl} → {_tl} · {r.Provider} · {r.Elapsed.TotalSeconds:0.0}s"+(r.IsUnofficial?" · 非官方通道可能失效":"");

    private async Task Retry()
    {
        try{_meta.Text="正在翻译…";var r=await _run(_source.Text,_sl,_tl,"default");_target.Text=r.Text;_meta.Text=Meta(r);}
        catch(Exception ex){_meta.Text=ex.Message;}
    }

    /// <summary>所有可用引擎并发跑一遍，逐条列出来对照，某一路失败只显示该路的错误。</summary>
    private async Task CompareAsync()
    {
        _meta.Text="正在向所有可用引擎请求…";_compare.Children.Clear();_compareHost.Visibility=Visibility.Visible;
        try
        {
            var results=await _compareAll(_source.Text,_sl,_tl);
            _compare.Children.Clear();
            foreach(var outcome in results)
            {
                var card=new Border{Background=Brushes.White,CornerRadius=new CornerRadius(6),Padding=new Thickness(10),Margin=new Thickness(0,0,0,6)};
                var stack=new StackPanel();
                stack.Children.Add(Ui.Text(outcome.Provider+(outcome.Error is null?$" · {outcome.Elapsed.TotalSeconds:0.0}s":" · 失败"),12,FontWeights.SemiBold));
                var body=Ui.Text(outcome.Error??outcome.Text,13);
                if(outcome.Error is not null)body.Foreground=Brushes.Firebrick;
                body.TextWrapping=TextWrapping.Wrap;stack.Children.Add(body);
                if(outcome.Error is null)
                {
                    var use=Ui.Button("用这条",(_,_)=>{_target.Text=outcome.Text;_meta.Text=$"{_sl} → {_tl} · 已采用 {outcome.Provider}";});
                    use.HorizontalAlignment=HorizontalAlignment.Left;stack.Children.Add(use);
                }
                card.Child=stack;_compare.Children.Add(card);
            }
            var ok=results.Count(x=>x.Error is null);
            _meta.Text=$"{_sl} → {_tl} · 对照完成，{ok}/{results.Count} 个引擎返回结果";
        }
        catch(Exception ex){_meta.Text=ex.Message;}
    }
}

internal sealed record ProviderOutcome(string Provider,string Text,TimeSpan Elapsed,string? Error);

internal sealed class OcrEditorWindow : Window
{
    public OcrEditorWindow(string text)
    {
        Title="Gtools · OCR 校对";Width=620;Height=430;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var box=new TextBox{Text=text,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(10)};
        var root=new DockPanel();var bar=new StackPanel{Orientation=Orientation.Horizontal};bar.Children.Add(Ui.Button("复制",(_,_)=>Clipboard.SetText(box.Text)));bar.Children.Add(Ui.Button("关闭",(_,_)=>Close()));DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);root.Children.Add(box);Content=root;
    }
}

internal sealed class VocabularyWindow : Window
{
    private readonly LocalDatabase _db;private readonly DictionaryService _dictionary;
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single,
        HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,
        CanUserSortColumns=true,RowHeight=30,BorderThickness=new Thickness(0)};
    private readonly TextBox _search=new(){Width=220,VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(4,3,4,3)};
    private readonly ComboBox _tagFilter=new(){Width=130,Margin=new Thickness(4,0,4,0)};
    private readonly TextBlock _detail=Ui.Text("",13);
    private readonly TextBlock _count=Ui.Text("",12);
    private bool _suppress;

    public VocabularyWindow(LocalDatabase db,DictionaryService dictionary)
    {
        _db=db;_dictionary=dictionary;Title="Gtools · 英语生词本";Width=1000;Height=620;MinWidth=760;MinHeight=460;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new SolidColorBrush(Color.FromRgb(248,248,250));

        Column("单词",nameof(VocabularyEntry.Word),150,FontWeights.SemiBold);
        Column("音标",nameof(VocabularyEntry.Phonetic),160);
        Column("释义",nameof(VocabularyEntry.Meaning),330);
        Column("标签",nameof(VocabularyEntry.Tags),130);
        _grid.Columns.Add(new DataGridTextColumn{Header="熟练度",Width=70,Binding=new System.Windows.Data.Binding(nameof(VocabularyEntry.Familiarity)){Converter=new StarsConverter()}});
        _grid.Columns.Add(new DataGridTextColumn{Header="加入时间",Width=110,Binding=new System.Windows.Data.Binding(nameof(VocabularyEntry.CreatedAt)){StringFormat="yyyy-MM-dd"}});

        var root=new DockPanel{Margin=new Thickness(12)};
        var top=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        _search.TextChanged+=(_,_)=>Refresh();
        _tagFilter.SelectionChanged+=(_,_)=>{if(!_suppress)Refresh();};
        top.Children.Add(Ui.Text("搜索",12));top.Children.Add(_search);
        top.Children.Add(Ui.Text("标签",12));top.Children.Add(_tagFilter);
        top.Children.Add(Ui.Button("新增",(_,_)=>Edit(null)));
        top.Children.Add(Ui.Button("编辑",(_,_)=>Edit(Selected)));
        top.Children.Add(Ui.Button("重新查词",async(_,_)=>await RefetchAsync()));
        top.Children.Add(Ui.Button("删除",(_,_)=>Delete()));
        top.Children.Add(Ui.Button("背单词",(_,_)=>Review()));
        top.Children.Add(Ui.Button("导出 CSV",(_,_)=>Export()));
        DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        DockPanel.SetDock(_count,Dock.Bottom);root.Children.Add(_count);

        _detail.Margin=new Thickness(12);
        var detail=new Border{Background=Brushes.White,CornerRadius=new CornerRadius(8),Margin=new Thickness(10,0,0,0),
            Child=new ScrollViewer{Content=_detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},Width=300};
        DockPanel.SetDock(detail,Dock.Right);root.Children.Add(detail);
        root.Children.Add(new Border{Background=Brushes.White,CornerRadius=new CornerRadius(8),Padding=new Thickness(4),Child=_grid});

        _grid.SelectionChanged+=(_,_)=>ShowDetail(Selected);
        _grid.MouseDoubleClick+=(_,_)=>Edit(Selected);
        Content=root;Refresh();
    }

    private VocabularyEntry? Selected=>_grid.SelectedItem as VocabularyEntry;

    private void Column(string header,string path,double width,FontWeight? weight=null)
    {
        var column=new DataGridTextColumn{Header=header,Width=width,Binding=new System.Windows.Data.Binding(path)};
        if(weight is not null)column.FontWeight=weight.Value;
        column.ElementStyle=new Style(typeof(TextBlock));
        column.ElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        column.ElementStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center));
        _grid.Columns.Add(column);
    }

    private void Refresh()
    {
        var all=_db.SearchVocabulary(_search.Text);
        var tags=all.SelectMany(x=>SplitTags(x.Tags)).Distinct().OrderBy(x=>x).ToList();
        var selectedTag=_tagFilter.SelectedItem as string;
        _suppress=true;
        _tagFilter.ItemsSource=new[]{"全部标签"}.Concat(tags).ToArray();
        _tagFilter.SelectedItem=selectedTag is not null&&(selectedTag=="全部标签"||tags.Contains(selectedTag))?selectedTag:"全部标签";
        _suppress=false;
        var filter=_tagFilter.SelectedItem as string;
        var rows=filter is null or "全部标签"?all:all.Where(x=>SplitTags(x.Tags).Contains(filter,StringComparer.OrdinalIgnoreCase)).ToList();
        _grid.ItemsSource=rows;
        _count.Text=$"共 {all.Count} 个生词，当前显示 {rows.Count} 个。双击一行可编辑；熟练度由“背单词”累计。";
        ShowDetail(Selected);
    }

    private static string[] SplitTags(string tags)=>tags.Split(new[]{',','，',';','；',' '},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

    private void ShowDetail(VocabularyEntry? item)
    {
        if(item is null){_detail.Text="选中一个生词查看详情。";return;}
        var sb=new StringBuilder();
        sb.AppendLine(item.Word);
        if(item.Phonetic.Length>0)sb.AppendLine(item.Phonetic);
        sb.AppendLine();
        if(item.Meaning.Length>0){sb.AppendLine("释义");sb.AppendLine(item.Meaning);sb.AppendLine();}
        if(item.Context.Length>0){sb.AppendLine("原句");sb.AppendLine(item.Context);sb.AppendLine();}
        if(item.Notes.Length>0){sb.AppendLine("例句 / 备注");sb.AppendLine(item.Notes);sb.AppendLine();}
        if(item.Tags.Length>0)sb.AppendLine("标签："+item.Tags);
        sb.AppendLine($"熟练度：{new string('★',item.Familiarity)}{new string('☆',5-item.Familiarity)}");
        if(item.LastReviewed is {} reviewed)sb.AppendLine($"上次复习：{reviewed:yyyy-MM-dd HH:mm}");
        _detail.Text=sb.ToString();
    }

    private void Edit(VocabularyEntry? item)
    {
        var d=new VocabularyEditWindow(item,_dictionary){Owner=this};
        if(d.ShowDialog()!=true)return;
        if(item is null)_db.AddVocabulary(d.Word,d.Meaning,d.Context,d.Tags,d.Notes,d.Phonetic);
        else _db.UpdateVocabulary(item with{Word=d.Word,Meaning=d.Meaning,Context=d.Context,Tags=d.Tags,Notes=d.Notes,Phonetic=d.Phonetic});
        Refresh();
    }

    /// <summary>给以前只存了译文、没有音标和例句的老生词补齐词典信息。</summary>
    private async Task RefetchAsync()
    {
        if(Selected is not {} item)return;
        _count.Text=$"正在查询 {item.Word} …";
        var lookup=await _dictionary.LookupAsync(item.Word);
        if(lookup.IsEmpty){_count.Text=$"没有查到 {item.Word}，可能不是英文单词或接口暂时不可用。";return;}
        _db.UpdateVocabulary(item with
        {
            Phonetic=lookup.Phonetic.Length>0?lookup.Phonetic:item.Phonetic,
            Meaning=lookup.Meaning.Length>0?lookup.Meaning:item.Meaning,
            Tags=item.Tags.Length>0?item.Tags:string.Join(',',lookup.Tags),
            Notes=item.Notes.Length>0?item.Notes:VocabularyEditWindow.FormatExamples(lookup)
        });
        Refresh();
    }

    private void Delete()
    {
        if(Selected is not {} item)return;
        if(MessageBox.Show($"删除生词 {item.Word}？","Gtools",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        _db.DeleteVocabulary(item.Id);Refresh();
    }

    private void Review()
    {
        var items=(_grid.ItemsSource as IEnumerable<VocabularyEntry>)?.ToList()??new List<VocabularyEntry>();
        if(items.Count==0){MessageBox.Show("当前列表没有可复习的生词。","Gtools");return;}
        new VocabularyReviewWindow(items,_db){Owner=this}.ShowDialog();Refresh();
    }

    private void Export()
    {
        var d=new SaveFileDialog{Filter="CSV 文件|*.csv",FileName=$"Gtools-vocabulary-{DateTime.Now:yyyyMMdd}.csv"};if(d.ShowDialog()!=true)return;
        var sb=new StringBuilder("Word,Phonetic,Meaning,Context,Tags,Notes,Familiarity,CreatedAt\r\n");
        foreach(var x in _db.SearchVocabulary())
            sb.AppendLine(string.Join(',',new[]{x.Word,x.Phonetic,x.Meaning,x.Context,x.Tags,x.Notes,x.Familiarity.ToString(CultureInfo.InvariantCulture),x.CreatedAt.ToString("O")}.Select(Csv)));
        File.WriteAllText(d.FileName,sb.ToString(),new UTF8Encoding(true));
    }
    private static string Csv(string s)=>$"\"{s.Replace("\"","\"\"")}\"";

    private sealed class StarsConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
        {
            var n=Math.Clamp(value is int i?i:0,0,5);return new string('★',n)+new string('☆',5-n);
        }
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>0;
    }
}

internal sealed class VocabularyEditWindow : Window
{
    private readonly TextBox _word=new(),_phonetic=new(),_meaning=new(),_context=new(),_tags=new(),_notes=new();
    private readonly TextBlock _status=Ui.Text("",12);
    private readonly DictionaryService _dictionary;

    public string Word=>_word.Text.Trim();public string Phonetic=>_phonetic.Text.Trim();public string Meaning=>_meaning.Text.Trim();
    public string Context=>_context.Text.Trim();public string Tags=>_tags.Text.Trim();public string Notes=>_notes.Text.Trim();

    public VocabularyEditWindow(VocabularyEntry? entry,DictionaryService dictionary,DictionaryLookup? prefill=null)
    {
        _dictionary=dictionary;Title="Gtools · 生词";Width=560;Height=610;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _meaning.AcceptsReturn=true;_meaning.Height=76;_meaning.TextWrapping=TextWrapping.Wrap;
        _context.AcceptsReturn=true;_context.Height=54;_context.TextWrapping=TextWrapping.Wrap;
        _notes.AcceptsReturn=true;_notes.Height=96;_notes.TextWrapping=TextWrapping.Wrap;
        _notes.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;

        if(entry is not null)
        {
            _word.Text=entry.Word;_phonetic.Text=entry.Phonetic;_meaning.Text=entry.Meaning;
            _context.Text=entry.Context;_tags.Text=entry.Tags;_notes.Text=entry.Notes;
        }
        if(prefill is not null)Apply(prefill,overwrite:false);

        var panel=new StackPanel{Margin=new Thickness(14)};
        Field(panel,"单词 / 短语",_word);
        var lookupBar=new WrapPanel();
        lookupBar.Children.Add(Ui.Button("查词并填充",async(_,_)=>await LookupAsync(true)));
        lookupBar.Children.Add(Ui.Button("只补空白项",async(_,_)=>await LookupAsync(false)));
        lookupBar.Children.Add(_status);
        panel.Children.Add(lookupBar);
        Field(panel,"音标",_phonetic);
        Field(panel,"释义",_meaning);
        Field(panel,"原句（截图/翻译时的上下文）",_context);
        Field(panel,"标签（逗号分隔，查词会自动带上考试标签）",_tags);
        Field(panel,"例句 / 备注",_notes);

        var bar=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
        bar.Children.Add(Ui.Button("保存",(_,_)=>{if(Word.Length>0)DialogResult=true;else _status.Text="单词不能为空。";}));
        bar.Children.Add(Ui.Button("取消",(_,_)=>DialogResult=false));
        panel.Children.Add(bar);
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};

        // 新建且已经填了单词（例如从翻译窗口带过来）时自动查一次，省一次点击。
        if(entry is null&&prefill is null&&Word.Length>0)Loaded+=async(_,_)=>await LookupAsync(false);
    }

    private static void Field(Panel panel,string label,TextBox box)
    {
        panel.Children.Add(Ui.Text(label,12));box.Margin=new Thickness(4,0,4,6);panel.Children.Add(box);
    }

    private async Task LookupAsync(bool overwrite)
    {
        if(Word.Length==0){_status.Text="先填单词。";return;}
        _status.Text="查询中…";
        var lookup=await _dictionary.LookupAsync(Word);
        if(lookup.IsEmpty){_status.Text="没查到，可能不是英文单词或接口暂时不可用。";return;}
        Apply(lookup,overwrite);
        _status.Text=lookup.Sources.Length>0?"来源："+string.Join(" + ",lookup.Sources):"已填充";
    }

    private void Apply(DictionaryLookup lookup,bool overwrite)
    {
        Set(_phonetic,lookup.Phonetic,overwrite);
        Set(_meaning,lookup.Meaning,overwrite);
        Set(_tags,string.Join(',',lookup.Tags),overwrite);
        Set(_notes,FormatExamples(lookup),overwrite);
    }

    private static void Set(TextBox box,string value,bool overwrite)
    {
        if(value.Length==0)return;
        if(overwrite||box.Text.Trim().Length==0)box.Text=value;
    }

    public static string FormatExamples(DictionaryLookup lookup)
    {
        var sb=new StringBuilder();
        foreach(var example in lookup.Examples)
        {
            sb.AppendLine(example.Source);
            if(example.Translation.Length>0)sb.AppendLine(example.Translation);
            sb.AppendLine();
        }
        if(lookup.EnglishDefinition.Length>0){sb.AppendLine("英英释义");sb.AppendLine(lookup.EnglishDefinition);}
        return sb.ToString().Trim();
    }
}

/// <summary>抽卡式复习：先只看单词，想不起来再翻面，认识/不认识会累计熟练度。</summary>
internal sealed class VocabularyReviewWindow : Window
{
    private readonly List<VocabularyEntry> _items;private readonly LocalDatabase _db;
    private readonly TextBlock _word=Ui.Text("",34,FontWeights.Bold);
    private readonly TextBlock _phonetic=Ui.Text("",15);
    private readonly TextBlock _back=Ui.Text("",14);
    private readonly TextBlock _progress=Ui.Text("",12);
    private readonly Button _flip;
    private int _index;private bool _revealed;private int _known;

    public VocabularyReviewWindow(IEnumerable<VocabularyEntry> items,LocalDatabase db)
    {
        _db=db;_items=items.OrderBy(x=>x.Familiarity).ThenBy(_=>Guid.NewGuid()).ToList();
        Title="Gtools · 背单词";Width=560;Height=470;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(248,248,250));

        var panel=new StackPanel{Margin=new Thickness(20)};
        _word.HorizontalAlignment=HorizontalAlignment.Center;_phonetic.HorizontalAlignment=HorizontalAlignment.Center;
        _back.Visibility=Visibility.Collapsed;
        panel.Children.Add(_progress);panel.Children.Add(_word);panel.Children.Add(_phonetic);
        panel.Children.Add(new Border{Background=Brushes.White,CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,10,0,10),
            Child=new ScrollViewer{Content=_back,Height=170,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});

        _flip=Ui.Button("显示释义 (空格)",(_,_)=>Reveal());
        var bar=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Center};
        bar.Children.Add(_flip);
        bar.Children.Add(Ui.Button("认识 (→)",(_,_)=>Answer(true)));
        bar.Children.Add(Ui.Button("不认识 (←)",(_,_)=>Answer(false)));
        panel.Children.Add(bar);
        Content=panel;

        KeyDown+=(_,e)=>
        {
            if(e.Key==Key.Space){Reveal();e.Handled=true;}
            else if(e.Key==Key.Right){Answer(true);e.Handled=true;}
            else if(e.Key==Key.Left){Answer(false);e.Handled=true;}
            else if(e.Key==Key.Escape)Close();
        };
        Show(0);
    }

    private void Show(int index)
    {
        _index=index;_revealed=false;
        if(_index>=_items.Count)
        {
            _word.Text="完成";_phonetic.Text="";
            _back.Text=$"这一轮 {_items.Count} 个生词，认识 {_known} 个。";
            _back.Visibility=Visibility.Visible;_flip.IsEnabled=false;_progress.Text="";
            return;
        }
        var item=_items[_index];
        _word.Text=item.Word;_phonetic.Text=item.Phonetic;
        _back.Text=string.Join("\n\n",new[]{item.Meaning,item.Context,item.Notes}.Where(x=>x.Length>0));
        _back.Visibility=Visibility.Collapsed;_flip.IsEnabled=true;
        _progress.Text=$"{_index+1} / {_items.Count} · 熟练度 {new string('★',item.Familiarity)}{new string('☆',5-item.Familiarity)}";
    }

    private void Reveal(){if(_index<_items.Count){_revealed=true;_back.Visibility=Visibility.Visible;}}

    private void Answer(bool remembered)
    {
        if(_index>=_items.Count)return;
        if(!_revealed){Reveal();return;}
        _db.RecordReview(_items[_index].Id,remembered);
        if(remembered)_known++;
        Show(_index+1);
    }
}

internal sealed class ClipboardWindow : Window
{
    private readonly LocalDatabase _db;private readonly ClipboardHistoryService _service;private readonly DataGrid _grid=new(){AutoGenerateColumns=false,IsReadOnly=true,RowHeight=64};private readonly TextBox _search=new();private readonly IntPtr _returnTo;
    public ClipboardWindow(LocalDatabase db,ClipboardHistoryService service,IntPtr returnTo)
    {
        _db=db;_service=service;_returnTo=returnTo;Title="Gtools · 历史剪贴板";Width=850;Height=520;WindowStartupLocation=WindowStartupLocation.CenterScreen;Topmost=true;BuildColumns();
        var root=new DockPanel{Margin=new Thickness(8)};var top=new StackPanel{Orientation=Orientation.Horizontal};_search.Width=300;_search.TextChanged+=(_,_)=>Refresh();top.Children.Add(_search);
        top.Children.Add(Ui.Button("粘贴",async(_,_)=>await Paste()));top.Children.Add(Ui.Button("收藏/取消",(_,_)=>Favorite()));top.Children.Add(Ui.Button("删除",(_,_)=>Delete()));top.Children.Add(Ui.Button("清空未收藏",(_,_)=>{_db.ClearClipboard();Refresh();}));
        top.Children.Add(Ui.Button(service.Paused?"继续记录":"暂停记录",(s,_)=>{service.Paused=!service.Paused;((Button)s).Content=service.Paused?"继续记录":"暂停记录";}));DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        _grid.MouseDoubleClick+=async(_,_)=>await Paste();_grid.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;await Paste();}};root.Children.Add(_grid);Content=root;Loaded+=(_,_)=>{Refresh();_search.Focus();};
        _service.Updated+=OnUpdated;Closed+=(_,_)=>_service.Updated-=OnUpdated;
    }
    private void OnUpdated(object? s,EventArgs e)=>Dispatcher.Invoke(Refresh);
    private void BuildColumns()
    {
        _grid.Columns.Add(new DataGridTextColumn{Header="★",Binding=new System.Windows.Data.Binding(nameof(ClipboardViewRow.Favorite)),Width=36});
        var imageFactory=new FrameworkElementFactory(typeof(Image));imageFactory.SetBinding(Image.SourceProperty,new System.Windows.Data.Binding(nameof(ClipboardViewRow.Thumbnail)));imageFactory.SetValue(Image.StretchProperty,Stretch.Uniform);var template=new DataTemplate{VisualTree=imageFactory};_grid.Columns.Add(new DataGridTemplateColumn{Header="预览",CellTemplate=template,Width=100});
        _grid.Columns.Add(new DataGridTextColumn{Header="内容",Binding=new System.Windows.Data.Binding(nameof(ClipboardViewRow.Preview)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});_grid.Columns.Add(new DataGridTextColumn{Header="类型",Binding=new System.Windows.Data.Binding(nameof(ClipboardViewRow.Kind)),Width=80});_grid.Columns.Add(new DataGridTextColumn{Header="时间",Binding=new System.Windows.Data.Binding(nameof(ClipboardViewRow.Time)),Width=145});
    }
    private void Refresh(){try{_grid.ItemsSource=_db.SearchClipboard(_search.Text).Select(x=>new ClipboardViewRow(x)).ToArray();Title=$"Gtools · 历史剪贴板 · {_db.DatabaseSizeBytes/1024d/1024:0.0} MB";}catch{_grid.ItemsSource=Array.Empty<ClipboardViewRow>();}}
    private async Task Paste(){if(_grid.SelectedItem is not ClipboardViewRow row)return;_service.Restore(row.Entry);Close();await Task.Delay(100);NativeMethods.ShowWindow(_returnTo,NativeMethods.SW_RESTORE);NativeMethods.SetForegroundWindow(_returnTo);await Task.Delay(80);NativeMethods.Paste();}
    private void Favorite(){if(_grid.SelectedItem is ClipboardViewRow row){_db.SetClipboardFavorite(row.Entry.Id,!row.Entry.IsFavorite);Refresh();}}
    private void Delete(){if(_grid.SelectedItem is ClipboardViewRow row){_db.DeleteClipboard(row.Entry.Id);Refresh();}}
}

internal sealed class ClipboardViewRow
{
    public ClipboardEntry Entry{get;}public string Favorite=>Entry.IsFavorite?"★":"";public string Preview=>Entry.Preview;public string Kind=>Entry.Kind;public string Time=>Entry.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");public BitmapSource? Thumbnail{get;}
    public ClipboardViewRow(ClipboardEntry entry){Entry=entry;if(entry.ImagePng is not null)try{using var ms=new MemoryStream(entry.ImagePng);var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelWidth=96;b.StreamSource=ms;b.EndInit();b.Freeze();Thumbnail=b;}catch{}}
}

/// <summary>
/// 右下角的悬浮窗，承载两块可以各自独立显隐的内容：上面是待办清单，下面是性能悬浮条。
/// 做成一个窗口而不是两个互相跟随的窗口，是为了避开同步移动、z-order 和多显示器 DPI 那一堆麻烦。
///
/// 位置约定：设置里存的锚点是<b>最下面那一块的左上角</b>。待办块展开时窗口向上生长，
/// 下面的流量条必须原地不动，见 <see cref="FloatingDockLayout"/>。
/// </summary>
internal sealed class FloatingDockWindow : Window
{
    // 折叠宽度比原来的 170 多出 18px，正好补上折叠箭头占掉的那一列；
    // 否则速率会被截成「25.4 KB/」这种样子。两列指标的可用宽度和加箭头之前一致。
    private const double CollapsedWidth=188,ExpandedWidth=262,BlockGap=4;

    private readonly TextBlock _up=new(),_down=new(),_cpu=new(),_ram=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly SystemMetrics _metrics=new();
    private readonly GtoolsSettings _settings;private readonly SettingsStore _store;private readonly LocalDatabase _db;
    private readonly StackPanel _root=new();
    private readonly Border _monitorBlock,_todoBlock;
    private readonly StackPanel _todoRows=new();
    private readonly ScrollViewer _todoScroll;
    private readonly TextBox _todoInput=new();
    private readonly TextBlock _todoCount=Ui.Text("",11);
    private readonly Button _chevron;
    private readonly DockTheme _theme;
    private readonly DesktopLayerHost _desktopLayer;
    private bool _restoring,_monitorVisible,_todoEnabled;
    private bool _dragging;
    private System.Drawing.Point _dragCursor;
    private double _dragWindowLeft,_dragWindowTop;

    public FloatingDockWindow(GtoolsSettings settings,SettingsStore store,LocalDatabase db)
    {
        _settings=settings;_store=store;_db=db;_theme=DockTheme.For(settings.MonitorTheme);
        Title="Gtools 悬浮条";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.Height;
        ShowInTaskbar=false;Topmost=false;ShowActivated=false;AllowsTransparency=true;Background=Brushes.Transparent;
        WindowStartupLocation=WindowStartupLocation.Manual;
        Width=CollapsedWidth;

        _desktopLayer=new DesktopLayerHost(this);
        _desktopLayer.PlacementInvalidated+=(_,_)=>Reposition();

        _todoScroll=new ScrollViewer{Content=_todoRows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        _todoBlock=BuildTodoBlock();
        (_monitorBlock,_chevron)=BuildMonitorBlock();
        _root.Children.Add(_todoBlock);_root.Children.Add(_monitorBlock);
        Content=_root;

        _todoBlock.Visibility=Visibility.Collapsed;_monitorBlock.Visibility=Visibility.Collapsed;

        SourceInitialized+=(_,_)=>_desktopLayer.Initialize();
        Loaded+=(_,_)=>{Reposition();Dispatcher.BeginInvoke(_desktopLayer.ReassertDesktopLayer);};
        Activated+=(_,_)=>Dispatcher.BeginInvoke(_desktopLayer.ReassertDesktopLayer);
        IsVisibleChanged+=(_,_)=>{if(IsVisible&&IsLoaded){_desktopLayer.EnsureAttached();Reposition();Dispatcher.BeginInvoke(_desktopLayer.ReassertDesktopLayer);}};
        SizeChanged+=(_,_)=>{if(IsLoaded)Reposition();};
        LocationChanged+=(_,_)=>{if(IsLoaded&&!_restoring&&!_desktopLayer.IsAttached)SaveAnchor();};
        _timer.Tick+=(_,_)=>
        {
            var s=_metrics.Sample();
            _up.Text=SystemSnapshot.Rate(s.UpBytes);_down.Text=SystemSnapshot.Rate(s.DownBytes);
            _cpu.Text=$"{s.Cpu:0} %";_ram.Text=$"{s.Memory:0} %";
        };
        Closed+=async(_,_)=>{_desktopLayer.Dispose();await _store.SaveAsync();};
    }

    // ---------- 模块开关 ----------

    public void ShowMonitor(bool visible)
    {
        _monitorVisible=visible;
        _monitorBlock.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        if(visible)_timer.Start();else _timer.Stop();
        Sync();
    }

    public void ShowTodo(bool enabled)
    {
        _todoEnabled=enabled;
        if(enabled)RefreshTodo();
        Sync();
    }

    private bool TodoVisible=>_todoEnabled&&_settings.TodoExpanded;

    private void Sync()
    {
        _todoBlock.Visibility=TodoVisible?Visibility.Visible:Visibility.Collapsed;
        _todoBlock.Margin=TodoVisible?new Thickness(0,0,0,BlockGap):new Thickness(0);
        _chevron.Visibility=_todoEnabled&&_monitorVisible?Visibility.Visible:Visibility.Collapsed;
        UpdateChevron();
        Width=TodoVisible?ExpandedWidth:CollapsedWidth;
        if(_monitorVisible||TodoVisible)
        {
            if(!IsVisible)Show();
            Reposition();
        }
        else if(IsVisible)Hide();
    }

    /// <summary>折叠状态下把未完成条数显示在箭头上，正好服务“容易忘”这个诉求。</summary>
    private void UpdateChevron()
    {
        if(TodoVisible){_chevron.Content="▾";return;}
        var pending=_todoEnabled?_db.ListTodo().Count(x=>!x.IsDone):0;
        _chevron.Content=pending>0?$"▴{pending}":"▴";
    }

    private void ToggleTodo()
    {
        _settings.TodoExpanded=!_settings.TodoExpanded;
        if(_settings.TodoExpanded)RefreshTodo();
        Sync();
        _=_store.SaveAsync();
    }

    // ---------- 位置 ----------

    private double AboveHeight=>TodoVisible?_todoBlock.ActualHeight+BlockGap:0;

    // 赋值 Left 会同步触发 LocationChanged，如果那里回写设置，就会在读 Top 之前把存好的值冲掉，
    // 表现为纵向位置永远记不住。所以恢复期间必须先关掉回写。
    private void Reposition()
    {
        if(!IsLoaded)return;
        _desktopLayer.EnsureAttached();
        _restoring=true;
        try
        {
            var work=SystemParameters.WorkArea;
            var (left,top)=FloatingDockLayout.Resolve(_settings.MonitorLeft,_settings.MonitorTop,AboveHeight,
                ActualWidth>0?ActualWidth:Width,ActualHeight>0?ActualHeight:Height,
                new DesktopRect((int)work.Left,(int)work.Top,(int)work.Right,(int)work.Bottom));
            _desktopLayer.MoveToScreenDip(left,top);
        }
        finally{_restoring=false;}
        SaveAnchor();
    }

    private void SaveAnchor()
    {
        var position=_desktopLayer.GetScreenDipPosition();
        var (left,top)=FloatingDockLayout.AnchorOf(position.Left,position.Top,AboveHeight);
        _settings.MonitorLeft=left;_settings.MonitorTop=top;
    }

    /// <summary>只有拖拽区能拖窗口。挂在整个窗口上会把复选框和输入框的点击全抢走。</summary>
    private void EnableDrag(UIElement handle)
    {
        handle.MouseLeftButtonDown+=(_,e)=>
        {
            if(_settings.MonitorLocked||e.ButtonState!=MouseButtonState.Pressed)return;
            if(e.OriginalSource is Control)return; // 按钮、复选框、输入框上的点击不算拖拽
            if(!NativeMethods.GetCursorPos(out _dragCursor))return;
            var position=_desktopLayer.GetScreenDipPosition();
            _dragWindowLeft=position.Left;_dragWindowTop=position.Top;_dragging=true;
            handle.CaptureMouse();e.Handled=true;
        };
        handle.MouseMove+=(_,e)=>
        {
            if(!_dragging||e.LeftButton!=MouseButtonState.Pressed)return;
            if(!NativeMethods.GetCursorPos(out var cursor))return;
            var delta=_desktopLayer.PixelDeltaToDip(cursor.X-_dragCursor.X,cursor.Y-_dragCursor.Y);
            _desktopLayer.MoveToScreenDip(_dragWindowLeft+delta.X,_dragWindowTop+delta.Y);
            e.Handled=true;
        };
        handle.MouseLeftButtonUp+=(_,e)=>FinishDrag(handle,e);
        handle.LostMouseCapture+=(_,_)=>{if(_dragging){_dragging=false;SaveAnchor();Reposition();}};
    }

    private void FinishDrag(UIElement handle,MouseButtonEventArgs e)
    {
        if(!_dragging)return;
        _dragging=false;handle.ReleaseMouseCapture();SaveAnchor();Reposition();_=_store.SaveAsync();e.Handled=true;
    }

    // ---------- 待办块 ----------

    private Border BuildTodoBlock()
    {
        var panel=new StackPanel{Margin=new Thickness(7,5,7,6)};

        var header=new Grid();
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=Ui.Text("待办",11.5,FontWeights.SemiBold);title.Foreground=_theme.Foreground;title.Margin=new Thickness(1,0,4,0);
        Grid.SetColumn(title,0);header.Children.Add(title);
        _todoCount.Foreground=_theme.Muted;_todoCount.Margin=new Thickness(0,0,4,0);_todoCount.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(_todoCount,1);header.Children.Add(_todoCount);
        var clear=IconButton("清除已完成","⌫",(_,_)=>{_db.ClearCompletedTodo();RefreshTodo();});
        Grid.SetColumn(clear,2);header.Children.Add(clear);
        panel.Children.Add(header);
        EnableDrag(header);

        _todoInput.FontSize=11.5;_todoInput.Margin=new Thickness(1,4,1,4);_todoInput.Padding=new Thickness(4,2,4,2);
        _todoInput.Background=_theme.InputBackground;_todoInput.Foreground=_theme.Foreground;
        _todoInput.BorderBrush=_theme.Border;_todoInput.BorderThickness=new Thickness(1);
        _todoInput.ToolTip="输入后按回车添加";
        _todoInput.KeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter){AddTodo();e.Handled=true;}
            else if(e.Key==Key.Escape){_todoInput.Clear();e.Handled=true;}
        };
        // ShowActivated=false 的置顶窗口不会自动接管键盘焦点，点输入框时要显式激活。
        _todoInput.PreviewMouseLeftButtonDown+=(_,_)=>{Activate();_todoInput.Focus();};
        panel.Children.Add(_todoInput);

        panel.Children.Add(_todoScroll);
        return new Border
        {
            Background=_theme.Background,BorderBrush=_theme.Border,BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(3),Child=panel
        };
    }

    private void AddTodo()
    {
        var text=_todoInput.Text.Trim();
        if(text.Length==0)return;
        _db.AddTodo(text);_todoInput.Clear();RefreshTodo();
    }

    private void RefreshTodo()
    {
        var items=_db.ListTodo();
        _todoRows.Children.Clear();
        foreach(var item in items)_todoRows.Children.Add(TodoRow(item,items));
        var pending=items.Count(x=>!x.IsDone);
        _todoCount.Text=items.Count==0?"":pending>0?$"{pending} 项未完成":"全部完成";
        // 超过设定行数就滚动，避免浮窗长到顶天。
        _todoScroll.MaxHeight=Math.Max(1,Math.Clamp(_settings.TodoVisibleRows,1,20))*24;
        UpdateChevron();
    }

    private UIElement TodoRow(TodoItem item,IReadOnlyList<TodoItem> all)
    {
        var row=new Grid{Margin=new Thickness(0,1,0,1)};
        row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});

        var check=new CheckBox{IsChecked=item.IsDone,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(1,0,4,0)};
        // 用 Checked/Unchecked 而不是 Click：勾选状态还可能被鼠标以外的途径改变（键盘、辅助功能/UI 自动化），
        // 只挂 Click 的话界面打了勾但数据库没写，重启就丢。
        void Toggled(object? _,RoutedEventArgs __){_db.SetTodoDone(item.Id,check.IsChecked==true);RefreshTodo();}
        check.Checked+=Toggled;check.Unchecked+=Toggled;
        Grid.SetColumn(check,0);row.Children.Add(check);

        var text=new TextBlock
        {
            Text=item.Text,FontSize=11.5,VerticalAlignment=VerticalAlignment.Center,
            TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=item.Text,
            Foreground=item.IsDone?_theme.Muted:_theme.Foreground,
            TextDecorations=item.IsDone?TextDecorations.Strikethrough:null
        };
        Grid.SetColumn(text,1);row.Children.Add(text);

        var index=all.ToList().IndexOf(item);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};
        buttons.Children.Add(IconButton("上移","▲",(_,_)=>Move(all,index,-1),index>0));
        buttons.Children.Add(IconButton("下移","▼",(_,_)=>Move(all,index,1),index<all.Count-1));
        buttons.Children.Add(IconButton("删除","✕",(_,_)=>{_db.DeleteTodo(item.Id);RefreshTodo();}));
        Grid.SetColumn(buttons,2);row.Children.Add(buttons);
        return row;
    }

    private void Move(IReadOnlyList<TodoItem> all,int index,int delta)
    {
        var target=index+delta;
        if(index<0||target<0||target>=all.Count)return;
        var ids=all.Select(x=>x.Id).ToList();
        (ids[index],ids[target])=(ids[target],ids[index]);
        _db.ReorderTodo(ids);RefreshTodo();
    }

    private Button IconButton(string tip,string glyph,RoutedEventHandler click,bool enabled=true)
    {
        var button=new Button
        {
            Content=glyph,ToolTip=tip,FontSize=10,Width=17,Height=17,Padding=new Thickness(0),
            Margin=new Thickness(1,0,0,0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),
            Foreground=_theme.Muted,Cursor=Cursors.Hand,VerticalAlignment=VerticalAlignment.Center
        };
        // 不用 IsEnabled=false：默认模板的禁用态会画一个灰色方块，在浮窗里很脏。
        if(!enabled){button.Opacity=.25;button.IsHitTestVisible=false;button.Cursor=Cursors.Arrow;}
        button.Click+=click;return button;
    }

    // ---------- 流量条（外观保持不变） ----------

    private (Border Block,Button Chevron) BuildMonitorBlock()
    {
        var grid=new Grid{Margin=new Thickness(7,4,7,4)};
        grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new());
        Cell(grid,0,0,Triangle(_theme.Up,true),_up,_theme.Foreground);
        Cell(grid,0,1,Triangle(_theme.Down,false),_down,_theme.Foreground);
        Cell(grid,1,0,Chip(_theme.Cpu),_cpu,_theme.Foreground);
        Cell(grid,1,1,Memory(_theme.Ram),_ram,_theme.Foreground);

        var chevron=new Button
        {
            Content="▴",ToolTip="展开 / 折叠待办",FontSize=10,Width=18,Padding=new Thickness(0),
            Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=_theme.Muted,
            Cursor=Cursors.Hand,VerticalAlignment=VerticalAlignment.Center,Visibility=Visibility.Collapsed
        };
        chevron.Click+=(_,_)=>ToggleTodo();
        Grid.SetColumn(chevron,2);Grid.SetRowSpan(chevron,2);grid.Children.Add(chevron);

        var block=new Border
        {
            Background=_theme.Background,BorderBrush=_theme.Border,BorderThickness=new Thickness(1),
            // 窗口变宽时这一块会因为布局取整少 1px，导致展开/折叠时流量条上下抖一个像素。钉死最小高度即可。
            CornerRadius=new CornerRadius(3),Child=grid,MinHeight=44
        };
        EnableDrag(block);
        return (block,chevron);
    }

    private static void Cell(Grid grid,int row,int column,UIElement icon,TextBlock value,Brush foreground)
    {
        value.Text="—";value.FontSize=11.5;value.Margin=new Thickness(4,0,0,0);
        value.Foreground=foreground;value.VerticalAlignment=VerticalAlignment.Center;
        var panel=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(1,1,1,1),VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(icon);panel.Children.Add(value);
        Grid.SetRow(panel,row);Grid.SetColumn(panel,column);grid.Children.Add(panel);
    }

    private static UIElement Triangle(Brush brush,bool pointingUp)=>new System.Windows.Shapes.Polygon
    {
        Points=pointingUp?new PointCollection{new(5,0),new(10,9),new(0,9)}:new PointCollection{new(0,0),new(10,0),new(5,9)},
        Fill=brush,VerticalAlignment=VerticalAlignment.Center
    };

    private static UIElement Chip(Brush brush)
    {
        var outer=new System.Windows.Shapes.Rectangle{Width=11,Height=11,RadiusX=2,RadiusY=2,Stroke=brush,StrokeThickness=1.4};
        var inner=new System.Windows.Shapes.Rectangle{Width=4,Height=4,Fill=brush,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        return new Grid{Width=11,Height=11,VerticalAlignment=VerticalAlignment.Center,Children={outer,inner}};
    }

    private static UIElement Memory(Brush brush)
    {
        var body=new System.Windows.Shapes.Rectangle{Width=12,Height=9,RadiusX=1,RadiusY=1,Fill=brush};
        var bars=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        for(var i=0;i<3;i++)bars.Children.Add(new System.Windows.Shapes.Rectangle{Width=1.6,Height=5,Fill=Brushes.White,Margin=new Thickness(1,0,1,0)});
        return new Grid{Width=12,Height=9,VerticalAlignment=VerticalAlignment.Center,Children={body,bars}};
    }
}

internal sealed record DockTheme(Brush Background,Brush Border,Brush Foreground,Brush Muted,Brush InputBackground,
    Brush Up,Brush Down,Brush Cpu,Brush Ram)
{
    public static DockTheme For(MonitorTheme theme)
    {
        var dark=theme==MonitorTheme.Dark;
        Brush B(Color c){var brush=new SolidColorBrush(c);brush.Freeze();return brush;}
        return new(
            B(dark?Color.FromArgb(232,28,28,32):Color.FromRgb(232,242,252)),
            B(dark?Color.FromArgb(232,70,70,78):Color.FromRgb(124,169,206)),
            B(dark?Colors.White:Color.FromRgb(31,95,160)),
            B(dark?Color.FromRgb(150,150,158):Color.FromRgb(112,146,180)),
            B(dark?Color.FromArgb(255,44,44,50):Color.FromRgb(248,252,255)),
            B(Color.FromRgb(240,138,40)),
            B(Color.FromRgb(63,165,68)),
            B(dark?Color.FromRgb(120,200,120):Color.FromRgb(63,165,68)),
            B(dark?Color.FromRgb(110,190,170):Color.FromRgb(53,160,128)));
    }
}

internal sealed class ToastWindow : Window
{
    public ToastWindow(string text,Action? click=null)
    {
        Width=320;Height=64;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;Topmost=true;ShowActivated=false;
        Left=SystemParameters.WorkArea.Right-Width-18;Top=SystemParameters.WorkArea.Bottom-Height-18;
        var b=new Border{Background=new SolidColorBrush(Color.FromArgb(240,35,35,38)),CornerRadius=new CornerRadius(9),Padding=new Thickness(12)};b.Child=new TextBlock{Text=text,Foreground=Brushes.White,TextWrapping=TextWrapping.Wrap};Content=b;
        if(click is not null)MouseLeftButtonUp+=(_,_)=>{click();Close();};Loaded+=async(_,_)=>{await Task.Delay(2600);if(IsVisible)Close();};
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly GtoolsSettings _s;private readonly SettingsStore _store;private readonly Dictionary<string,CheckBox> _checks=new();
    private readonly TextBox _base=new(),_model=new(),_key=new(),_prompt=new(){AcceptsReturn=true,Height=64};
    private readonly TextBox _ocrKey=new(),_translateKey=new(),_longKey=new(),_clipKey=new(),_clipLimit=new();
    private readonly TextBox _longFrames=new();
    private readonly TextBox _todoRows=new();
    public event EventHandler? SettingsChanged;
    public SettingsWindow(GtoolsSettings settings,SettingsStore store)
    {
        _s=settings;_store=store;Title="Gtools · 设置";Width=620;Height=680;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto};var p=new StackPanel{Margin=new Thickness(16)};scroll.Content=p;
        p.Children.Add(Ui.Text("模块",20,FontWeights.SemiBold));
        AddCheck(p,"开机启动",nameof(_s.StartWithWindows),_s.StartWithWindows);AddCheck(p,"OCR",nameof(_s.OcrEnabled),_s.OcrEnabled);AddCheck(p,"截图翻译",nameof(_s.TranslationEnabled),_s.TranslationEnabled);AddCheck(p,"聊天长截图",nameof(_s.LongShotEnabled),_s.LongShotEnabled);AddCheck(p,"历史剪贴板",nameof(_s.ClipboardEnabled),_s.ClipboardEnabled);AddCheck(p,"性能悬浮条",nameof(_s.MonitorEnabled),_s.MonitorEnabled);AddCheck(p,"锁定性能悬浮条位置",nameof(_s.MonitorLocked),_s.MonitorLocked);
        p.Children.Add(Ui.Text("快捷键",20,FontWeights.SemiBold));var keys=new Grid();for(var i=0;i<4;i++)keys.ColumnDefinitions.Add(new());AddKey(keys,0,"OCR",_ocrKey,_s.OcrHotkey);AddKey(keys,1,"翻译",_translateKey,_s.TranslateHotkey);AddKey(keys,2,"长截图",_longKey,_s.LongShotHotkey);AddKey(keys,3,"剪贴板",_clipKey,_s.ClipboardHotkey);p.Children.Add(keys);p.Children.Add(Ui.Text("快捷键修改后重启 Gtools 生效；` 键写作 Oem3。",12));AddField(p,"剪贴板保留条数（收藏不计入清理）",_clipLimit,_s.ClipboardLimit.ToString(CultureInfo.InvariantCulture));
        p.Children.Add(Ui.Text("长截图适配",20,FontWeights.SemiBold));var longProfiles=new ComboBox{ItemsSource=Enum.GetValues<LongShotCaptureProfile>(),SelectedItem=_s.LongShotProfile,Margin=new Thickness(3)};longProfiles.SelectionChanged+=(_,_)=>_s.LongShotProfile=(LongShotCaptureProfile)(longProfiles.SelectedItem??LongShotCaptureProfile.WeChat4);p.Children.Add(longProfiles);
        p.Children.Add(Ui.Text("每次采集默认使用手动模式；需要时可在进度窗切换自动滚动。Generic 适用于网页、文档等非聊天软件。",12));
        AddCheck(p,"向上滚动采集（聊天记录）；取消则向下滚动（网页、文档）",nameof(_s.LongShotScrollUp),_s.LongShotScrollUp);
        AddField(p,"单张长图最多采集帧数",_longFrames,_s.LongShotMaxFrames.ToString(CultureInfo.InvariantCulture));
        p.Children.Add(Ui.Text("翻译 Provider",20,FontWeights.SemiBold));
        var providers=new ComboBox{ItemsSource=new[]{"google-web","youdao-web","openai-compatible"},SelectedItem=_s.TranslationProvider,Margin=new Thickness(3)};providers.SelectionChanged+=(_,_)=>_s.TranslationProvider=providers.SelectedItem?.ToString()??"google-web";p.Children.Add(providers);
        p.Children.Add(Ui.Text("Google 与有道都是免密钥的非官方网页通道，可能随时失效；只会发送 OCR 后的文字，不会上传截图。翻译窗口里的“多引擎对照”会同时请求所有可用引擎。",12));
        AddCheck(p,"加入生词本时自动查词（有道词典 + dictionaryapi.dev）",nameof(_s.DictionaryLookupEnabled),_s.DictionaryLookupEnabled);
        p.Children.Add(Ui.Text("性能悬浮条",20,FontWeights.SemiBold));
        var monitorThemes=new ComboBox{ItemsSource=Enum.GetValues<MonitorTheme>(),SelectedItem=_s.MonitorTheme,Margin=new Thickness(3)};
        monitorThemes.SelectionChanged+=(_,_)=>_s.MonitorTheme=(MonitorTheme)(monitorThemes.SelectedItem??MonitorTheme.Light);
        p.Children.Add(monitorThemes);
        p.Children.Add(Ui.Text("Light 是浅蓝双行样式，Dark 是深色样式。切换后需要重启 Gtools 才会重建悬浮窗。",12));
        AddCheck(p,"待办清单（拼在流量条上方）",nameof(_s.TodoEnabled),_s.TodoEnabled);
        AddCheck(p,"启动时展开待办；折叠后只显示流量条",nameof(_s.TodoExpanded),_s.TodoExpanded);
        AddField(p,"待办最多显示几行（超出后在块内滚动）",_todoRows,_s.TodoVisibleRows.ToString(CultureInfo.InvariantCulture));
        p.Children.Add(Ui.Text("流量条右侧的小箭头可以随时折叠/展开待办；折叠且还有未完成项时箭头上会显示条数。托盘菜单里也有开关。",12));
        AddField(p,"OpenAI 兼容地址",_base,_s.OpenAiBaseUrl);AddField(p,"模型",_model,_s.OpenAiModel);AddField(p,"API Key（留空表示不修改）",_key,"");AddField(p,"系统提示词",_prompt,_s.OpenAiPrompt);
        var save=Ui.Button("保存并应用",async(_,_)=>{Apply();await _store.SaveAsync();SettingsChanged?.Invoke(this,EventArgs.Empty);DialogResult=true;});p.Children.Add(save);Content=scroll;
    }
    private void AddCheck(Panel p,string label,string key,bool value){var c=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(5)};_checks[key]=c;p.Children.Add(c);}
    private static void AddKey(Grid grid,int column,string label,TextBox box,string value){var p=new StackPanel();p.Children.Add(Ui.Text(label,12));box.Text=value;p.Children.Add(box);Grid.SetColumn(p,column);grid.Children.Add(p);}
    private static void AddField(Panel p,string label,TextBox box,string value){p.Children.Add(Ui.Text(label,12));box.Text=value;p.Children.Add(box);}
    private void Apply()
    {
        _s.StartWithWindows=Get(nameof(_s.StartWithWindows));_s.OcrEnabled=Get(nameof(_s.OcrEnabled));_s.TranslationEnabled=Get(nameof(_s.TranslationEnabled));_s.LongShotEnabled=Get(nameof(_s.LongShotEnabled));_s.ClipboardEnabled=Get(nameof(_s.ClipboardEnabled));_s.MonitorEnabled=Get(nameof(_s.MonitorEnabled));_s.MonitorLocked=Get(nameof(_s.MonitorLocked));
        _s.OcrHotkey=_ocrKey.Text.Trim();_s.TranslateHotkey=_translateKey.Text.Trim();_s.LongShotHotkey=_longKey.Text.Trim();_s.ClipboardHotkey=_clipKey.Text.Trim();if(int.TryParse(_clipLimit.Text,out var limit))_s.ClipboardLimit=Math.Clamp(limit,50,10000);
        _s.LongShotScrollUp=Get(nameof(_s.LongShotScrollUp));if(int.TryParse(_longFrames.Text,out var maxFrames))_s.LongShotMaxFrames=Math.Clamp(maxFrames,2,400);
        _s.DictionaryLookupEnabled=Get(nameof(_s.DictionaryLookupEnabled));
        _s.TodoEnabled=Get(nameof(_s.TodoEnabled));_s.TodoExpanded=Get(nameof(_s.TodoExpanded));
        if(int.TryParse(_todoRows.Text,out var rows))_s.TodoVisibleRows=Math.Clamp(rows,1,20);
        _s.OpenAiBaseUrl=_base.Text.Trim();_s.OpenAiModel=_model.Text.Trim();_s.OpenAiPrompt=_prompt.Text;if(!string.IsNullOrWhiteSpace(_key.Text))_s.OpenAiKeyProtected=SecretProtector.Protect(_key.Text.Trim());
    }
    private bool Get(string key)=>_checks[key].IsChecked==true;
}

internal sealed class LongShotProgressWindow : Window
{
    private readonly TextBlock _label=Ui.Text("准备采集…",14);private readonly TextBlock _status=Ui.Text("",12);
    private readonly CancellationTokenSource _cts=new();private readonly LongShotCaptureModeState _modeState=new();
    private readonly Button _modeButton;private readonly Button _nextButton;private readonly Button _captureAndFinishButton;
    private readonly Button _finishButton;private readonly Button _cancelButton;
    private int _frameCount;private bool _allowClose;
    public CancellationToken Token=>_cts.Token;
    public LongShotCaptureMode Mode=>_modeState.Mode;
    public bool WasCancelled{get;private set;}
    public LongShotProgressWindow()
    {
        Title="Gtools · 长截图采集";Width=560;Height=215;Topmost=true;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.Manual;
        Left=SystemParameters.WorkArea.Right-Width-12;Top=SystemParameters.WorkArea.Top+12;
        var p=new StackPanel{Margin=new Thickness(12)};
        p.Children.Add(_label);p.Children.Add(_status);
        p.Children.Add(Ui.Text("默认手动：滚动聊天后逐帧采集。需要时可切换自动滚动，自动失败会回到手动。",12));
        var bar=new WrapPanel();
        _modeButton=Ui.Button("切换自动",(_,_)=>ToggleMode());bar.Children.Add(_modeButton);
        _nextButton=Ui.Button("采集下一帧",(_,_)=>RequestManualCapture());_nextButton.IsEnabled=false;bar.Children.Add(_nextButton);
        _captureAndFinishButton=Ui.Button("采集并完成 (Enter)",(_,_)=>RequestCaptureAndFinish());bar.Children.Add(_captureAndFinishButton);
        _finishButton=Ui.Button("直接完成",(_,_)=>RequestFinishWithoutCapture());bar.Children.Add(_finishButton);
        _cancelButton=Ui.Button("取消 (Esc)",(_,_)=>Cancel());bar.Children.Add(_cancelButton);
        p.Children.Add(bar);Content=p;
        KeyDown+=(_,e)=>{if(e.Key==Key.Enter)RequestCaptureAndFinish();else if(e.Key==Key.Escape)Cancel();};
        Closing+=(_,_)=>{if(!_allowClose)Cancel();};
    }
    public void SetFrame(int n){_frameCount=n;UpdateModeLabel();}
    public void SetStatus(string text)=>_status.Text=text;
    public void SetMode(LongShotCaptureMode mode,string message)
    {
        _modeState.SetMode(mode);_modeButton.Content=mode==LongShotCaptureMode.Manual?"切换自动":"切换手动";UpdateModeLabel();
        _nextButton.IsEnabled=false;_status.Text=message;
    }
    public async Task<LongShotCaptureAction?> WaitForNextCaptureAsync()
    {
        _nextButton.IsEnabled=Mode==LongShotCaptureMode.Manual;
        try{return await _modeState.WaitForNextActionAsync(_cts.Token);}
        catch(OperationCanceledException){return null;}
        finally{_nextButton.IsEnabled=false;}
    }
    private void ToggleMode()
    {
        if(Mode==LongShotCaptureMode.Manual)SetMode(LongShotCaptureMode.Automatic,"已切换自动滚动；下一帧将由 Gtools 自动滚动并采集。");
        else SetMode(LongShotCaptureMode.Manual,"已切换手动：请滚动聊天，再点“采集下一帧”。");
    }
    private void RequestManualCapture()
    {
        if(_modeState.RequestManualCapture())_status.Text="正在采集当前画面…";
    }
    private void RequestCaptureAndFinish()
    {
        if(_modeState.RequestCaptureAndFinish())_status.Text="将采集当前画面并完成…";
    }
    private void RequestFinishWithoutCapture()
    {
        if(_modeState.RequestFinishWithoutCapture())_status.Text="将使用已经采集的画面直接完成…";
    }
    private void UpdateModeLabel()=>_label.Text=_frameCount>0
        ?$"已采集 {_frameCount} 帧 · {(Mode==LongShotCaptureMode.Manual?"手动":"自动")}模式"
        :$"准备采集 · {(Mode==LongShotCaptureMode.Manual?"手动":"自动")}模式";
    private void Cancel(){if(_cts.IsCancellationRequested)return;WasCancelled=true;_cts.Cancel();}
    public void SetProcessing(string message)
    {
        _modeButton.IsEnabled=false;_nextButton.IsEnabled=false;_captureAndFinishButton.IsEnabled=false;_finishButton.IsEnabled=false;
        _status.Text=message;_label.Text=$"已采集 {_frameCount} 帧 · 正在生成长图";
    }
    public bool ConfirmUnreliableCapture(double confidence)=>MessageBox.Show(this,
        $"这一屏无法可靠测量滚动距离（置信度 {confidence:0.00}）。\n\n仍然保存可能产生错位接缝，是否继续？",
        "Gtools · 接缝需要确认",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes;
    public void NotifySafetyLimit(string message)=>MessageBox.Show(this,message,"Gtools · 长截图已截断",MessageBoxButton.OK,MessageBoxImage.Information);
    public void CloseAfterCompletion(){if(!IsLoaded)return;_allowClose=true;Close();}

    /// <summary>把进度窗挪到不和采集区重叠的角落。挪得开就不必每帧 Hide/Show，采集期间不再闪烁。</summary>
    public bool TryPlaceOutside(System.Drawing.Rectangle capture)
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        var work=System.Windows.Forms.Screen.FromRectangle(capture).WorkingArea;
        var w=Width*dpi.DpiScaleX;var h=Height*dpi.DpiScaleY;
        var left=(double)work.Left;var top=(double)work.Top;var right=(double)work.Right;var bottom=(double)work.Bottom;
        var candidates=new[]
        {
            new System.Windows.Point(right-w-8,top+8), new System.Windows.Point(left+8,top+8),
            new System.Windows.Point(right-w-8,bottom-h-8), new System.Windows.Point(left+8,bottom-h-8)
        };
        foreach(var c in candidates)
        {
            var box=new System.Drawing.Rectangle((int)c.X,(int)c.Y,(int)w,(int)h);
            if(box.IntersectsWith(capture))continue;
            Left=c.X/dpi.DpiScaleX;Top=c.Y/dpi.DpiScaleY;return true;
        }
        return false;
    }
}

internal sealed class LongShotPreviewWindow : Window
{
    private readonly CancellationTokenSource _lifetime=new();
    public LongShotPreviewWindow(LongShotResponse r,LongShotWorkerClient worker)
    {
        Title="Gtools · 长截图预览";Width=780;Height=700;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Closed+=(_,_)=>_lifetime.Cancel();
        var root=new DockPanel{Margin=new Thickness(8)};var top=new WrapPanel();
        var notice=Ui.Text(r.Warnings.Length==0?"拼接完成，接缝置信度正常。":"⚠ "+string.Join("；",r.Warnings),13,r.Warnings.Length==0?FontWeights.Normal:FontWeights.SemiBold);
        if(r.Warnings.Length>0)notice.Foreground=Brushes.DarkOrange;top.Children.Add(notice);
        if(r.OutputPath is not null)
        {
            top.Children.Add(Ui.Button("打开文件夹",(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{r.OutputPath}\""){UseShellExecute=true})));
            var cutTop=new TextBox{Width=65,Text="0",ToolTip="顶部裁剪像素"};var cutBottom=new TextBox{Width=65,Text="0",ToolTip="底部裁剪像素"};top.Children.Add(Ui.Text("裁首/尾 px",12));top.Children.Add(cutTop);top.Children.Add(cutBottom);
            var cropButton=Ui.Button("另存裁剪版",async(sender,_)=>
            {
                if(!int.TryParse(cutTop.Text,out var a)||!int.TryParse(cutBottom.Text,out var b))return;
                var button=(Button)sender;button.IsEnabled=false;
                try{await SaveCroppedAsync(r,worker,a,b);}
                catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){return;}
                catch(Exception ex){MessageBox.Show(this,"裁剪失败："+ex.Message,"Gtools",MessageBoxButton.OK,MessageBoxImage.Error);}
                finally{button.IsEnabled=true;}
            });
            top.Children.Add(cropButton);
        }
        DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        if(r.OutputPath is not null&&File.Exists(r.OutputPath)){var image=new Image{Stretch=Stretch.Uniform,Source=LoadPreview(r.OutputPath),HorizontalAlignment=HorizontalAlignment.Center};root.Children.Add(new ScrollViewer{Content=image,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});}
        Content=root;
    }
    internal static BitmapSource LoadPreview(string path,int decodePixelWidth=1200)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=decodePixelWidth;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
    }
    private async Task SaveCroppedAsync(LongShotResponse result,LongShotWorkerClient worker,int top,int bottom)
    {
        if(result.OutputPath is null||result.Diagnostics is null)throw new InvalidOperationException("缺少长图尺寸信息，无法安全裁剪。");
        var width=result.Diagnostics.Width;var originalHeight=result.Diagnostics.Height;
        top=Math.Clamp(top,0,originalHeight-1);bottom=Math.Clamp(bottom,0,originalHeight-top-1);
        var dialog=new SaveFileDialog{Filter="PNG 图片|*.png",FileName=Path.GetFileNameWithoutExtension(result.OutputPath)+"-cropped.png"};if(dialog.ShowDialog(this)!=true)return;
        var request=new LongShotCropRequest(Guid.NewGuid().ToString("N"),result.OutputPath,dialog.FileName,0,top,width,originalHeight-top-bottom);
        var response=await worker.CropAsync(request,_lifetime.Token);
        if(!response.Success)throw new InvalidOperationException(response.Error??"裁剪 Worker 返回失败。");
        MessageBox.Show(this,"裁剪版已保存。","Gtools",MessageBoxButton.OK,MessageBoxImage.Information);
    }
}
