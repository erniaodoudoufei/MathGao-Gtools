using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gtools.Core;

namespace Gtools.Tests;

[Collection(TodoUiTestCollection.Name)]
public sealed class TodoStyleUiTests
{
    private static TextBlock Text(TodoTopTests.TodoPanel panel,int row)=>((Grid)panel.Rows.Children[row]).Children.OfType<TextBlock>().Single();
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Color(TodoTopTests.TodoPanel panel,int row,TodoTextColor color)
    {
        var palette=panel.Palette(panel.Db.ListTodo()[row].Id);
        Click(((UniformGrid)palette.Child).Children.OfType<Button>().Single(x=>Equals(x.Tag,color)));
    }

    [Fact]
    public Task TogglesComposeResetPreserveScrollAndWorkWhilePositionIsLocked()=>TodoTopTests.OnSta(panel=>
    {
        panel.Settings.MonitorLocked=true;panel.Scroll.ScrollToBottom();panel.Layout();var offset=panel.Scroll.VerticalOffset;
        var before=panel.Db.ListTodo();Click(panel.Button(10,"加粗"));Click(panel.Button(10,"倾斜"));panel.Layout();
        Assert.Equal(before[10] with{IsBold=true,IsItalic=true},panel.Db.ListTodo()[10]);
        Assert.Equal(FontWeights.Bold,Text(panel,10).FontWeight);Assert.Equal(FontStyles.Italic,Text(panel,10).FontStyle);
        Assert.Equal(true,panel.Button(10,"加粗").Tag);Assert.Equal(true,panel.Button(10,"倾斜").Tag);
        Assert.Equal(offset,panel.Scroll.VerticalOffset);Assert.Equal(0,panel.Undo.Count);
        Click(panel.Button(10,"加粗"));Click(panel.Button(10,"倾斜"));
        Assert.Equal(before,panel.Db.ListTodo());Assert.Equal(false,panel.Button(10,"加粗").Tag);
        Assert.Equal(338,panel.Settings.MonitorLeft);Assert.Equal(500,panel.Settings.MonitorTop);
    });

