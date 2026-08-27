using Gtools.Core;
using Microsoft.Data.Sqlite;

namespace Gtools.Tests;

public sealed class VocabularyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Gtools.Tests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_root, "vocab.db");

    public VocabularyTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>模拟 1.1.0 的老库：没有 phonetic/familiarity/last_reviewed，而且同一个单词存了多条。</summary>
    private void CreateLegacyDatabase()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = File, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE vocabulary(
          id INTEGER PRIMARY KEY AUTOINCREMENT, word TEXT NOT NULL, meaning TEXT NOT NULL DEFAULT '',
          context TEXT NOT NULL DEFAULT '', tags TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL);
        INSERT INTO vocabulary(word,meaning,context,tags,notes,created_at) VALUES
          ('focus','旧释义','','','', '2026-01-01T00:00:00+08:00'),
          ('Focus','新释义','原句','考研','', '2026-02-01T00:00:00+08:00'),
          ('resilient','有韧性的','','','', '2026-03-01T00:00:00+08:00');
        """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Initialize_MigratesLegacyDatabaseAndCollapsesDuplicateWords()
    {
        CreateLegacyDatabase();
        var db = new LocalDatabase(File);
        db.Initialize();

        var all = db.SearchVocabulary();
        Assert.Equal(2, all.Count);
        // 大小写不同的重复词合并成一条，保留最后加入的那条。
        var focus = Assert.Single(all.Where(x => x.Word.Equals("focus", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("新释义", focus.Meaning);
        Assert.Equal("原句", focus.Context);
        Assert.Equal("考研", focus.Tags);
        // 新列有可用的默认值，老数据不丢。
        Assert.Equal("", focus.Phonetic);
        Assert.Equal(0, focus.Familiarity);
        Assert.Null(focus.LastReviewed);
        Assert.Contains(all, x => x.Word == "resilient");
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        CreateLegacyDatabase();
        var db = new LocalDatabase(File);
        db.Initialize();
        db.Initialize();
        Assert.Equal(2, db.SearchVocabulary().Count);
    }

    [Fact]
    public void AddVocabulary_MergesIntoTheExistingWordInsteadOfDuplicating()
    {
        var db = new LocalDatabase(File); db.Initialize();

        var first = db.AddVocabulary("focus", "集中", phonetic: "美 /ˈfoʊkəs/");
        var second = db.AddVocabulary("FOCUS", "", context: "Please focus.", tags: "CET4");

        Assert.Equal(first, second);
        var entry = Assert.Single(db.SearchVocabulary());
        Assert.Equal("集中", entry.Meaning);            // 空值不覆盖已有释义
        Assert.Equal("Please focus.", entry.Context);   // 非空值写入
        Assert.Equal("CET4", entry.Tags);
        Assert.Equal("美 /ˈfoʊkəs/", entry.Phonetic);   // 之前查到的音标保留
    }

    [Fact]
    public void UpdateVocabulary_PersistsPhonetic()
    {
        var db = new LocalDatabase(File); db.Initialize();
        var id = db.AddVocabulary("resilient", "有韧性的");
        var entry = db.SearchVocabulary().Single();

        db.UpdateVocabulary(entry with { Phonetic = "英 /rɪˈzɪliənt/", Notes = "例句" });

        var updated = db.SearchVocabulary().Single();
        Assert.Equal("英 /rɪˈzɪliənt/", updated.Phonetic);
        Assert.Equal("例句", updated.Notes);
        Assert.Equal(id, updated.Id);
    }

    [Fact]
    public void RecordReview_ClampsFamiliarityBetweenZeroAndFive()
    {
        var db = new LocalDatabase(File); db.Initialize();
        var id = db.AddVocabulary("focus", "集中");

        for (var i = 0; i < 8; i++) db.RecordReview(id, true);
        var maxed = db.SearchVocabulary().Single();
        Assert.Equal(5, maxed.Familiarity);
        Assert.NotNull(maxed.LastReviewed);

        for (var i = 0; i < 8; i++) db.RecordReview(id, false);
        Assert.Equal(0, db.SearchVocabulary().Single().Familiarity);
    }

    [Fact]
    public void SearchVocabulary_AlsoMatchesNotes()
    {
        var db = new LocalDatabase(File); db.Initialize();
        db.AddVocabulary("focus", "集中", notes: "He prefers to focus on the positive.");
        db.AddVocabulary("resilient", "有韧性的");

        Assert.Single(db.SearchVocabulary("positive"));
        Assert.Equal(2, db.SearchVocabulary().Count);
    }
}
