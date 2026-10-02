using Gtools.Core;

namespace Gtools.Tests;

public sealed class TodoUndoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Gtools.Tests", Guid.NewGuid().ToString("N"));
    private readonly LocalDatabase _db;

    public TodoUndoTests()
    {
        Directory.CreateDirectory(_root);
        _db = new LocalDatabase(Path.Combine(_root, "todo.db"));
        _db.Initialize();
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private long[] Add(params string[] texts) => texts.Select(_db.AddTodo).ToArray();
    private string[] Texts() => _db.ListTodo().Select(x => x.Text).ToArray();

    [Fact]
    public void UndoDelete_PutsTheItemBackExactlyAsItWas()
    {
        var ids = Add("Obsidian", "向量", "双腾讯");
        _db.SetTodoDone(ids[1], true);
        var before = _db.ListTodo().Single(x => x.Id == ids[1]);
        var history = new TodoUndoHistory(_db);

        history.Delete(ids[1]);
        Assert.Equal(new[] { "Obsidian", "双腾讯" }, Texts());

        var step = history.Undo();

        Assert.NotNull(step);
        Assert.Equal(new[] { "Obsidian", "向量", "双腾讯" }, Texts()); // 回到原来的位置，不是追加到末尾
        var after = _db.ListTodo().Single(x => x.Text == "向量");
        Assert.Equal(before, after); // id、勾选状态、排序号、创建和完成时间全部一致
    }

    [Fact]
    public void UndoClearCompleted_RestoresEveryClearedItemInPlace()
    {
        var ids = Add("a", "b", "c", "d", "e");
        _db.SetTodoDone(ids[0], true);
        _db.SetTodoDone(ids[2], true);
        _db.SetTodoDone(ids[4], true);
        var history = new TodoUndoHistory(_db);

        Assert.Equal(3, history.ClearCompleted());
        Assert.Equal(new[] { "b", "d" }, Texts());

        history.Undo();

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Texts());
        Assert.Equal(new[] { true, false, true, false, true }, _db.ListTodo().Select(x => x.IsDone));
        Assert.False(history.CanUndo); // 一次清除只算一步
    }

    [Fact]
    public void MultipleOperationsUndoInReverseOrder()
    {
        var ids = Add("a", "b", "c");
        _db.SetTodoDone(ids[2], true);
        var history = new TodoUndoHistory(_db);

        history.Delete(ids[0]);
        history.ClearCompleted();
        Assert.Equal(new[] { "b" }, Texts());
        Assert.Equal(2, history.Count);
        Assert.Equal("清除「c」", history.NextDescription);

        history.Undo();
        Assert.Equal(new[] { "b", "c" }, Texts());
        Assert.Equal("删除「a」", history.NextDescription);

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, Texts());
        Assert.Null(history.Undo());
    }

    [Fact]
    public void ItemsAddedAfterTheDeleteStayWhereTheyAre()
    {
        var ids = Add("a", "b", "c");
        var history = new TodoUndoHistory(_db);

        history.Delete(ids[1]);
        _db.AddTodo("d");
        history.Undo();

        Assert.Equal(new[] { "a", "b", "c", "d" }, Texts());
        // 恢复之后新加的条目仍然排在最后。
        _db.AddTodo("e");
        Assert.Equal("e", Texts().Last());
    }

    [Fact]
    public void NothingToDeleteOrClear_DoesNotCreateAnUndoStep()
    {
        Add("a");
        var history = new TodoUndoHistory(_db);

        Assert.Null(history.Delete(9999));
        Assert.Equal(0, history.ClearCompleted());

        Assert.False(history.CanUndo);
        Assert.Null(history.NextDescription);
        Assert.Null(history.Undo());
        Assert.Equal(new[] { "a" }, Texts());
    }

    [Fact]
    public void HistoryKeepsOnlyTheMostRecentSteps()
    {
        var ids = Add("a", "b", "c", "d");
        var history = new TodoUndoHistory(_db, capacity: 2);

        history.Delete(ids[0]);
        history.Delete(ids[1]);
        history.Delete(ids[2]);

        Assert.Equal(2, history.Count);
        history.Undo();
        history.Undo();
        Assert.Null(history.Undo());
        // 最早那一步超出容量被丢弃，「a」回不来；后两步正常恢复。
        Assert.Equal(new[] { "b", "c", "d" }, Texts());
    }

    [Fact]
    public void LongTextIsShortenedInTheDescription()
    {
        var id = _db.AddTodo("周五之前把三年级二班的听课记录整理好交给教研组");
        var history = new TodoUndoHistory(_db);
        history.Delete(id);
        Assert.Equal("删除「周五之前把三年级二班的听…」", history.NextDescription);
    }

    [Fact]
    public void RestoreTodo_FallsBackToANewIdWhenTheOriginalIsTaken()
    {
        var id = _db.AddTodo("a");
        var original = _db.ListTodo().Single();

        // 原 id 仍被占用时不能覆盖它，而是作为新条目恢复。
        _db.RestoreTodo(new[] { original with { Text = "a 的副本" } });

        var all = _db.ListTodo();
        Assert.Equal(2, all.Count);
        Assert.Equal("a", all.Single(x => x.Id == id).Text);
        Assert.Contains(all, x => x.Id != id && x.Text == "a 的副本");
    }

    [Fact]
    public void UndoSurvivesReopeningTheDatabase()
    {
        // 撤销写回的是数据库本身，不是内存里的列表：换一个连接读回来也一样。
        var ids = Add("a", "b");
        var history = new TodoUndoHistory(_db);
        history.Delete(ids[0]);
        history.Undo();

        var reopened = new LocalDatabase(Path.Combine(_root, "todo.db"));
        Assert.Equal(new[] { "a", "b" }, reopened.ListTodo().Select(x => x.Text));
    }
}
