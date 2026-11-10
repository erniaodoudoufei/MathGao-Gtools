using Gtools.Core;
using Microsoft.Data.Sqlite;

namespace Gtools.Tests;

public sealed class TodoStyleTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));
    private string File=>Path.Combine(_root,"todo.db");
    private LocalDatabase Database(){var db=new LocalDatabase(File);db.Initialize();return db;}
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}

    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public void StylesPersistWithoutChangingOtherFields(bool bold,bool italic)
    {
        var db=Database();var id=db.AddTodo("合成任务");db.AddTodo("合成其他任务");db.SetTodoDone(id,true);
        var before=db.ListTodo();db.UpdateTodoStyle(id,bold,italic,TodoTextColor.Purple);
        var after=Database().ListTodo();
        Assert.Equal(before[0] with{IsBold=bold,IsItalic=italic,TextColor=TodoTextColor.Purple},after[0]);
        Assert.Equal(before[1],after[1]);
        db.UpdateTodoStyle(id,false,false,TodoTextColor.Default);Assert.Equal(before,Database().ListTodo());
    }

    [Fact]
    public void AllPaletteColorsSurviveDoneTextAndOrderChanges()
    {
        var db=Database();
        foreach(var color in Enum.GetValues<TodoTextColor>())
        {
            var id=db.AddTodo("合成颜色");db.UpdateTodoStyle(id,true,true,color);
            db.SetTodoDone(id,true);db.SetTodoDone(id,false);db.UpdateTodoText(id,"合成编辑");
        }
        db.ReorderTodo(db.ListTodo().Select(x=>x.Id).Reverse().ToArray());
        var restored=Database().ListTodo();
        Assert.Equal(Enumerable.Reverse(Enum.GetValues<TodoTextColor>()),restored.Select(x=>x.TextColor));
        Assert.All(restored,x=>{Assert.True(x.IsBold);Assert.True(x.IsItalic);Assert.False(x.IsDone);Assert.Equal("合成编辑",x.Text);});
        var plainId=db.AddTodo("合成默认");var plain=db.ListTodo().Single(x=>x.Id==plainId);
        Assert.False(plain.IsBold);Assert.False(plain.IsItalic);Assert.Equal(TodoTextColor.Default,plain.TextColor);
    }

    [Fact]
    public void OldDatabaseMigratesInPlaceAndRepeatedInitializeKeepsStyle()
    {
        Directory.CreateDirectory(_root);
        using(var connection=new SqliteConnection($"Data Source={File};Pooling=False"))
        {
            connection.Open();using var command=connection.CreateCommand();
            command.CommandText="""
                CREATE TABLE todo_items(id INTEGER PRIMARY KEY AUTOINCREMENT,text_value TEXT NOT NULL,
                  is_done INTEGER NOT NULL DEFAULT 0,sort_order INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,completed_at TEXT);
                INSERT INTO todo_items VALUES(42,'合成旧任务',1,8,'2026-10-01T12:00:00+08:00','2026-10-02T12:00:00+08:00');
                """;command.ExecuteNonQuery();
        }
        var db=Database();var before=Assert.Single(db.ListTodo());
        Assert.Equal(new TodoItem(42,"合成旧任务",true,8,DateTimeOffset.Parse("2026-10-01T12:00:00+08:00"),
            DateTimeOffset.Parse("2026-10-02T12:00:00+08:00")),before);
        db.UpdateTodoStyle(42,true,true,TodoTextColor.Green);
        Assert.Equal(before with{IsBold=true,IsItalic=true,TextColor=TodoTextColor.Green},Assert.Single(Database().ListTodo()));
    }

    [Fact]
    public void DeleteClearUndoAndConflictingIdRestoreKeepStyles()
    {
        var db=Database();var a=db.AddTodo("合成删除");var b=db.AddTodo("合成清除");
        db.UpdateTodoStyle(a,true,false,TodoTextColor.Red);db.UpdateTodoStyle(b,false,true,TodoTextColor.Blue);db.SetTodoDone(b,true);
        var before=db.ListTodo();var undo=new TodoUndoHistory(db);
        undo.Delete(a);undo.ClearCompleted();Assert.Empty(db.ListTodo());
        undo.Undo();undo.Undo();Assert.Equal(before,Database().ListTodo());
        db.RestoreTodo(new[]{before[0]});
        var copy=db.ListTodo().Single(x=>x.Id!=a&&x.Text==before[0].Text);
        Assert.Equal(before[0] with{Id=copy.Id},copy);
    }

    [Fact]
    public void InvalidColorIsRejectedAndUnknownStoredColorFallsBackSafely()
    {
        var db=Database();var id=db.AddTodo("合成任务");var before=db.ListTodo();
        Assert.Throws<ArgumentOutOfRangeException>(()=>db.UpdateTodoStyle(id,true,true,(TodoTextColor)99));
        Assert.Equal(before,db.ListTodo());
        using var connection=new SqliteConnection($"Data Source={File};Pooling=False");connection.Open();
        using var command=connection.CreateCommand();command.CommandText="UPDATE todo_items SET text_color=99";command.ExecuteNonQuery();
        Assert.Equal(TodoTextColor.Default,Assert.Single(db.ListTodo()).TextColor);
        db.UpdateTodoStyle(long.MaxValue,true,true,TodoTextColor.Red);Assert.Single(db.ListTodo());
    }
}
