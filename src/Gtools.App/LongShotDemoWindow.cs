using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Gtools.App;

/// <summary>Isolated, synthetic desktop fixture. It never sends chat messages or uses production data.</summary>
internal sealed class LongShotDemoWindow : Window
{
    public LongShotDemoWindow(Func<Task> capture)
    {
        Title="Gtools · 合成微信长截图验收";Width=960;Height=850;Left=60;Top=30;WindowStartupLocation=WindowStartupLocation.Manual;
        var root=new DockPanel();var bar=new WrapPanel{Margin=new Thickness(10)};
        bar.Children.Add(Ui.Button("开始长截图验收",async(_,_)=>await capture()));
        bar.Children.Add(Ui.Text("合成聊天 MSG 001–030 · 手动向上滚动 · 框选灰色消息区域",14));
        var animated=new CheckBox{Content="模拟动画 / 加载变化",Margin=new Thickness(8)};bar.Children.Add(animated);
        DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);
        var canvas=new Canvas{Width=860,Height=6600,Background=new SolidColorBrush(Color.FromRgb(250,250,250))};
        Border? moving=null;
        for(var index=1;index<=30;index++)
        {
            var y=(index-1)*220;var x=index%3==0?240:55;
            var bubble=new Border{Width=570,Height=204,Padding=new Thickness(12),Background=new SolidColorBrush(Color.FromRgb(234,234,234))};
            var p=new StackPanel();p.Children.Add(Ui.Text($"MSG {index:000} · 合成测试消息",19,FontWeights.SemiBold));
            p.Children.Add(Ui.Text("相同发送者、相似长消息和大块空白。\n数学题目、英语练习、微信聊天截图。",17));
            p.Children.Add(Ui.Text($"唯一校验编号 {index*379:000000}",17));
            if(index%4==0)
            {
                var picture=new Border{Width=150,Height=45,Background=new SolidColorBrush(Color.FromRgb((byte)(index*17%240),90,180)),HorizontalAlignment=HorizontalAlignment.Left};
                p.Children.Add(picture);moving=picture;
            }
            var footer=Ui.Text("重复的消息页脚 / 检查顺序与接缝",15);footer.Margin=new Thickness(0,12,0,0);p.Children.Add(footer);
            bubble.Child=p;Canvas.SetLeft(bubble,x);Canvas.SetTop(bubble,y+5);canvas.Children.Add(bubble);
        }
        var scroll=new ScrollViewer{Content=canvas,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};root.Children.Add(scroll);
        Content=root;Loaded+=(_,_)=>scroll.ScrollToEnd();
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(130)};var n=0;
        timer.Tick+=(_,_)=>{if(animated.IsChecked==true&&moving is not null)moving.Background=new SolidColorBrush(n++%2==0?Colors.OrangeRed:Colors.LightBlue);};
        timer.Start();Closed+=(_,_)=>timer.Stop();
    }
}
