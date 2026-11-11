using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gtools.Core;

namespace Gtools.Tests;

[Collection(TodoUiTestCollection.Name)]
public sealed class TodoBoardTests
{
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static long[] VisibleIds(TodoTopTests.TodoPanel panel)=>panel.Rows.Children.OfType<Grid>().Select(x=>(long)x.Tag).ToArray();

    [Fact]
    public void ColorPreferenceNormalizesMissingDuplicateAndUnknownValues()
    {
        Assert.Equal(new[]{TodoTextColor.Red,TodoTextColor.Blue,TodoTextColor.Green,TodoTextColor.Default,TodoTextColor.Orange,TodoTextColor.Purple},
            TodoBoardOrder.Groups([4,4,999,1,3],[]));
    }

    [Fact]
    public Task InitialGroupsFollowOldOrderAndRememberEmptyColors()=>TodoTopTests.OnSta(panel=>
    {
        var old=panel.Db.ListTodo();
        panel.Db.UpdateTodoStyle(old[0].Id,true,false,TodoTextColor.Green);
        panel.Db.UpdateTodoStyle(old[1].Id,false,true,TodoTextColor.Purple);
        panel.Db.UpdateTodoStyle(old[4].Id,false,false,TodoTextColor.Red);
        panel.Db.UpdateTodoStyle(old[7].Id,false,false,TodoTextColor.Green);
        panel.Settings.TodoColorGroupOrder=null;panel.Refresh();
        Assert.Equal(new[]{old[4].Id,old[0].Id,old[7].Id,old[1].Id},VisibleIds(panel).Take(4));
        var order=panel.Settings.TodoColorGroupOrder!.ToArray();Assert.Equal(6,order.Length);
        panel.Invoke("MoveTodoGroupBy",TodoTextColor.Orange,-1);
        Assert.NotEqual(order,panel.Settings.TodoColorGroupOrder);
        var saved=panel.ReadSavedSettings();
        Assert.Equal(panel.Settings.TodoColorGroupOrder,saved.TodoColorGroupOrder);
        var orange=panel.Db.AddTodo("橙色组的新任务",TodoTextColor.Orange);panel.Refresh();
        Assert.Equal(saved.TodoColorGroupOrder,panel.Settings.TodoColorGroupOrder);
        Assert.Contains(orange,VisibleIds(panel));
    });

    [Fact]
    public Task NewItemUsesSelectedColorAndResetsOnlyAfterSuccessfulAdd()=>TodoTopTests.OnSta(panel=>
    {
        var old=panel.Db.AddTodo("红色旧任务",TodoTextColor.Red);panel.Refresh();
        var input=panel.Field<TextBox>("_todoInput");
        panel.Invoke("SelectNewTodoColor",TodoTextColor.Red);panel.Invoke("AddTodo");
        Assert.Equal(TodoTextColor.Red,panel.Field<TodoTextColor>("_newTodoColor"));
        input.Text="  红色新任务  ";TodoTopTests.TodoPanel.Press(input,System.Windows.Input.Key.Enter);
        Assert.Equal(TodoTextColor.Default,panel.Field<TodoTextColor>("_newTodoColor"));Assert.Empty(input.Text);
        Assert.Equal("红色新任务",panel.ItemAt(0).Text);Assert.Equal(TodoTextColor.Red,panel.ItemAt(0).TextColor);
        Assert.False(panel.ItemAt(0).IsBold);Assert.Equal(old,panel.IdAt(1));
        input.Text="默认组新任务";panel.Invoke("AddTodo");Assert.Equal("默认组新任务",panel.ItemAt(2).Text);
    });

