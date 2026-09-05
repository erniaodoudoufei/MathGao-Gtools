using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gtools.Core;

namespace Gtools.App;

internal sealed class LongShotProgressWindow : Window
{
    private readonly CancellationTokenSource _cts=new();
    private readonly LongShotCaptureModeState _state=new();
    private GlobalHotkeyService? _escape;
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,FontSize=14,Margin=new Thickness(0,8,0,8)};
    private readonly Button _capture=new(){Content="采集下一帧"},_undo=new(){Content="撤销上一帧"},_finish=new(){Content="完成并预览"};
    private bool _completed;
    public CancellationToken Token=>_cts.Token;
    public LongShotProgressWindow(bool scrollUp)
    {
        Title="Gtools · 微信长截图";Width=520;Height=240;Topmost=true;ShowInTaskbar=false;ShowActivated=false;
        var root=new StackPanel{Margin=new Thickness(14)};
        root.Children.Add(Ui.Text($"手动{(scrollUp?"向上":"向下")}滚动 · 逐帧确认",18,FontWeights.SemiBold));
        root.Children.Add(Ui.Text("每次保留至少半屏重叠。确认接缝后才能继续采集。",13));root.Children.Add(_status);
        var bar=new WrapPanel();foreach(var button in new[]{_capture,_undo,_finish}){button.Margin=new Thickness(3);button.Padding=new Thickness(8);bar.Children.Add(button);}
        bar.Children.Add(Ui.Button("取消 (Esc)",(_,_)=>Cancel()));root.Children.Add(bar);Content=root;
        _capture.Click+=(_,_)=>_state.Request(LongShotCaptureAction.Capture);
        _undo.Click+=(_,_)=>_state.Request(LongShotCaptureAction.Undo);
        _finish.Click+=(_,_)=>_state.Request(LongShotCaptureAction.Finish);
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)Cancel();};Closing+=(_,_)=>{if(!_completed)Cancel();};
        Loaded+=(_,_)=>{if(_escape is null){_escape=new();_escape.Register("Escape",Cancel,out _);}};
        Closed+=(_,_)=>_escape?.Dispose();
        SetStatus("正在准备第一帧…");
    }
    private void Cancel(){if(!_cts.IsCancellationRequested)_cts.Cancel();foreach(var child in OwnedWindows.Cast<Window>().ToArray())child.Close();}
    public void SetStatus(string message)=>_status.Text=message;
    public async Task<LongShotCaptureAction> WaitAsync(int frames,string? message=null)
    {
        SetStatus(message??$"已确认 {frames} 帧。请滚动聊天，再采集下一帧。");
        _capture.IsEnabled=true;_undo.IsEnabled=frames>1;_finish.IsEnabled=frames>0;
        try{return await _state.WaitAsync(Token);}
        finally{_capture.IsEnabled=false;_undo.IsEnabled=false;_finish.IsEnabled=false;}
    }
    public void Complete(){_completed=true;Close();}
    public void Place(System.Drawing.Rectangle capture)
    {
        var screen=System.Windows.Forms.Screen.FromRectangle(capture);var work=screen.WorkingArea;
        var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
        // Move with physical Win32 coordinates; WPF converts after the monitor DPI transition.
        LongShotTargetGuard.PlaceWindow(handle,work.Right-(int)(Width*VisualTreeHelper.GetDpi(this).DpiScaleX)-12,work.Top+12);
    }
}

internal sealed class LongShotFirstFrameWindow : Window
{
    public bool Reselect{get;private set;}
    public LongShotFirstFrameWindow(string path)
    {
        Title="Gtools · 确认第一帧与选区";Width=850;Height=650;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new DockPanel{Margin=new Thickness(12)};
        var heading=Ui.Text("检查选区：只保留消息，避开标题、输入框、滚动条和“新消息”浮层。\n若浮层压住了消息，请先关闭该提示再重新框选；不要裁掉需要保留的文字。",15);
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var bar=new WrapPanel{Margin=new Thickness(0,10,0,0)};
        bar.Children.Add(Ui.Button("确认第一帧",(_,_)=>DialogResult=true));
        bar.Children.Add(Ui.Button("重新框选",(_,_)=>{Reselect=true;DialogResult=false;}));
        bar.Children.Add(Ui.Button("取消任务",(_,_)=>DialogResult=false));DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
        root.Children.Add(new ScrollViewer{Content=new Image{Source=LongShotImage.Load(path),Stretch=Stretch.Uniform}});Content=root;
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)DialogResult=false;};
    }
}

