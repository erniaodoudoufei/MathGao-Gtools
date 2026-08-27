using Gtools.Core;

namespace Gtools.Tests;

public sealed class TodoStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Gtools.Tests", Guid.NewGuid().ToString("N"));
    private readonly LocalDatabase _db;

    public TodoStorageTests()
    {
        Directory.CreateDirectory(_root);
        _db = new LocalDatabase(Path.Combine(_root, "todo.db"));
        _db.Initialize();
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private long[] Add(params string[] texts) => texts.Select(_db.AddTodo).ToArray();

    [Fact]
    public void AddTodo_KeepsInsertionOrder()
    {
        Add("交作业", "打电话给张老师", "买牛奶");
        Assert.Equal(new[] { "交作业", "打电话给张老师", "买牛奶" }, _db.ListTodo().Select(x => x.Text));
        Assert.All(_db.ListTodo(), x => Assert.False(x.IsDone));
    }

    [Fact]
    public void SetTodoDone_PersistsAcrossConnectionsAndClearsCompletedAtWhenUndone()
    {
        var ids = Add("交作业", "买牛奶");

        _db.SetTodoDone(ids[0], true);
        var done = _db.ListTodo().Single(x => x.Id == ids[0]);
        Assert.True(done.IsDone);
        Assert.NotNull(done.CompletedAt);

        // 每次调用都新开连接，能读回来就说明真的落盘了，不是留在内存里。
        var reopened = new LocalDatabase(Path.Combine(_root, "todo.db"));
        Assert.True(reopened.ListTodo().Single(x => x.Id == ids[0]).IsDone);

        _db.SetTodoDone(ids[0], false);
        var undone = _db.ListTodo().Single(x => x.Id == ids[0]);
        Assert.False(undone.IsDone);
        Assert.Null(undone.CompletedAt);
    }

    [Fact]
    public void ReorderTodo_RewritesTheWholeOrder()
    {
        var ids = Add("a", "b", "c", "d");
        _db.ReorderTodo(new[] { ids[2], ids[0], ids[3], ids[1] });

        Assert.Equal(new[] { "c", "a", "d", "b" }, _db.ListTodo().Select(x => x.Text));
        // 序号必须是连续的 1..n，否则后续上移下移会算错位置。
        Assert.Equal(new[] { 1, 2, 3, 4 }, _db.ListTodo().Select(x => x.SortOrder));
    }

    [Fact]
    public void ReorderTodo_RepairsDuplicateOrExhaustedSortOrders()
    {
        // 模拟历史脏数据：所有条目序号相同，此时只靠相邻交换永远修不好。
        var ids = Add("a", "b", "c");
        _db.ReorderTodo(new[] { ids[0], ids[0], ids[0] });
        _db.ReorderTodo(new[] { ids[1], ids[2], ids[0] });

        Assert.Equal(new[] { "b", "c", "a" }, _db.ListTodo().Select(x => x.Text));
        Assert.Equal(new[] { 1, 2, 3 }, _db.ListTodo().Select(x => x.SortOrder));
    }

    [Fact]
    public void MovingAnItemUp_SwapsOnlyWithItsNeighbour()
    {
        var ids = Add("a", "b", "c");
        var order = ids.ToList();
        (order[1], order[0]) = (order[0], order[1]);   // 界面上「上移 b」就是这个操作
        _db.ReorderTodo(order);

        Assert.Equal(new[] { "b", "a", "c" }, _db.ListTodo().Select(x => x.Text));
    }

    [Fact]
    public void NewItemsGoToTheEndEvenAfterReordering()
    {
        var ids = Add("a", "b");
        _db.ReorderTodo(new[] { ids[1], ids[0] });
        _db.AddTodo("c");

        Assert.Equal(new[] { "b", "a", "c" }, _db.ListTodo().Select(x => x.Text));
    }

    [Fact]
    public void ClearCompletedTodo_RemovesOnlyDoneItems()
    {
        var ids = Add("a", "b", "c");
        _db.SetTodoDone(ids[0], true);
        _db.SetTodoDone(ids[2], true);

        _db.ClearCompletedTodo();

        Assert.Equal(new[] { "b" }, _db.ListTodo().Select(x => x.Text));
    }

    [Fact]
    public void DeleteAndUpdate_WorkOnASingleItem()
    {
        var ids = Add("a", "b");
        _db.UpdateTodoText(ids[0], "  改过的  ");
        _db.DeleteTodo(ids[1]);

        var only = Assert.Single(_db.ListTodo());
        Assert.Equal("改过的", only.Text);
    }

    [Fact]
    public void ReorderTodo_OnEmptyListIsANoOp()
    {
        _db.ReorderTodo(Array.Empty<long>());
        Assert.Empty(_db.ListTodo());
    }

    [Fact]
    public void TodoTableCoexistsWithVocabularyAndClipboard()
    {
        // 待办表是加进同一个 Initialize() 的，不能把已有的表或触发器搞坏。
        _db.AddTodo("交作业");
        _db.AddVocabulary("focus", "集中");
        var clip = new ClipboardEntry(0, "text", "hello", "hello", null, null, null, false, DateTimeOffset.Now, "H1");
        Assert.True(_db.AddClipboard(clip, 50));

        Assert.Single(_db.ListTodo());
        Assert.Single(_db.SearchVocabulary());
        Assert.Single(_db.SearchClipboard());
    }
}