    [Fact]
    public Task AcrossColorsMovesOnlyTheGroupAndRedRemainsLocked()=>TodoTopTests.OnSta(panel=>
    {
        var red=panel.Db.AddTodo("红色",TodoTextColor.Red);var p1=panel.Db.AddTodo("紫色1",TodoTextColor.Purple);
        var p2=panel.Db.AddTodo("紫色2",TodoTextColor.Purple);var green=panel.Db.AddTodo("绿色",TodoTextColor.Green);panel.Refresh();
        var before=panel.Db.ListTodo();
        Assert.False(panel.Drop(red,green,true));Assert.False(panel.Drop(green,red,false));
        Assert.True(panel.Drop(p1,green,true));Assert.Equal(before,panel.Db.ListTodo());
        Assert.Equal(red,panel.IdAt(0));Assert.True(panel.Index(p2)<panel.Index(p1));Assert.True(panel.Index(p1)<panel.Index(green));
        Assert.True(panel.Drop(p2,green,false));Assert.True(panel.Index(green)<panel.Index(p2));
        Assert.True(panel.Index(p2)<panel.Index(p1));Assert.Equal(before,panel.Db.ListTodo());
        var saved=panel.ReadSavedSettings();
        Assert.Equal(VisibleIds(panel),TodoBoardOrder.Display(new LocalDatabase(panel.File).ListTodo(),TodoBoardOrder.Groups(saved.TodoColorGroupOrder,before)).Select(x=>x.Id));
    });

    [Fact]
    public Task CompleteUncompleteAndUndoPreserveStyleAndGroupOrder()=>TodoTopTests.OnSta(panel=>
    {
        var red=panel.Db.AddTodo("红色",TodoTextColor.Red);var green=panel.Db.AddTodo("绿色",TodoTextColor.Green);
        panel.Db.UpdateTodoStyle(red,true,true,TodoTextColor.Red);panel.Refresh();var before=VisibleIds(panel);
        panel.Row(panel.Index(green)).Children.OfType<CheckBox>().Single().IsChecked=true;
        panel.Row(panel.Index(red)).Children.OfType<CheckBox>().Single().IsChecked=true;
        Assert.Equal(new[]{green,red},VisibleIds(panel).TakeLast(2));
        Assert.Equal(TodoTextColor.Red,panel.ItemAt(panel.Index(red)).TextColor);Assert.True(panel.ItemAt(panel.Index(red)).IsBold);
        Assert.DoesNotContain(panel.Buttons(panel.Index(red)),b=>Equals(b.ToolTip,"一键置顶"));
        Assert.False(panel.Drop(red,before[1],false));Assert.False(panel.Drop(before[1],red,true));
        panel.Invoke("ClearCompletedTodo");Click(panel.UndoButton);
        Assert.Equal(new[]{green,red},VisibleIds(panel).TakeLast(2));
        panel.Row(panel.Index(green)).Children.OfType<CheckBox>().Single().IsChecked=false;
        panel.Row(panel.Index(red)).Children.OfType<CheckBox>().Single().IsChecked=false;
        Assert.Equal(before,VisibleIds(panel));Assert.True(panel.ItemAt(0).IsItalic);
    });

    [Fact]
    public Task RecolorOnlyMovesOneItemAndStaleDropDoesNotWrite()=>TodoTopTests.OnSta(panel=>
    {
        var source=panel.ItemAt(0);var target=panel.ItemAt(1);
        var red=panel.Db.AddTodo("旧红",TodoTextColor.Red);panel.Refresh();
        var palette=panel.Palette(source.Id);
        Click(((System.Windows.Controls.Primitives.UniformGrid)palette.Child).Children.OfType<Button>().Single(b=>Equals(b.Tag,TodoTextColor.Red)));
        Assert.Equal(new[]{source.Id,red},VisibleIds(panel).Take(2));
        var after=panel.Db.ListTodo();Assert.Equal(target,after.Single(x=>x.Id==target.Id));
        Assert.False((bool)panel.Invoke("ApplyTodoDropById",source,target,true)!);Assert.Equal(after,panel.Db.ListTodo());
    });

