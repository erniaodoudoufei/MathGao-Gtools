namespace Gtools.Core;

public sealed record TodoUndoStep(string Description, IReadOnlyList<TodoItem> Items);

/// <summary>
/// 待办的「后悔药」。删除和清除已完成之前先把要删的条目原样记下来，撤销时按原 id、原顺序、
/// 原勾选状态放回去。可以连续撤销多步，最多保留最近 <c>capacity</c> 步。
/// 历史只放在内存里，和编辑器的 Ctrl+Z 一样，程序重启后清空。
/// </summary>
public sealed class TodoUndoHistory(LocalDatabase db, int capacity = 20)
{
    private readonly LinkedList<TodoUndoStep> _steps = new();

    public bool CanUndo => _steps.Count > 0;
    public int Count => _steps.Count;
    public string? NextDescription => _steps.Last?.Value.Description;

    /// <returns>被删除的条目；id 不存在时返回 null，且不产生撤销记录。</returns>
    public TodoItem? Delete(long id)
    {
        var item = db.ListTodo().FirstOrDefault(x => x.Id == id);
        if (item is null) return null;
        db.DeleteTodo(id);
        Push(new TodoUndoStep($"删除「{Shorten(item.Text)}」", new[] { item }));
        return item;
    }

    /// <returns>清除的条数；没有已完成项时返回 0，且不产生撤销记录。</returns>
    public int ClearCompleted()
    {
        var done = db.ListTodo().Where(x => x.IsDone).ToArray();
        if (done.Length == 0) return 0;
        db.ClearCompletedTodo();
        Push(new TodoUndoStep(done.Length == 1 ? $"清除「{Shorten(done[0].Text)}」" : $"清除 {done.Length} 项已完成", done));
        return done.Length;
    }

    /// <returns>刚撤销的那一步；没有可撤销的操作时返回 null。</returns>
    public TodoUndoStep? Undo()
    {
        if (_steps.Last is not { } last) return null;
        _steps.RemoveLast();
        db.RestoreTodo(last.Value.Items);
        return last.Value;
    }

    private void Push(TodoUndoStep step)
    {
        _steps.AddLast(step);
        while (_steps.Count > Math.Max(1, capacity)) _steps.RemoveFirst();
    }

    private static string Shorten(string text) => text.Length <= 12 ? text : text[..12] + "…";
}