internal static class LongShotImage
{
    private const int MaxPreviewPixels=4_000_000;

    internal sealed record Preview(BitmapSource Bitmap,int OriginalWidth,int OriginalHeight);

    public static BitmapSource Load(string path)=>LoadInfo(path).Bitmap;

    public static Preview LoadInfo(string path,int maxWidth=1600)
    {
        using var stream=File.OpenRead(path);
        Span<byte> header=stackalloc byte[24];stream.ReadExactly(header);stream.Position=0;
        if(!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("预览需要 PNG 文件。");
        var width=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        var height=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if(width<=0||height<=0)throw new InvalidDataException("图像尺寸无效。");
        var boundedWidth=Math.Max(1,(int)Math.Min(Math.Min(width,maxWidth),Math.Sqrt((double)MaxPreviewPixels*width/height)));
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=boundedWidth;image.StreamSource=stream;image.EndInit();image.Freeze();
        return new(image,width,height);
    }
}

internal sealed class LongShotSeamWindow : Window
{
    private readonly LongShotWorkerClient _worker;
    private readonly string _upper,_lower,_previewPath;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly TextBlock _message=Ui.Text("正在分析相邻两帧…",14);
    private readonly TextBlock _anchors=Ui.Text("手工对齐：在上图和下图各点一次同一行文字。",13);
    private readonly TextBox _offset=new(){Width=65},_cut=new(){Width=65};
    private readonly Button _confirm=new(){Content="确认加入",IsEnabled=false,Padding=new Thickness(14,8,14,8)};
    private readonly Image _resultImage=new(){Stretch=Stretch.None,HorizontalAlignment=HorizontalAlignment.Left};
    private readonly System.Windows.Shapes.Line _previewGuide=new(){Stroke=Brushes.OrangeRed,StrokeThickness=1,IsHitTestVisible=false};
    private readonly CheckBox _overlay=new(){Content="半透明叠加检查",Margin=new Thickness(8)},_actual=new(){Content="原始尺寸 100%",IsChecked=true,Margin=new Thickness(8)};
    private LongShotPairResponse? _analysis;private BitmapSource? _actualPreview,_overlayPreview;
    private double? _upperY,_lowerY;private bool _manual,_busy;private readonly int _height;
    private TaskCompletionSource? _idle;
    public Task WaitForIdleAsync()=>_idle?.Task??Task.CompletedTask;
    public ConfirmedLongShotSeam? Seam{get;private set;}
    public bool Cancelled{get;private set;}=true;
    public LongShotSeamWindow(string upper,string lower,string previewPath,LongShotWorkerClient worker)
    {
        _upper=upper;_lower=lower;_previewPath=previewPath;_worker=worker;
        var upperInfo=LongShotImage.LoadInfo(upper);var lowerInfo=LongShotImage.LoadInfo(lower);
        var upperBitmap=upperInfo.Bitmap;var lowerBitmap=lowerInfo.Bitmap;
        if(upperInfo.OriginalWidth!=lowerInfo.OriginalWidth||upperInfo.OriginalHeight!=lowerInfo.OriginalHeight)
            throw new InvalidDataException("相邻帧的物理尺寸不同，请重新框选。 ");
        _height=upperInfo.OriginalHeight;
        Title="Gtools · 检查并确认接缝";Width=Math.Min(1120,SystemParameters.WorkArea.Width-40);Height=Math.Min(900,SystemParameters.WorkArea.Height-40);
        MinWidth=700;MinHeight=500;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var root=new Grid{Margin=new Thickness(12)};
        foreach(var size in new[]{GridLength.Auto,new GridLength(210),GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new(){Height=size});
        _message.TextWrapping=TextWrapping.Wrap;_message.Margin=new Thickness(0,0,0,8);root.Children.Add(_message);
        var pair=new Grid();pair.ColumnDefinitions.Add(new());pair.ColumnDefinitions.Add(new());
        AddFrame(pair,upperInfo,0,"内容较早的一帧（上图）",true);AddFrame(pair,lowerInfo,1,"内容较晚的一帧（下图）",false);
        Grid.SetRow(pair,1);root.Children.Add(pair);
        var controls=new StackPanel();controls.Children.Add(_anchors);var align=new WrapPanel();
        align.Children.Add(Ui.Text("相对位移 px",13));align.Children.Add(_offset);
        align.Children.Add(Ui.Button("−1",async(_,_)=>await NudgeAsync(-1)));align.Children.Add(Ui.Button("+1",async(_,_)=>await NudgeAsync(1)));
        align.Children.Add(Ui.Text("接缝线（上图 y）",13));align.Children.Add(_cut);
        align.Children.Add(Ui.Button("更新预览",async(_,_)=>await RefreshAsync(true)));
        align.Children.Add(_overlay);align.Children.Add(_actual);controls.Children.Add(align);
        controls.Children.Add(Ui.Text("下方为接缝附近的实际成图；橙线标记接缝（不写入成图）。叠加图出现重影说明还没对齐。",12));
        Grid.SetRow(controls,2);root.Children.Add(controls);
        var previewGrid=new Grid{HorizontalAlignment=HorizontalAlignment.Left};previewGrid.Children.Add(_resultImage);
        var guideCanvas=new Canvas{IsHitTestVisible=false};guideCanvas.Children.Add(_previewGuide);previewGrid.Children.Add(guideCanvas);
        _resultImage.SizeChanged+=(_,_)=>PositionGuide();
        var preview=new ScrollViewer{Content=previewGrid,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=Brushes.WhiteSmoke};
        Grid.SetRow(preview,3);root.Children.Add(preview);
        var bar=new WrapPanel{Margin=new Thickness(0,8,0,0)};bar.Children.Add(_confirm);
        bar.Children.Add(Ui.Button("丢弃本帧并重采",(_,_)=>{Cancelled=false;DialogResult=false;}));
        bar.Children.Add(Ui.Button("取消任务 (Esc)",(_,_)=>Close()));Grid.SetRow(bar,4);root.Children.Add(bar);Content=root;
        _confirm.Click+=(_,_)=>Confirm();
        _overlay.Click+=(_,_)=>ShowPreview();_actual.Click+=(_,_)=>ShowPreview();
        _offset.TextChanged+=(_,_)=>Invalidate();_cut.TextChanged+=(_,_)=>Invalidate();
        Loaded+=async(_,_)=>await RefreshAsync(false);
        Closed+=(_,_)=>_lifetime.Cancel();KeyDown+=(_,e)=>{if(e.Key==Key.Escape)Close();};
    }
    private void AddFrame(Grid grid,LongShotImage.Preview info,int column,string title,bool upper)
    {
        var bitmap=info.Bitmap;
        var panel=new DockPanel{Margin=new Thickness(4)};var label=Ui.Text(title,13);DockPanel.SetDock(label,Dock.Top);panel.Children.Add(label);
        var image=new Image{Source=bitmap,Stretch=Stretch.Uniform};
        var host=new Grid();host.Children.Add(image);var marks=new Canvas{IsHitTestVisible=false};
        var marker=new System.Windows.Shapes.Line{Stroke=Brushes.OrangeRed,StrokeThickness=2,Visibility=Visibility.Collapsed};marks.Children.Add(marker);host.Children.Add(marks);
        double? selectedY=null;
        void PlaceMarker()
        {
            if(selectedY is not {} y)return;
            var scale=Math.Min(image.ActualWidth/bitmap.PixelWidth,image.ActualHeight/bitmap.PixelHeight);
            marker.X1=(image.ActualWidth-bitmap.PixelWidth*scale)/2;marker.X2=marker.X1+bitmap.PixelWidth*scale;
            marker.Y1=marker.Y2=(image.ActualHeight-bitmap.PixelHeight*scale)/2+y*scale;marker.Visibility=Visibility.Visible;
        }
        image.SizeChanged+=(_,_)=>PlaceMarker();
        image.MouseLeftButtonDown+=(_,e)=>
        {
            if(_busy)return;
            var scale=Math.Min(image.ActualWidth/bitmap.PixelWidth,image.ActualHeight/bitmap.PixelHeight);
            var marginY=(image.ActualHeight-bitmap.PixelHeight*scale)/2;
            var previewY=(e.GetPosition(image).Y-marginY)/scale;
            if(previewY<0||previewY>=bitmap.PixelHeight)return;
            var y=previewY*info.OriginalHeight/bitmap.PixelHeight;
            selectedY=previewY;PlaceMarker();
            if(upper)_upperY=y;else _lowerY=y;
            _anchors.Text=$"上图标记：{(_upperY is {} a?Math.Round(a).ToString():"未选")}px · 下图标记：{(_lowerY is {} b?Math.Round(b).ToString():"未选")}px。请点同一行，再更新预览。";
            _manual=true;Invalidate();
            if(_upperY is {} uy&&_lowerY is {} ly)
            {
                var offset=LongShotSeamGeometry.FromAnchors(uy,ly);_offset.Text=offset.ToString();_cut.Text=((offset+_height)/2).ToString();
            }
        };
        panel.Children.Add(host);Grid.SetColumn(panel,column);grid.Children.Add(panel);
    }
    private void Invalidate(){_confirm.IsEnabled=false;}
    private async Task NudgeAsync(int delta)
    {
        if(_busy||!int.TryParse(_offset.Text,out var value))return;
        _manual=true;_offset.Text=(value+delta).ToString();await RefreshAsync(true);
    }
    private async Task RefreshAsync(bool useEntered)
    {
        if(_busy)return;int? offset=null,cut=null;
        if(useEntered)
        {
            if(!int.TryParse(_offset.Text,out var d)||!int.TryParse(_cut.Text,out var c)){_message.Text="请输入整数像素。";return;}
            offset=d;cut=c;
            if(_analysis is null||d!=_analysis.Offset)_manual=true;
        }
        _busy=true;_idle=new(TaskCreationOptions.RunContinuationsAsynchronously);Invalidate();_message.Text="正在检查重叠并生成实际接缝预览…";
        _offset.IsReadOnly=true;_cut.IsReadOnly=true;
        try
        {
            var result=await _worker.AnalyzeAsync(new(Guid.NewGuid().ToString("N"),_upper,_lower,_previewPath,offset,cut),_lifetime.Token);
            _analysis=result;_message.Text=result.Error??result.Message;
            if(result.Offset>0){_offset.Text=result.Offset.ToString();_cut.Text=result.CutY.ToString();}
            _actualPreview=result.PreviewPath is {} p?LongShotImage.Load(p):null;
            _overlayPreview=result.OverlayPath is {} o?LongShotImage.Load(o):null;ShowPreview();
            _confirm.IsEnabled=result.CanConfirm&&result.Success;
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){}
        catch(Exception ex){_message.Text="分析失败，可丢弃后重采："+ex.Message;}
        finally{_busy=false;_offset.IsReadOnly=false;_cut.IsReadOnly=false;_idle.TrySetResult();}
    }
    private void ShowPreview()
    {
        var bitmap=_overlay.IsChecked==true?_overlayPreview:_actualPreview;_resultImage.Source=bitmap;
        _resultImage.Stretch=Stretch.Uniform;
        _resultImage.Width=bitmap is null?double.NaN:_actual.IsChecked==true?bitmap.PixelWidth:Math.Max(300,ActualWidth-50);
        PositionGuide();
    }
    private void PositionGuide()
    {
        if(_analysis is not {} r||_actualPreview is null){_previewGuide.Visibility=Visibility.Collapsed;return;}
        var from=Math.Max(r.Offset,r.CutY-150);var scale=_resultImage.ActualHeight/_actualPreview.PixelHeight;
        _previewGuide.X1=0;_previewGuide.X2=_resultImage.ActualWidth;_previewGuide.Y1=_previewGuide.Y2=(r.CutY-from)*scale;
        _previewGuide.Visibility=Visibility.Visible;
    }
    private void Confirm()
    {
        if(_busy||!_confirm.IsEnabled||_analysis is not{CanConfirm:true} r)return;
        Seam=new(_upper,_lower,r.Offset,r.CutY,_manual?LongShotAlignmentSource.ManualAnchors:LongShotAlignmentSource.Suggested,r.Confidence);
        Cancelled=false;DialogResult=true;
    }
}