    [Theory][InlineData(MonitorTheme.Light)][InlineData(MonitorTheme.Dark)]
    public Task PaletteAppliesEveryColorAndCompletedTextKeepsStyle(MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        Click(panel.Button(1,"加粗"));Click(panel.Button(1,"倾斜"));
        var defaultColor=((SolidColorBrush)Text(panel,0).Foreground).Color;
        var distinct=new HashSet<System.Windows.Media.Color>();
        foreach(var color in Enum.GetValues<TodoTextColor>())
        {
            Color(panel,1,color);var item=panel.Db.ListTodo()[1];var text=Text(panel,1);
            Assert.Equal(color,item.TextColor);Assert.True(item.IsBold);Assert.True(item.IsItalic);
            Assert.Equal(.65,text.Opacity);Assert.Equal(TextDecorations.Strikethrough,text.TextDecorations);
            Assert.Equal(FontWeights.Bold,text.FontWeight);Assert.Equal(FontStyles.Italic,text.FontStyle);
            var brush=(SolidColorBrush)text.Foreground;distinct.Add(brush.Color);
            Assert.Equal(brush.Color,((SolidColorBrush)panel.Button(1,"文字颜色").Foreground).Color);
            var palette=panel.Palette(item.Id);var buttons=((UniformGrid)palette.Child).Children.OfType<Button>().ToArray();
            Assert.Equal(6,buttons.Length);Assert.Equal(new Thickness(2),buttons.Single(x=>Equals(x.Tag,color)).BorderThickness);
        }
        Assert.Equal(6,distinct.Count);Color(panel,1,TodoTextColor.Default);Assert.Equal(defaultColor,((SolidColorBrush)Text(panel,1).Foreground).Color);
        var check=((Grid)panel.Rows.Children[1]).Children.OfType<CheckBox>().Single();check.IsChecked=false;
        Assert.Equal(1,Text(panel,1).Opacity);Assert.Null(Text(panel,1).TextDecorations);
        Assert.True(panel.Db.ListTodo()[1].IsBold);Assert.True(panel.Db.ListTodo()[1].IsItalic);
    },theme);

    [Fact]
    public Task StyledItemSupportsTopMoveDeleteUndoAndRestart()=>TodoTopTests.OnSta(panel=>
    {
        Click(panel.Button(5,"加粗"));Click(panel.Button(5,"倾斜"));Color(panel,5,TodoTextColor.Purple);
        var target=panel.Db.ListTodo()[5];Click(panel.Button(5,"一键置顶"));Click(panel.Button(0,"下移"));Click(panel.Button(1,"上移"));
        var moved=panel.Db.ListTodo()[0];Assert.Equal(target with{SortOrder=moved.SortOrder},moved);
        Click(panel.Button(0,"删除"));Click(panel.UndoButton);Assert.Equal(moved,panel.Db.ListTodo()[0]);
        var reopened=new LocalDatabase(panel.File);reopened.Initialize();Assert.Equal(moved,reopened.ListTodo()[0]);
        Assert.Equal(FontWeights.Bold,Text(panel,0).FontWeight);Assert.Equal(FontStyles.Italic,Text(panel,0).FontStyle);
    });

    [Fact]
    public Task OldStyleButtonReadsLatestStateAndDeletedItemCannotChangeOthers()=>TodoTopTests.OnSta(panel=>
    {
        var oldBold=panel.Button(3,"加粗");var id=panel.Db.ListTodo()[3].Id;
        panel.Db.UpdateTodoStyle(id,false,true,TodoTextColor.Green);Click(oldBold);
        Assert.Equal(TodoTextColor.Green,panel.Db.ListTodo()[3].TextColor);Assert.True(panel.Db.ListTodo()[3].IsItalic);
        Assert.True(panel.Db.ListTodo()[3].IsBold);Click(oldBold);Assert.False(panel.Db.ListTodo()[3].IsBold);
        panel.Db.DeleteTodo(id);var before=panel.Db.ListTodo();Click(oldBold);Assert.Equal(before,panel.Db.ListTodo());
    });

    [Theory]
    [InlineData(1d,MonitorTheme.Light)][InlineData(1.25d,MonitorTheme.Light)][InlineData(1.5d,MonitorTheme.Light)][InlineData(2d,MonitorTheme.Light)]
    [InlineData(1d,MonitorTheme.Dark)][InlineData(1.25d,MonitorTheme.Dark)][InlineData(1.5d,MonitorTheme.Dark)][InlineData(2d,MonitorTheme.Dark)]
    public Task StyledRowsToolLineAndPaletteHaveReadableNonOverlappingDpiLayout(double scale,MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        var colors=Enum.GetValues<TodoTextColor>();
        var items=panel.Db.ListTodo();
        for(var i=0;i<items.Count;i++)panel.Db.UpdateTodoStyle(items[i].Id,i%2==0,i%3==0,colors[i%colors.Length]);
        panel.Settings.TodoVisibleRows=12;panel.Refresh();VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));panel.Layout();
        for(var rowIndex=0;rowIndex<items.Count;rowIndex++)
        {
            // Collapsed by default: the styled text gets the whole row (previously ~100px beside seven buttons).
            Assert.Equal(9,panel.Buttons(rowIndex).Length);Assert.True(Text(panel,rowIndex).ActualWidth>=180);
            panel.AssertRowLayout(rowIndex);
            Click(panel.Button(rowIndex,"更多"));panel.Layout();
            Assert.Equal(Visibility.Visible,panel.Tools(rowIndex).Visibility);Assert.True(Text(panel,rowIndex).ActualWidth>=140);
            panel.AssertRowLayout(rowIndex);
        }
        var palette=panel.Palette(items[0].Id);VisualTreeHelper.SetRootDpi(palette,new DpiScale(scale,scale));
        palette.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));palette.Arrange(new Rect(palette.DesiredSize));palette.UpdateLayout();
        Assert.True(palette.ActualWidth<=262);Assert.Equal(6,((UniformGrid)palette.Child).Children.Count);
        var destination=Environment.GetEnvironmentVariable("GTOOLS_TODO_STYLE_PREVIEW_ROOT");
        if(string.IsNullOrWhiteSpace(destination))return;
        Directory.CreateDirectory(destination);
        void Save(FrameworkElement visual,string name)
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth*scale),(int)Math.Ceiling(visual.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(destination,$"{name}-{theme}-{scale*100:0}.png"));encoder.Save(file);
        }
        Save(panel.Root,"todo-style");Save(palette,"palette");
    },theme);
}