    [Fact]
    public Task DragAtViewportEdgesScrollsBothWaysWithoutCommitting()=>TodoTopTests.OnSta(panel=>
    {
        panel.Scroll.ScrollToTop();panel.Layout();var items=panel.Db.ListTodo();
        SetDragField(panel,"_todoDragging",items[0]);
        var viewport=(Rect)panel.Invoke("TodoViewport")!;
        SetDragField(panel,"_todoDragPoint",new Point(viewport.Left+20,viewport.Bottom-1));
        panel.Invoke("ScrollTodoDrag");panel.Drain();panel.Layout();var down=panel.Scroll.VerticalOffset;Assert.True(down>0);
        SetDragField(panel,"_todoDragPoint",new Point(viewport.Left+20,viewport.Top+1));
        panel.Invoke("ScrollTodoDrag");panel.Drain();panel.Layout();Assert.True(panel.Scroll.VerticalOffset<down);
        panel.Field<System.Windows.Threading.DispatcherTimer>("_todoDragScroll").Start();
        panel.Invoke("ClearTodoDragPreview");Assert.False(panel.Field<System.Windows.Threading.DispatcherTimer>("_todoDragScroll").IsEnabled);
        Assert.Equal(items,panel.Db.ListTodo());
    });

    private static void SetDragField(TodoTopTests.TodoPanel panel,string name,object value)=>
        typeof(Gtools.App.FloatingDockWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(panel.Window,value);

    [Theory]
    [InlineData(1d,MonitorTheme.Light)][InlineData(1.25d,MonitorTheme.Light)][InlineData(1.5d,MonitorTheme.Light)][InlineData(2d,MonitorTheme.Light)]
    [InlineData(1d,MonitorTheme.Dark)][InlineData(1.25d,MonitorTheme.Dark)][InlineData(1.5d,MonitorTheme.Dark)][InlineData(2d,MonitorTheme.Dark)]
    public Task GroupedLayoutAndDragPreviewKeepTheDockCompact(double scale,MonitorTheme theme)=>TodoTopTests.OnSta(panel=>
    {
        var source=panel.Db.AddTodo("紫色任务",TodoTextColor.Purple);var target=panel.Db.AddTodo("绿色任务",TodoTextColor.Green);
        VisualTreeHelper.SetRootDpi(panel.Root,new DpiScale(scale,scale));panel.Refresh();panel.Scroll.ScrollToBottom();panel.Layout();
        var entry=panel.Field<TextBox>("_todoInput");var button=panel.Field<Button>("_newTodoColorButton");
        Assert.True(entry.TranslatePoint(new Point(entry.ActualWidth,0),panel.Root).X<=button.TranslatePoint(new Point(),panel.Root).X);
        Assert.True(button.TranslatePoint(new Point(button.ActualWidth,0),panel.Root).X<=262);
        Assert.Equal(panel.Db.ListTodo().Count,panel.Rows.Children.Count);Assert.Equal(24*panel.Settings.TodoVisibleRows,panel.Scroll.MaxHeight);
        var before=panel.Db.ListTodo();var groups=panel.Settings.TodoColorGroupOrder!.ToArray();
        SetDragField(panel,"_todoDragging",before.Single(x=>x.Id==source));
        var targetPoint=panel.Row(panel.Index(target)).TranslatePoint(new Point(40,10),panel.Scroll);
        Assert.Equal(TodoDropKind.Group,panel.Invoke("DrawTodoDragPreview",targetPoint));
        Assert.Equal(Visibility.Visible,panel.Field<TextBlock>("_todoDropHint").Visibility);
        Assert.False(panel.Field<Canvas>("_todoDragOverlay").IsHitTestVisible);
        panel.Invoke("ClearTodoDragPreview");Assert.Equal(Visibility.Collapsed,panel.Field<Canvas>("_todoDragOverlay").Visibility);
        Assert.Equal(before,panel.Db.ListTodo());Assert.Equal(groups,panel.Settings.TodoColorGroupOrder);
    },theme);
}
