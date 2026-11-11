using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Gtools.Core;

namespace Gtools.App;

internal sealed partial class FloatingDockWindow
{
    private static readonly (TodoTextColor Color,string Label)[] TodoColors =
    [ (TodoTextColor.Default,"默认"), (TodoTextColor.Red,"红色"), (TodoTextColor.Orange,"橙色"),
      (TodoTextColor.Green,"绿色"), (TodoTextColor.Blue,"蓝色"), (TodoTextColor.Purple,"紫色") ];
    private TodoTextColor _newTodoColor;
    private Button _newTodoColorButton=null!;
    private TodoTextColor[] _todoGroups=[];
    private IReadOnlyList<TodoItem> _todoDragItems=[];

    private Grid BuildTodoEntry()
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        grid.Children.Add(_todoInput);
        _newTodoColorButton=IconButton("新建颜色","●",(_,_)=>ShowNewTodoPalette());
        _newTodoColorButton.Width=28;_newTodoColorButton.Height=21;_newTodoColorButton.FontSize=14;
        _newTodoColorButton.Background=_theme.InputBackground;_newTodoColorButton.BorderBrush=_theme.Border;
        _newTodoColorButton.BorderThickness=new Thickness(1);SelectNewTodoColor(TodoTextColor.Default);
        Grid.SetColumn(_newTodoColorButton,1);grid.Children.Add(_newTodoColorButton);return grid;
    }

    private void SelectNewTodoColor(TodoTextColor color)
    {
        _newTodoColor=color;_newTodoColorButton.Foreground=TodoColorBrush(color);
        var label=TodoColors.First(x=>x.Color==color).Label;
        _newTodoColorButton.ToolTip=$"新建颜色：{label}";
        System.Windows.Automation.AutomationProperties.SetName(_newTodoColorButton,(string)_newTodoColorButton.ToolTip);
    }

    private void ShowNewTodoPalette()
    {
        _todoColorPopup.IsOpen=false;_todoColorPopup.PlacementTarget=_newTodoColorButton;
        var palette=BuildTodoPalette(_newTodoColor,color=>{SelectNewTodoColor(color);Activate();_todoInput.Focus();});
        var colors=palette.Child;palette.Child=null;
        var menu=new StackPanel();menu.Children.Add(colors);
        var order=new Button{Content="调整颜色顺序",Margin=new Thickness(2,4,2,2),Padding=new Thickness(4,2,4,2),
            Foreground=_theme.Foreground,Background=_theme.InputBackground,BorderBrush=_theme.Border};
        order.Click+=(_,_)=>_todoColorPopup.Child=BuildTodoGroupMenu();
        menu.Children.Add(order);palette.Child=menu;
        _todoColorPopup.Child=palette;_todoColorPopup.IsOpen=true;
    }

    private Border BuildTodoGroupMenu()
    {
        var menu=new StackPanel{MinWidth=154};
        menu.Children.Add(new TextBlock{Text="调整颜色顺序",Foreground=_theme.Foreground,Margin=new Thickness(4)});
        for(var index=0;index<_todoGroups.Length;index++)
        {
            var color=_todoGroups[index];var name=TodoColors.First(x=>x.Color==color).Label;
            var row=new DockPanel{Margin=new Thickness(4,1,4,1),LastChildFill=true};
            var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            actions.Children.Add(IconButton($"{name}组下移","▼",(_,_)=>MoveTodoGroupBy(color,1),index>0&&index<_todoGroups.Length-1));
            actions.Children.Insert(0,IconButton($"{name}组上移","▲",(_,_)=>MoveTodoGroupBy(color,-1),index>1));
            var label=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            label.Children.Add(new TextBlock{Text="●",Foreground=TodoColorBrush(color),Margin=new Thickness(0,0,6,0)});
            label.Children.Add(new TextBlock{Text=color==TodoTextColor.Red?"红色（固定）":name,Foreground=_theme.Foreground});
            row.Children.Add(label);menu.Children.Add(row);
        }
        var border=new Border{Background=_theme.Background,BorderBrush=_theme.Border,BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(4),Padding=new Thickness(4),Child=menu};
        border.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){_todoColorPopup.IsOpen=false;e.Handled=true;}};
        return border;
    }

    private void MoveTodoGroupBy(TodoTextColor color,int direction)
    {
        var index=Array.IndexOf(_todoGroups,color);var target=index+direction;
        if(index<=0||target<=0||target>=_todoGroups.Length)return;
        var order=TodoBoardOrder.MoveGroup(_todoGroups,color,_todoGroups[target],direction>0);
        if(order is null)return;
        var reopen=_todoColorPopup.IsOpen;
        ApplyTodoDrop(new(TodoDropKind.Group,direction>0,Colors:order));
        if(reopen){_todoColorPopup.Child=BuildTodoGroupMenu();_todoColorPopup.IsOpen=true;}
    }

    private void InitializeTodoGroups(IReadOnlyList<TodoItem> items)
    {
        _todoGroups=TodoBoardOrder.Groups(_settings.TodoColorGroupOrder,items);
        var order=_todoGroups.Select(x=>(int)x).ToArray();
        if(_settings.TodoColorGroupOrder?.SequenceEqual(order)!=true)
        {
            _settings.TodoColorGroupOrder=order;_ = SaveTodoGroupsAsync();
        }
    }

    private async Task SaveTodoGroupsAsync()
    {
        try{await _store.SaveAsync();}
        catch(Exception ex){DiagnosticLog.Write("todo","groups.save.failed",ex.GetType().Name,"Error");ShowTodoNotice("颜色顺序保存失败");}
    }

    private bool ApplyTodoDrop(TodoDrop drop)
    {
        if(drop.Kind==TodoDropKind.None)return false;
        try
        {
            if(drop.Kind==TodoDropKind.Item)_db.ReorderTodo(drop.ItemIds!);
            else
            {
                _settings.TodoColorGroupOrder=drop.Colors!.Select(x=>(int)x).ToArray();
                _ = SaveTodoGroupsAsync();
            }
            RefreshTodoKeepingScroll();return true;
        }
        catch(Exception ex){DiagnosticLog.Write("todo","sort.failed",ex.GetType().Name,"Error");ShowTodoNotice("待办排序失败，请重试");return false;}
    }

    private void BringTodoIntoView(long id)=>Dispatcher.BeginInvoke(()=>
    {
        if(_todoRowViews.TryGetValue(id,out var row))row.Row.BringIntoView();
    },DispatcherPriority.Loaded);

    private const string TodoDragFormat="Gtools.TodoOrder.v1";
    private sealed record TodoDrag(TodoItem Source,Point Start);
    private TodoDrag? _todoDragCandidate;
    private TodoItem? _todoDragging;
    private string? _todoDragToken;
    private Point _todoDragPoint;
    private readonly Canvas _todoDragOverlay=new(){IsHitTestVisible=false,ClipToBounds=true,Visibility=Visibility.Collapsed};
    private readonly Border _todoDropLine=new(){Height=2,CornerRadius=new CornerRadius(1)};
    private readonly TextBlock _todoDropHint=new(){Text="移动整组",FontSize=11,Padding=new Thickness(4,0,4,0)};
    private readonly DispatcherTimer _todoDragScroll=new(){Interval=TimeSpan.FromMilliseconds(75)};

    private void WireTodoDrag()
    {
        _todoDropLine.Background=_theme.Foreground;_todoDropHint.Foreground=_theme.Foreground;_todoDropHint.Background=_theme.InputBackground;
        _todoDragOverlay.Children.Add(_todoDropLine);_todoDragOverlay.Children.Add(_todoDropHint);
        _todoScroll.AllowDrop=true;
        _todoScroll.PreviewMouseMove+=ContinueTodoDrag;
        _todoScroll.PreviewMouseLeftButtonUp+=(_,_)=>_todoDragCandidate=null;
        _todoScroll.MouseLeave+=(_,_)=>_todoDragCandidate=null;
        _todoScroll.PreviewDragOver+=PreviewTodoDrop;
        _todoScroll.PreviewDrop+=CommitTodoDrop;
        _todoScroll.DragLeave+=(_,_)=>ClearTodoDragPreview();
        _todoDragScroll.Tick+=(_,_)=>ScrollTodoDrag();
    }

    private void StartTodoDragCandidate(TodoItem item,MouseButtonEventArgs e)
    {
        _todoDragCandidate=e.ClickCount==1&&_todoEditingId is null?new(item,e.GetPosition(_todoScroll)):null;
    }

    private void ContinueTodoDrag(object sender,MouseEventArgs e)
    {
        if(_todoDragCandidate is not { } candidate)return;
        if(e.LeftButton!=MouseButtonState.Pressed||_todoEditingId is not null){_todoDragCandidate=null;return;}
        var point=e.GetPosition(_todoScroll);
        if(Math.Abs(point.X-candidate.Start.X)<SystemParameters.MinimumHorizontalDragDistance&&
            Math.Abs(point.Y-candidate.Start.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        _todoDragCandidate=null;
        if(!_todoRowViews.TryGetValue(candidate.Source.Id,out var view))return;
        _todoDragging=candidate.Source;_todoDragToken=Guid.NewGuid().ToString("N");
        var data=new DataObject();data.SetData(TodoDragFormat,_todoDragToken,false);e.Handled=true;
        try{DragDrop.DoDragDrop(view.Row,data,DragDropEffects.Move);}
        finally{_todoDragging=null;_todoDragToken=null;ClearTodoDragPreview();}
    }

    private bool IsTodoDrag(DragEventArgs e)=>_todoDragging is not null&&_todoDragToken is not null&&
        e.Data.GetDataPresent(TodoDragFormat,false)&&Equals(e.Data.GetData(TodoDragFormat,false),_todoDragToken);

    private Rect TodoViewport()
    {
        if(_todoScroll.Template?.FindName("PART_ScrollContentPresenter",_todoScroll) is FrameworkElement viewport)
            return new(viewport.TranslatePoint(new Point(),_todoScroll),viewport.RenderSize);
        return new(new Point(),_todoScroll.RenderSize);
    }

    private (TodoItem Item,bool After)? TodoTarget(Point point)
    {
        if(!TodoViewport().Contains(point))return null;
        foreach(var item in _todoDragItems)
        {
            if(!_todoRowViews.TryGetValue(item.Id,out var view))continue;
            var y=view.Row.TranslatePoint(new Point(),_todoScroll).Y;
            if(point.Y>=y-1&&point.Y<y+view.Row.ActualHeight+1)
                return (item,point.Y>=y+view.Row.RowDefinitions[0].ActualHeight/2);
        }
        return null;
    }

    private void PreviewTodoDrop(object sender,DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;
        if(!IsTodoDrag(e)){ClearTodoDragPreview();return;}
        _todoDragPoint=e.GetPosition(_todoScroll);
        if(!TodoViewport().Contains(_todoDragPoint)){ClearTodoDragPreview();return;}
        var drop=DrawTodoDragPreview(_todoDragPoint);
        if(drop!=TodoDropKind.None)e.Effects=DragDropEffects.Move;
        if(TodoScrollDirection(TodoViewport(),_todoDragPoint)!=0)_todoDragScroll.Start();else _todoDragScroll.Stop();
    }

    private TodoDropKind DrawTodoDragPreview(Point point)
    {
        var source=_todoDragging;var target=TodoTarget(point);
        if(source is null||target is not { } t){_todoDragOverlay.Visibility=Visibility.Collapsed;return TodoDropKind.None;}
        var drop=TodoBoardOrder.Resolve(_todoDragItems,_todoGroups,source.Id,t.Item.Id,t.After);
        if(drop.Kind==TodoDropKind.None){_todoDragOverlay.Visibility=Visibility.Collapsed;return drop.Kind;}
        var boundary=t.Item;
        if(drop.Kind==TodoDropKind.Group)
        {
            var members=_todoDragItems.Where(x=>!x.IsDone&&x.TextColor==t.Item.TextColor);
            boundary=drop.After?members.Last():members.First();
        }
        var row=_todoRowViews[boundary.Id].Row;var viewport=TodoViewport();
        var y=row.TranslatePoint(new Point(0,drop.After?row.ActualHeight:0),_todoScroll).Y;
        y=Math.Clamp(y,viewport.Top,Math.Max(viewport.Top,viewport.Bottom-2));
        _todoDropLine.Width=viewport.Width;Canvas.SetLeft(_todoDropLine,viewport.Left);Canvas.SetTop(_todoDropLine,y);
        _todoDropHint.Visibility=drop.Kind==TodoDropKind.Group?Visibility.Visible:Visibility.Collapsed;
        Canvas.SetLeft(_todoDropHint,viewport.Left+5);Canvas.SetTop(_todoDropHint,Math.Clamp(y-17,viewport.Top,Math.Max(viewport.Top,viewport.Bottom-17)));
        _todoDragOverlay.Visibility=Visibility.Visible;return drop.Kind;
    }

    private static int TodoScrollDirection(Rect viewport,Point point)
    {
        if(!viewport.Contains(point))return 0;
        var edge=Math.Min(24,viewport.Height/3);
        return point.Y<viewport.Top+edge?-1:point.Y>viewport.Bottom-edge?1:0;
    }

    private void ScrollTodoDrag()
    {
        var direction=TodoScrollDirection(TodoViewport(),_todoDragPoint);
        if(direction==0||_todoDragging is null){_todoDragScroll.Stop();return;}
        _todoScroll.ScrollToVerticalOffset(_todoScroll.VerticalOffset+direction*14);
        _todoScroll.UpdateLayout();DrawTodoDragPreview(_todoDragPoint);
    }

    private void CommitTodoDrop(object sender,DragEventArgs e)
    {
        e.Handled=true;e.Effects=DragDropEffects.None;ClearTodoDragPreview();
        if(!IsTodoDrag(e)||TodoTarget(e.GetPosition(_todoScroll)) is not { } target)return;
        if(ApplyTodoDropById(_todoDragging!,target.Item,target.After))e.Effects=DragDropEffects.Move;
    }

    private bool ApplyTodoDropById(TodoItem source,TodoItem target,bool after)
    {
        var fresh=_db.ListTodo();
        if(!fresh.Any(x=>x.Id==source.Id&&!x.IsDone&&x.TextColor==source.TextColor)||
            !fresh.Any(x=>x.Id==target.Id&&!x.IsDone&&x.TextColor==target.TextColor))return false;
        return ApplyTodoDrop(TodoBoardOrder.Resolve(fresh,_todoGroups,source.Id,target.Id,after));
    }

    private void ClearTodoDragPreview(){_todoDragScroll.Stop();_todoDragOverlay.Visibility=Visibility.Collapsed;}
}
