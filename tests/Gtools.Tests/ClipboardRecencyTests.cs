using Gtools.App;
using Gtools.Core;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class ClipboardRecencyTests
{
    internal static ClipboardEntry Entry(string name,string kind="text",bool favorite=false)=>new(0,kind,$"synthetic {name}",
        $"synthetic {name}",kind=="rich-text"?$"<b>{name}</b>":null,kind=="rich-text"?$"{{\\rtf1 {name}}}":null,
        kind=="image"?ClipboardFixture.Png(SKColors.Blue,128):null,favorite,new DateTimeOffset(2026,1,2,3,4,5,TimeSpan.Zero),name);

    internal static ClipboardEntry Add(LocalDatabase db,string name,string kind="text",bool favorite=false,int limit=500)
    {
        Assert.True(db.AddClipboard(Entry(name,kind,favorite),limit));return db.SearchClipboard().First();
    }

    internal static void SamePayload(ClipboardEntry before,ClipboardEntry after)
    {
        Assert.Equal(before.Id,after.Id);Assert.Equal(before.Kind,after.Kind);Assert.Equal(before.Preview,after.Preview);
        Assert.Equal(before.Text,after.Text);Assert.Equal(before.Html,after.Html);Assert.Equal(before.Rtf,after.Rtf);
        Assert.Equal(before.ImagePng,after.ImagePng);Assert.Equal(before.IsFavorite,after.IsFavorite);
        Assert.Equal(before.CreatedAt,after.CreatedAt);Assert.Equal(before.Hash,after.Hash);
    }

    [Theory][InlineData("text")][InlineData("rich-text")][InlineData("image")]
    public void UseMovesOriginalAheadOfFavoritesAndSurvivesRestart(string kind)
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var old=Add(db,"old",kind);var favorite=Add(db,"favorite",favorite:true);var newest=Add(db,"newest");
        Assert.Equal(new[]{newest.Id,favorite.Id,old.Id},db.SearchClipboard().Select(x=>x.Id));
        Assert.True(db.RecordClipboardUse(old.Id));
        Assert.Equal(new[]{old.Id,newest.Id,favorite.Id},db.SearchClipboard().Select(x=>x.Id));
        SamePayload(old,db.GetClipboard(old.Id)!);
        var reopened=new LocalDatabase(fixture.File);reopened.Initialize();
        Assert.Equal(old.Id,reopened.SearchClipboardSummaries().First().Id);
        Assert.Equal(db.SearchClipboard().Select(x=>x.Id),reopened.SearchClipboard("synthetic").Select(x=>x.Id));
        Assert.Equal(db.SearchClipboardSummaries().Select(x=>x.Id),reopened.SearchClipboardSummaries("synthetic").Select(x=>x.Id));
        var later=Add(db,"later");Assert.Equal(later.Id,db.SearchClipboard().First().Id);
        Assert.True(db.RecordClipboardUse(favorite.Id));Assert.Equal(favorite.Id,db.SearchClipboard().First().Id);
        SamePayload(favorite,db.GetClipboard(favorite.Id)!);
    }

    [Fact]
    public void DuplicateCopyMovesTheLastCapturedRowWithoutChangingIdentityOrCount()
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var a=Add(db,"a");var b=Add(db,"b","image",favorite:true);db.RecordClipboardUse(a.Id);
        Assert.False(db.AddClipboard(b with{CreatedAt=DateTimeOffset.Now},500,out var changed));Assert.True(changed);
        Assert.Equal(new[]{b.Id,a.Id},db.SearchClipboard().Select(x=>x.Id));SamePayload(b,db.GetClipboard(b.Id)!);
        // A non-consecutive external copy remains a new record under the existing dedup policy.
        Assert.True(db.AddClipboard(a,500));Assert.Equal(3,db.SearchClipboard().Count);
    }

    [Fact]
    public void ReadingOrTogglingFavoritesDoesNotReorderAndMissingUseDoesNotInsert()
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;var a=Add(db,"a","image");var b=Add(db,"b");
        db.GetClipboard(a.Id);db.GetClipboardImage(a.Id);db.SearchClipboardSummaries("synthetic");
        db.SetClipboardFavorite(a.Id,true);Assert.Equal(new[]{b.Id,a.Id},db.SearchClipboard().Select(x=>x.Id));
        db.SetClipboardFavorite(a.Id,false);Assert.False(db.RecordClipboardUse(long.MaxValue));
        Assert.Equal(new[]{b.Id,a.Id},db.SearchClipboard().Select(x=>x.Id));
    }

    [Fact]
    public void RetentionKeepsRecentlyUsedOldRowsAndAllFavorites()
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var favorite=Add(db,"favorite",favorite:true,limit:50);var used=Add(db,"used",limit:50);var evicted=Add(db,"old-unused",limit:50);
        for(var i=0;i<47;i++)Add(db,$"filler-{i}",limit:50);
        db.RecordClipboardUse(used.Id);Add(db,"new-1",limit:50);Add(db,"new-2",limit:50);
        Assert.NotNull(db.GetClipboard(used.Id));Assert.NotNull(db.GetClipboard(favorite.Id));Assert.Null(db.GetClipboard(evicted.Id));
        Assert.Equal(50,db.SearchClipboard().Count(x=>!x.IsFavorite));
        db.ClearClipboard();Assert.Equal(favorite.Id,Assert.Single(db.SearchClipboard()).Id);
    }

    [Fact]
    public void LegacyDatabaseIsUpgradedInPlaceAndFtsUpdatesOnlyForTextChanges()
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;
        var a=Add(db,"a","rich-text");var b=Add(db,"b","image",favorite:true);
        using(var c=Connect(fixture.File))
        {
            using var cmd=c.CreateCommand();
            // Reproduce the pre-1.1.12 table and its overly broad update trigger, with actual data/FTS.
            cmd.CommandText="""
                DROP INDEX ix_clipboard_activity;
                ALTER TABLE clipboard_items DROP COLUMN activity_order;
                DROP TRIGGER clipboard_au;
                CREATE TRIGGER clipboard_au AFTER UPDATE ON clipboard_items BEGIN
                  INSERT INTO clipboard_fts(clipboard_fts,rowid,preview,text_value) VALUES('delete',old.id,old.preview,old.text_value);
                  INSERT INTO clipboard_fts(rowid,preview,text_value) VALUES(new.id,new.preview,new.text_value);
                END;
                """;cmd.ExecuteNonQuery();
        }
        db.Initialize();Assert.Equal(new[]{b.Id,a.Id},db.SearchClipboard().Select(x=>x.Id));
        SamePayload(a,db.GetClipboard(a.Id)!);SamePayload(b,db.GetClipboard(b.Id)!);
        using(var c=Connect(fixture.File))
        {
            using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT activity_order=id FROM clipboard_items";
            using(var reader=cmd.ExecuteReader())while(reader.Read())Assert.True(reader.GetBoolean(0));
            cmd.CommandText="SELECT sql FROM sqlite_master WHERE name='clipboard_au'";Assert.Contains("UPDATE OF preview,text_value",(string)cmd.ExecuteScalar()!);
        }
        db.RecordClipboardUse(a.Id);db.Initialize();Assert.Equal(a.Id,db.SearchClipboard().First().Id);
        Assert.Equal(2,db.SearchClipboard("synthetic").Count);
        using(var c=Connect(fixture.File))
        {
            using var cmd=c.CreateCommand();cmd.CommandText="UPDATE clipboard_items SET text_value='replacementtoken',preview='replacementtoken' WHERE id=$id";
            cmd.Parameters.AddWithValue("$id",a.Id);cmd.ExecuteNonQuery();
        }
        Assert.Equal(a.Id,Assert.Single(db.SearchClipboard("replacementtoken")).Id);
        db.DeleteClipboard(a.Id);Assert.Empty(db.SearchClipboard("replacementtoken"));Assert.Equal(b.Id,Assert.Single(db.SearchClipboard("synthetic")).Id);
    }

    [Fact]
    public async Task ConcurrentUseUpdatesAreSerializedWithoutLosingRows()
    {
        using var fixture=new ClipboardFixture();var entries=Enumerable.Range(0,12).Select(i=>Add(fixture.Database,$"entry-{i}")).ToArray();
        await Task.WhenAll(entries.Select(e=>Task.Run(()=>Assert.True(fixture.Database.RecordClipboardUse(e.Id)))));
        using var c=Connect(fixture.File);using var cmd=c.CreateCommand();cmd.CommandText="SELECT activity_order FROM clipboard_items ORDER BY activity_order";
        var orders=new List<long>();using(var reader=cmd.ExecuteReader())while(reader.Read())orders.Add(reader.GetInt64(0));
        Assert.Equal(Enumerable.Range(13,12).Select(x=>(long)x),orders);
    }

    [Fact]
    public async Task FailedRestoreDoesNotChangeOrderOrEmitUpdate()
    {
        using var fixture=new ClipboardFixture();var a=Add(fixture.Database,"a");var b=Add(fixture.Database,"b");var updates=0;var marked=0;
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,new FakeClipboardReader(),
            restore:_=>Task.FromException(new IOException("synthetic restore failure")),recordUse:_=>{marked++;return true;});
        service.Updated+=(_,_)=>updates++;
        await Assert.ThrowsAsync<IOException>(()=>service.RestoreAsync(a));
        Assert.Equal(0,marked);Assert.Equal(0,updates);Assert.Equal(b.Id,fixture.Database.SearchClipboard().First().Id);
    }

    [Fact]
    public async Task FailedRecencyWriteIsAWarningNotARestoreFailure()
    {
        using var fixture=new ClipboardFixture();var a=Add(fixture.Database,"a");var b=Add(fixture.Database,"b");var restored=0;
        using var service=new ClipboardHistoryService(fixture.Database,()=>500,new FakeClipboardReader(),
            restore:_=>{restored++;return Task.CompletedTask;},recordUse:_=>throw new IOException("synthetic database failure"));
        var result=await service.RestoreAsync(a);Assert.Contains("内容已恢复",result.Warning);Assert.Equal(1,restored);
        Assert.Equal(b.Id,fixture.Database.SearchClipboard().First().Id);Assert.Equal(2,fixture.Database.SearchClipboard().Count);
    }

    [Fact]
    public async Task SlowCaptureSaveFinishesBeforeLaterUseAndDuplicateRefreshesObservers()
    {
        using var fixture=new ClipboardFixture();var db=fixture.Database;var a=Add(db,"a");var reader=new FakeClipboardReader();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var service=new ClipboardHistoryService(db,()=>500,reader,async(_,token)=>
        {await release.Task.WaitAsync(token);return db.AddClipboard(Entry("b"),500);},restore:_=>Task.CompletedTask))
        {
            reader.Set(ClipboardFixture.Snapshot(1));await service.NotifyChangedAsync();
            var use=service.RestoreAsync(a);Assert.False(use.IsCompleted);release.SetResult();
            Assert.Null((await use).Warning);Assert.Equal(a.Id,db.SearchClipboard().First().Id);
        }
        using(var service=new ClipboardHistoryService(db,()=>500,reader,restore:_=>Task.CompletedTask))
        {
            var updates=0;service.Updated+=(_,_)=>Interlocked.Increment(ref updates);
            var snapshot=ClipboardFixture.Snapshot(2);reader.Set(snapshot);await service.NotifyChangedAsync();await ClipboardFixture.Until(()=>service.State.PendingCount==0);
            var newest=db.SearchClipboard().First();await service.RestoreAsync(a);var priorUpdates=Volatile.Read(ref updates);
            reader.Set(snapshot with{Sequence=3});await service.NotifyChangedAsync();await ClipboardFixture.Until(()=>service.State.PendingCount==0);
            Assert.True(Volatile.Read(ref updates)>priorUpdates);Assert.Equal(newest.Id,db.SearchClipboard().First().Id);
            Assert.Equal(3,db.SearchClipboard().Count);
        }
    }

    private static SqliteConnection Connect(string path)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());connection.Open();return connection;
    }
}
