using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gtools.Core;

namespace Gtools.Tests;

public sealed class TodoEditTests
{
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static int OpenToolsRow(TodoTopTests.TodoPanel panel)=>
        Enumerable.Range(0,panel.Rows.Children.Count).Single(i=>panel.Tools(i).Visibility==Visibility.Visible);
    private static int[] OpenToolsRows(TodoTopTests.TodoPanel panel)=>
        Enumerable.Range(0,panel.Rows.Children.Count).Where(i=>panel.Tools(i).Visibility==Visibility.Visible).ToArray();

    [Theory]
    [InlineData(1d,MonitorTheme.Light)][InlineData(1.25d,MonitorTheme.Light)][InlineData(1.5d,MonitorTheme.Light)][InlineData(2d,MonitorTheme.Light)]
    [InlineData(1d,MonitorTheme.Dark)][InlineData(1.25d,MonitorTheme.Dark)][InlineData(1.5d,MonitorTheme.Dark)][InlineData(2d,MonitorTheme.Dark)]
    public Task LastRowStyleEditsPreserveTheClickableCollapseButton(double scale,MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));
        var index=panel.Rows.Children.Count-1;var rows=panel.Rows.Children.Cast<object>().ToArray();
        var id=panel.Db.ListTodo()[index].Id;var toggle=panel.Button(index,"更多");
        void AssertHit(Button button)
        {
            var point=button.TranslatePoint(new Point(button.ActualWidth/2,button.ActualHeight/2),panel.Root);
            var hit=VisualTreeHelper.HitTest(panel.Root,point)?.VisualHit;
            while(hit is not null&&!ReferenceEquals(hit,button))hit=VisualTreeHelper.GetParent(hit);
            Assert.Same(button,hit);
        }
        panel.Scroll.ScrollToBottom();panel.Hover(index);panel.Layout();AssertHit(toggle);Click(toggle);
        var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,()=>frame.Continue=false);
        Dispatcher.PushFrame(frame);panel.Layout();
        var offset=panel.Scroll.VerticalOffset;
        Assert.Equal("▴",toggle.Content);Assert.Equal("收起工具栏",toggle.ToolTip);
        AssertHit(panel.Button(index,"加粗"));Click(panel.Button(index,"加粗"));Click(panel.Button(index,"倾斜"));
        var palette=panel.Palette(id);Click(((System.Windows.Controls.Primitives.UniformGrid)palette.Child).Children.OfType<Button>().Single(x=>Equals(x.Tag,TodoTextColor.Red)));
        panel.Hover(index,false);panel.Layout();
        var item=panel.Db.ListTodo()[index];Assert.True(item.IsBold);Assert.True(item.IsItalic);Assert.Equal(TodoTextColor.Red,item.TextColor);
        Assert.Equal(rows,panel.Rows.Children.Cast<object>().ToArray());Assert.Equal(offset,panel.Scroll.VerticalOffset);
        Assert.Same(toggle,panel.Button(index,"更多"));AssertHit(toggle);Click(toggle);panel.Layout();
        Assert.Empty(OpenToolsRows(panel));Assert.Equal("⋯",toggle.Content);Assert.Equal(Visibility.Collapsed,panel.Actions(index).Visibility);
        panel.Hover(index);panel.Layout();AssertHit(toggle);Click(toggle);panel.Layout();
        Assert.Equal(index,OpenToolsRow(panel));Assert.Equal(true,panel.Button(index,"加粗").Tag);Assert.Equal(true,panel.Button(index,"倾斜").Tag);
        Assert.Equal(rows,panel.Rows.Children.Cast<object>().ToArray());Assert.Equal(0,panel.Undo.Count);
    },theme);

    [Fact]
    public Task ActionsStayHiddenUntilHoverAndToolLineFollowsTheItem()=>TodoTopTests.OnSta(panel=>
    {
        for(var i=0;i<panel.Rows.Children.Count;i++)
        {
            Assert.Equal(Visibility.Collapsed,panel.Actions(i).Visibility);Assert.Equal(Visibility.Collapsed,panel.Tools(i).Visibility);
            Assert.Same(Brushes.Transparent,panel.Row(i).Background);
        }
        panel.Hover(4);Assert.Equal(Visibility.Visible,panel.Actions(4).Visibility);
        Assert.Equal(Visibility.Collapsed,panel.Tools(4).Visibility);Assert.NotSame(Brushes.Transparent,panel.Row(4).Background);
        Assert.Equal(new[]{"编辑","更多","删除"},panel.Actions(4).Children.OfType<Button>().Select(b=>b.ToolTip));
        panel.Hover(4,false);Assert.Equal(Visibility.Collapsed,panel.Actions(4).Visibility);Assert.Same(Brushes.Transparent,panel.Row(4).Background);

        var before=panel.Db.ListTodo();var target=before[7];
        Click(panel.Button(4,"更多"));Assert.Equal(new[]{4},OpenToolsRows(panel));Assert.Equal(true,panel.Button(4,"更多").Tag);
        Click(panel.Button(7,"更多"));Assert.Equal(new[]{7},OpenToolsRows(panel));
        // Leaving an expanded row keeps its strip so 更多 can close it again.
        panel.Hover(7);panel.Hover(7,false);Assert.Equal(Visibility.Visible,panel.Actions(7).Visibility);
        Assert.Equal(before,panel.Db.ListTodo());Assert.Equal(0,panel.Undo.Count);

        Click(panel.Button(7,"上移"));Assert.Equal(target.Id,panel.Db.ListTodo()[6].Id);Assert.Equal(6,OpenToolsRow(panel));
        Click(panel.Button(6,"一键置顶"));Assert.Equal(target.Id,panel.Db.ListTodo()[0].Id);Assert.Equal(0,OpenToolsRow(panel));
        Click(panel.Button(0,"更多"));Assert.Empty(OpenToolsRows(panel));

        Click(panel.Button(3,"更多"));var removed=panel.Db.ListTodo()[3];Click(panel.Button(3,"删除"));
        Assert.DoesNotContain(panel.Db.ListTodo(),x=>x.Id==removed.Id);Assert.Empty(OpenToolsRows(panel));
        Assert.Null(panel.Field<long?>("_todoToolsId"));
        Click(panel.UndoButton);Assert.Contains(panel.Db.ListTodo(),x=>x.Id==removed.Id);Assert.Empty(OpenToolsRows(panel));
    });

    [Fact]
    public Task EditButtonSavesTrimmedTextWithEnterAndKeepsEverythingElse()=>TodoTopTests.OnSta(panel=>
    {
        var id=panel.Db.ListTodo()[5].Id;panel.Db.UpdateTodoStyle(id,true,false,TodoTextColor.Purple);panel.Db.SetTodoDone(id,true);panel.Refresh();
        var before=panel.Db.ListTodo();var item=before[5];
        Click(panel.Button(5,"编辑"));
        var editor=panel.Editor(5);Assert.NotNull(editor);Assert.Equal(item.Text,editor.Text);
        Assert.Empty(panel.Row(5).Children.OfType<TextBlock>());
        Assert.Equal(Visibility.Collapsed,panel.Actions(5).Visibility);Assert.Equal(Visibility.Collapsed,panel.Tools(5).Visibility);
        // Only one editor at a time and other rows are untouched.
        Assert.Single(Enumerable.Range(0,panel.Rows.Children.Count),i=>panel.Editor(i) is not null);

        editor.Text="  改过的合成事项  ";TodoTopTests.TodoPanel.Press(editor,Key.Enter);
        var after=panel.Db.ListTodo();
        Assert.Equal(item with{Text="改过的合成事项"},after[5]);
        Assert.Equal(before.Where(x=>x.Id!=id),after.Where(x=>x.Id!=id));
        Assert.Null(panel.Editor(5));Assert.Equal("改过的合成事项",panel.Row(5).Children.OfType<TextBlock>().Single().Text);
        Assert.Null(panel.Field<long?>("_todoEditingId"));Assert.Equal(0,panel.Undo.Count);
        var reopened=new LocalDatabase(panel.File);reopened.Initialize();Assert.Equal(after,reopened.ListTodo());
        Assert.Equal(338,panel.Settings.MonitorLeft);Assert.Equal(500,panel.Settings.MonitorTop);
    });

    [Fact]
    public Task EscapeBlankAndUnchangedEditsLeaveTheItemAlone()=>TodoTopTests.OnSta(panel=>
    {
        var before=panel.Db.ListTodo();
        Click(panel.Button(2,"编辑"));var editor=panel.Editor(2)!;editor.Text="不该保存";TodoTopTests.TodoPanel.Press(editor,Key.Escape);
        Assert.Null(panel.Editor(2));Assert.Equal(before,panel.Db.ListTodo());
        // Clearing the box is a cancel, never a delete: deletion stays on ✕ where it can be undone.
        Click(panel.Button(2,"编辑"));editor=panel.Editor(2)!;editor.Text="   ";TodoTopTests.TodoPanel.Press(editor,Key.Enter);
        Assert.Null(panel.Editor(2));Assert.Equal(before,panel.Db.ListTodo());
        Click(panel.Button(2,"编辑"));TodoTopTests.TodoPanel.Press(panel.Editor(2)!,Key.Enter);Assert.Equal(before,panel.Db.ListTodo());
    });

    [Fact]
    public Task DoubleClickOpensEditorAndLosingFocusSaves()=>TodoTopTests.OnSta(panel=>
    {
        panel.ClickText(9,1);Assert.Null(panel.Editor(9));
        panel.ClickText(9,2);var editor=panel.Editor(9);Assert.NotNull(editor);
        var id=panel.Db.ListTodo()[9].Id;editor.Text="双击改的";TodoTopTests.TodoPanel.LoseFocus(editor);
        Assert.Equal("双击改的",panel.Db.ListTodo().Single(x=>x.Id==id).Text);Assert.Null(panel.Editor(9));
    });

    [Fact]
    public Task DraftSurvivesRebuildsFollowsTheItemAndStaleEditorCannotCommit()=>TodoTopTests.OnSta(panel=>
    {
        var before=panel.Db.ListTodo();var id=before[3].Id;
        Click(panel.Button(3,"编辑"));var stale=panel.Editor(3)!;stale.Text="还没保存的草稿";
        // Ticking another row rebuilds the whole list; the draft must come along and nothing is written yet.
        ((Grid)panel.Rows.Children[8]).Children.OfType<CheckBox>().Single().IsChecked=true;
        Assert.True(panel.Db.ListTodo()[8].IsDone);Assert.Equal(before[3].Text,panel.Db.ListTodo()[3].Text);
        var current=panel.Editor(3);Assert.NotNull(current);Assert.NotSame(stale,current);Assert.Equal("还没保存的草稿",current.Text);
        // The replaced box losing focus is a side effect of the rebuild, not the user leaving.
        TodoTopTests.TodoPanel.LoseFocus(stale);Assert.Equal(before[3].Text,panel.Db.ListTodo()[3].Text);Assert.NotNull(panel.Editor(3));

        panel.Db.ReorderTodo(panel.Db.ListTodo().Select(x=>x.Id).Reverse().ToArray());panel.Refresh();
        var index=panel.Db.ListTodo().ToList().FindIndex(x=>x.Id==id);Assert.Equal(8,index);
        var moved=panel.Editor(index);Assert.NotNull(moved);Assert.Equal("还没保存的草稿",moved.Text);
        TodoTopTests.TodoPanel.Press(moved,Key.Enter);
        Assert.Equal("还没保存的草稿",panel.Db.ListTodo().Single(x=>x.Id==id).Text);
        Assert.Equal(before.Count,panel.Db.ListTodo().Count);
    });

    [Fact]
    public Task EditingAnotherRowSavesTheFirstAndDeletedItemIsNeverWrittenBack()=>TodoTopTests.OnSta(panel=>
    {
        var first=panel.Db.ListTodo()[1].Id;var second=panel.Db.ListTodo()[6].Id;
        Click(panel.Button(1,"编辑"));panel.Editor(1)!.Text="第一条改好";Click(panel.Button(6,"编辑"));
        Assert.Equal("第一条改好",panel.Db.ListTodo().Single(x=>x.Id==first).Text);
        Assert.Null(panel.Editor(1));Assert.NotNull(panel.Editor(6));

        panel.Editor(6)!.Text="不应写回";panel.Db.DeleteTodo(second);var remaining=panel.Db.ListTodo();
        TodoTopTests.TodoPanel.Press(panel.Editor(6)!,Key.Enter);
        Assert.Equal(remaining,panel.Db.ListTodo());Assert.DoesNotContain(panel.Db.ListTodo(),x=>x.Text=="不应写回");

        var third=panel.Db.ListTodo()[4].Id;Click(panel.Button(4,"编辑"));panel.Db.DeleteTodo(third);panel.Refresh();
        Assert.Null(panel.Field<long?>("_todoEditingId"));
        Assert.All(Enumerable.Range(0,panel.Rows.Children.Count),i=>Assert.Null(panel.Editor(i)));
    });

    [Theory]
    [InlineData(1d,MonitorTheme.Light)][InlineData(1.25d,MonitorTheme.Light)][InlineData(1.5d,MonitorTheme.Light)][InlineData(2d,MonitorTheme.Light)]
    [InlineData(1d,MonitorTheme.Dark)][InlineData(1.25d,MonitorTheme.Dark)][InlineData(1.5d,MonitorTheme.Dark)][InlineData(2d,MonitorTheme.Dark)]
    public Task DefaultHoverToolsAndEditingStatesFitAtSupportedDpi(double scale,MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        panel.Settings.TodoVisibleRows=12;panel.Refresh();
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));panel.Layout();
        var destination=Environment.GetEnvironmentVariable("GTOOLS_TODO_EDIT_PREVIEW_ROOT");
        void Save(string state)
        {
            if(string.IsNullOrWhiteSpace(destination))return;
            Directory.CreateDirectory(destination);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(262*scale),(int)Math.Ceiling(panel.Root.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(panel.Root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(destination,$"todo-{state}-{theme}-{scale*100:0}.png"));encoder.Save(file);
        }
        for(var i=0;i<panel.Rows.Children.Count;i++)panel.AssertRowLayout(i);
        Save("default");
        var height=panel.Row(2).ActualHeight;
        panel.Hover(2);panel.Layout();panel.AssertRowLayout(2);Assert.Equal(height,panel.Row(2).ActualHeight);Save("hover");
        panel.Hover(2,false);Click(panel.Button(2,"更多"));panel.Layout();panel.AssertRowLayout(2);
        Assert.True(panel.Row(2).ActualHeight>height);Save("tools");
        Click(panel.Button(5,"编辑"));panel.Layout();
        var editor=panel.Editor(5)!;Assert.True(editor.ActualWidth>=180);
        Assert.True(editor.TranslatePoint(new Point(editor.ActualWidth,0),panel.Root).X<=262);
        Assert.Empty(OpenToolsRows(panel));Save("editing");
    },theme);
}
