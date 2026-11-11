namespace Gtools.Core;

internal enum TodoDropKind { None, Item, Group }
internal sealed record TodoDrop(TodoDropKind Kind, bool After, long[]? ItemIds = null, TodoTextColor[]? Colors = null);

/// <summary>Projects storage order into the board and resolves drops without changing storage during a preview.</summary>
internal static class TodoBoardOrder
{
    internal static TodoTextColor[] Groups(IEnumerable<int>? saved, IReadOnlyList<TodoItem> items) =>
        new[] { TodoTextColor.Red }
            .Concat(saved is null ? items.Where(x => !x.IsDone).Select(x => x.TextColor) : saved.Select(x => (TodoTextColor)x))
            .Concat(Enum.GetValues<TodoTextColor>()).Where(x => Enum.IsDefined(x)).Distinct().ToArray();

    internal static TodoItem[] Display(IReadOnlyList<TodoItem> items, IReadOnlyList<TodoTextColor> groups) =>
        groups.SelectMany(color => items.Where(x => !x.IsDone && x.TextColor == color))
            .Concat(items.Where(x => x.IsDone).OrderBy(x => x.CompletedAt)).ToArray();

    internal static TodoTextColor[]? MoveGroup(IReadOnlyList<TodoTextColor> groups, TodoTextColor source, TodoTextColor target, bool after)
    {
        if (source == TodoTextColor.Red || source == target || (target == TodoTextColor.Red && !after) ||
            !groups.Contains(source) || !groups.Contains(target)) return null;
        var result = groups.Where(x => x != source).ToList();
        result.Insert(result.IndexOf(target) + (after ? 1 : 0), source);
        return result.SequenceEqual(groups) ? null : result.ToArray();
    }

    internal static TodoDrop Resolve(IReadOnlyList<TodoItem> items, IReadOnlyList<TodoTextColor> groups, long sourceId, long targetId, bool afterRow)
    {
        var source = items.FirstOrDefault(x => x.Id == sourceId && !x.IsDone);
        var target = items.FirstOrDefault(x => x.Id == targetId && !x.IsDone);
        if (source is null || target is null || sourceId == targetId) return new(TodoDropKind.None, false);
        if (source.TextColor != target.TextColor)
        {
            var afterGroup = Array.IndexOf(groups.ToArray(), source.TextColor) < Array.IndexOf(groups.ToArray(), target.TextColor);
            var order = MoveGroup(groups, source.TextColor, target.TextColor, afterGroup);
            return order is null ? new(TodoDropKind.None, false) : new(TodoDropKind.Group, afterGroup, Colors: order);
        }

        var members = items.Where(x => !x.IsDone && x.TextColor == source.TextColor).Select(x => x.Id).ToList();
        members.Remove(sourceId);
        members.Insert(members.IndexOf(targetId) + (afterRow ? 1 : 0), sourceId);
        // Reuse the slots occupied by this active group, including when completed items are interspersed in storage.
        var next = 0;
        var ids = items.Select(x => !x.IsDone && x.TextColor == source.TextColor ? members[next++] : x.Id).ToArray();
        return ids.SequenceEqual(items.Select(x => x.Id)) ? new(TodoDropKind.None, false) : new(TodoDropKind.Item, afterRow, ids);
    }
}