public sealed class FloatingDockLayoutTests
{
    private static readonly DesktopRect Work = new(0, 0, 2560, 1400);
    private const double BarHeight = 54, TodoHeight = 150, Gap = 4;

    [Fact]
    public void ExpandingTheTodoBlockDoesNotMoveTheBarBelowIt()
    {
        // 锚点约定是「最下面那一块的左上角」，所以展开前后流量条的屏幕 Y 必须完全一致。
        var (_, collapsedTop) = FloatingDockLayout.Resolve(300, 900, 0, 170, BarHeight, Work);
        var barYCollapsed = collapsedTop;

        var above = TodoHeight + Gap;
        var (_, expandedTop) = FloatingDockLayout.Resolve(300, 900, above, 262, BarHeight + above, Work);
        var barYExpanded = expandedTop + above;

        Assert.Equal(900, barYCollapsed);
        Assert.Equal(barYCollapsed, barYExpanded);
    }

    [Fact]
    public void AnchorOf_IsTheInverseOfResolve()
    {
        var above = TodoHeight + Gap;
        var (left, top) = FloatingDockLayout.Resolve(300, 900, above, 262, BarHeight + above, Work);
        var (anchorLeft, anchorTop) = FloatingDockLayout.AnchorOf(left, top, above);

        Assert.Equal(300, anchorLeft);
        Assert.Equal(900, anchorTop);
    }

    [Fact]
    public void NeverPlaced_GoesToTheBottomRightCorner()
    {
        var (left, top) = FloatingDockLayout.Resolve(double.NaN, double.NaN, 0, 170, BarHeight, Work);
        Assert.Equal(2560 - 170 - 14, left);
        Assert.Equal(1400 - BarHeight - 14, top);
    }

    [Fact]
    public void HalfWrittenAnchorIsTreatedAsUnplaced()
    {
        var (left, _) = FloatingDockLayout.Resolve(300, double.NaN, 0, 170, BarHeight, Work);
        Assert.Equal(2560 - 170 - 14, left);
    }

    [Fact]
    public void ExpandingNearTheTopEdgeClampsInsteadOfGoingOffScreen()
    {
        // 浮窗被拖到很靠上时展开，向上生长会顶出屏幕，必须钳制回来。
        var above = TodoHeight + Gap;
        var (_, top) = FloatingDockLayout.Resolve(300, 20, above, 262, BarHeight + above, Work);
        Assert.True(top >= Work.Top, $"top={top}");
    }

    [Theory]
    [InlineData(99999d, 99999d)]
    [InlineData(-5000d, -5000d)]
    public void OffScreenAnchorsArePulledBackIntoView(double anchorLeft, double anchorTop)
    {
        var (left, top) = FloatingDockLayout.Resolve(anchorLeft, anchorTop, 0, 170, BarHeight, Work);
        Assert.InRange(left, Work.Left, Work.Right - 170);
        Assert.InRange(top, Work.Top, Work.Bottom - BarHeight);
    }
}
