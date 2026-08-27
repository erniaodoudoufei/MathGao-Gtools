using Microsoft.Data.Sqlite;

namespace Gtools.Core;

public sealed record VocabularyEntry(long Id, string Word, string Meaning, string Context, string Tags, string Notes,
    DateTimeOffset CreatedAt, string Phonetic = "", int Familiarity = 0, DateTimeOffset? LastReviewed = null);
public sealed record ClipboardEntry(long Id, string Kind, string Preview, string? Text, string? Html, string? Rtf,
    byte[]? ImagePng, bool IsFavorite, DateTimeOffset CreatedAt, string Hash);
public sealed record TodoItem(long Id, string Text, bool IsDone, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed class LocalDatabase
{
    private readonly string _connectionString;
    private readonly string _file;
    public LocalDatabase(string? path = null)
    {
        var file = path ?? AppPaths.DatabaseFile;_file=file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS vocabulary(
          id INTEGER PRIMARY KEY AUTOINCREMENT, word TEXT NOT NULL, meaning TEXT NOT NULL DEFAULT '',
          context TEXT NOT NULL DEFAULT '', tags TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_vocabulary_word ON vocabulary(word);
        CREATE TABLE IF NOT EXISTS clipboard_items(
          id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, preview TEXT NOT NULL DEFAULT '',
          text_value TEXT, html_value TEXT, rtf_value TEXT, image_png BLOB, is_favorite INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL, content_hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_clipboard_created ON clipboard_items(created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_clipboard_hash ON clipboard_items(content_hash);
        CREATE VIRTUAL TABLE IF NOT EXISTS clipboard_fts USING fts5(preview, text_value, content='clipboard_items', content_rowid='id');
        CREATE TRIGGER IF NOT EXISTS clipboard_ai AFTER INSERT ON clipboard_items BEGIN
          INSERT INTO clipboard_fts(rowid, preview, text_value) VALUES (new.id, new.preview, new.text_value);
        END;
        CREATE TRIGGER IF NOT EXISTS clipboard_ad AFTER DELETE ON clipboard_items BEGIN
          INSERT INTO clipboard_fts(clipboard_fts, rowid, preview, text_value) VALUES('delete', old.id, old.preview, old.text_value);
        END;
        CREATE TRIGGER IF NOT EXISTS clipboard_au AFTER UPDATE ON clipboard_items BEGIN
          INSERT INTO clipboard_fts(clipboard_fts, rowid, preview, text_value) VALUES('delete', old.id, old.preview, old.text_value);
          INSERT INTO clipboard_fts(rowid, preview, text_value) VALUES (new.id, new.preview, new.text_value);
        END;
        CREATE TABLE IF NOT EXISTS todo_items(
          id INTEGER PRIMARY KEY AUTOINCREMENT, text_value TEXT NOT NULL, is_done INTEGER NOT NULL DEFAULT 0,
          sort_order INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, completed_at TEXT);
        CREATE INDEX IF NOT EXISTS ix_todo_order ON todo_items(sort_order);
        """;
        cmd.ExecuteNonQuery();
        MigrateVocabulary(c);
    }

    // 1.1.0 的生词表没有音标和复习字段；老库需要就地补列，不能靠重建表丢数据。
    private static void MigrateVocabulary(SqliteConnection c)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = c.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(vocabulary)";
            using var r = info.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }
        foreach (var (column, definition) in new[]
        {
            ("phonetic", "TEXT NOT NULL DEFAULT ''"),
            ("familiarity", "INTEGER NOT NULL DEFAULT 0"),
            ("last_reviewed", "TEXT")
        })
        {
            if (existing.Contains(column)) continue;
            using var alter = c.CreateCommand();
            alter.CommandText = $"ALTER TABLE vocabulary ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
        // 唯一索引必须在去重之后建，否则老库里已有的重复单词会让建索引直接失败。
        using var dedupe = c.CreateCommand();
        dedupe.CommandText =
            "DELETE FROM vocabulary WHERE id NOT IN (SELECT MAX(id) FROM vocabulary GROUP BY word COLLATE NOCASE);" +
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_vocabulary_word ON vocabulary(word COLLATE NOCASE);";
        dedupe.ExecuteNonQuery();
    }

    /// <summary>同一个单词只保留一条：重复加入时合并释义与原句，而不是堆出一堆重复行。</summary>
    public long AddVocabulary(string word, string meaning, string context = "", string tags = "", string notes = "", string phonetic = "")
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText =
        """
        INSERT INTO vocabulary(word,meaning,context,tags,notes,created_at,phonetic) VALUES($w,$m,$c,$t,$n,$d,$p)
        ON CONFLICT(word COLLATE NOCASE) DO UPDATE SET
          meaning=CASE WHEN length(excluded.meaning)>0 THEN excluded.meaning ELSE vocabulary.meaning END,
          context=CASE WHEN length(excluded.context)>0 THEN excluded.context ELSE vocabulary.context END,
          tags=CASE WHEN length(excluded.tags)>0 THEN excluded.tags ELSE vocabulary.tags END,
          notes=CASE WHEN length(excluded.notes)>0 THEN excluded.notes ELSE vocabulary.notes END,
          phonetic=CASE WHEN length(excluded.phonetic)>0 THEN excluded.phonetic ELSE vocabulary.phonetic END;
        SELECT id FROM vocabulary WHERE word=$w COLLATE NOCASE;
        """;
        cmd.Parameters.AddWithValue("$w", word.Trim()); cmd.Parameters.AddWithValue("$m", meaning.Trim());
        cmd.Parameters.AddWithValue("$c", context.Trim()); cmd.Parameters.AddWithValue("$t", tags.Trim());
        cmd.Parameters.AddWithValue("$n", notes.Trim()); cmd.Parameters.AddWithValue("$d", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$p", phonetic.Trim());
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void RecordReview(long id, bool remembered)
    {
        Execute(remembered
            ? "UPDATE vocabulary SET familiarity=MIN(familiarity+1,5), last_reviewed=$d WHERE id=$id"
            : "UPDATE vocabulary SET familiarity=MAX(familiarity-1,0), last_reviewed=$d WHERE id=$id",
            ("$d", DateTimeOffset.Now.ToString("O")), ("$id", id));
    }

    public IReadOnlyList<VocabularyEntry> SearchVocabulary(string query = "")
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        const string columns = "id,word,meaning,context,tags,notes,created_at,phonetic,familiarity,last_reviewed";
        cmd.CommandText = string.IsNullOrWhiteSpace(query)
            ? $"SELECT {columns} FROM vocabulary ORDER BY id DESC"
            : $"SELECT {columns} FROM vocabulary WHERE word LIKE $q OR meaning LIKE $q OR context LIKE $q OR tags LIKE $q OR notes LIKE $q ORDER BY id DESC";
        if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
        using var r = cmd.ExecuteReader(); var list = new List<VocabularyEntry>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            DateTimeOffset.Parse(r.GetString(6)), r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? 0 : (int)r.GetInt64(8),
            r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9))));
        return list;
    }

    public void UpdateVocabulary(VocabularyEntry item)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vocabulary SET word=$w,meaning=$m,context=$c,tags=$t,notes=$n,phonetic=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$w", item.Word); cmd.Parameters.AddWithValue("$m", item.Meaning);
        cmd.Parameters.AddWithValue("$c", item.Context); cmd.Parameters.AddWithValue("$t", item.Tags);
        cmd.Parameters.AddWithValue("$n", item.Notes); cmd.Parameters.AddWithValue("$p", item.Phonetic);
        cmd.Parameters.AddWithValue("$id", item.Id); cmd.ExecuteNonQuery();
    }

    public void DeleteVocabulary(long id) => Execute("DELETE FROM vocabulary WHERE id=$id", ("$id", id));

    public bool AddClipboard(ClipboardEntry item, int limit)
    {
        try
        {
            using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText="SELECT content_hash FROM clipboard_items ORDER BY id DESC LIMIT 1";if((cmd.ExecuteScalar() as string)==item.Hash){tx.Rollback();return false;}cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO clipboard_items(kind,preview,text_value,html_value,rtf_value,image_png,is_favorite,created_at,content_hash) VALUES($k,$p,$t,$h,$r,$i,$f,$d,$x)";
            cmd.Parameters.AddWithValue("$k", item.Kind); cmd.Parameters.AddWithValue("$p", item.Preview);
            cmd.Parameters.AddWithValue("$t", (object?)item.Text ?? DBNull.Value); cmd.Parameters.AddWithValue("$h", (object?)item.Html ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)item.Rtf ?? DBNull.Value); cmd.Parameters.AddWithValue("$i", (object?)item.ImagePng ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$f", item.IsFavorite ? 1 : 0); cmd.Parameters.AddWithValue("$d", item.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$x", item.Hash); cmd.ExecuteNonQuery();
            cmd.Parameters.Clear(); cmd.CommandText = "DELETE FROM clipboard_items WHERE is_favorite=0 AND id NOT IN (SELECT id FROM clipboard_items ORDER BY id DESC LIMIT $limit)";
            cmd.Parameters.AddWithValue("$limit", Math.Max(50, limit)); cmd.ExecuteNonQuery(); tx.Commit(); return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return false; }
    }

    public IReadOnlyList<ClipboardEntry> SearchClipboard(string query = "", int count = 100)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
            cmd.CommandText = "SELECT id,kind,preview,text_value,html_value,rtf_value,image_png,is_favorite,created_at,content_hash FROM clipboard_items ORDER BY is_favorite DESC,id DESC LIMIT $n";
        else
        {
            cmd.CommandText = "SELECT c.id,c.kind,c.preview,c.text_value,c.html_value,c.rtf_value,c.image_png,c.is_favorite,c.created_at,c.content_hash FROM clipboard_fts f JOIN clipboard_items c ON c.id=f.rowid WHERE clipboard_fts MATCH $q ORDER BY c.is_favorite DESC,c.id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$q", string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(EscapeFts)));
        }
        cmd.Parameters.AddWithValue("$n", count); using var r = cmd.ExecuteReader(); var list = new List<ClipboardEntry>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), GetNullable(r,3), GetNullable(r,4), GetNullable(r,5), r.IsDBNull(6)?null:(byte[])r[6], r.GetInt64(7)!=0, DateTimeOffset.Parse(r.GetString(8)), r.GetString(9)));
        return list;
    }

    public long AddTodo(string text)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO todo_items(text_value,is_done,sort_order,created_at) " +
                          "VALUES($t,0,(SELECT COALESCE(MAX(sort_order),0)+1 FROM todo_items),$d); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", text.Trim()); cmd.Parameters.AddWithValue("$d", DateTimeOffset.Now.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<TodoItem> ListTodo()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,text_value,is_done,sort_order,created_at,completed_at FROM todo_items ORDER BY sort_order, id";
        using var r = cmd.ExecuteReader(); var list = new List<TodoItem>();
        while (r.Read())
            list.Add(new(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, (int)r.GetInt64(3),
                DateTimeOffset.Parse(r.GetString(4)), r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5))));
        return list;
    }

    public void SetTodoDone(long id, bool done) =>
        Execute("UPDATE todo_items SET is_done=$v, completed_at=$d WHERE id=$id",
            ("$v", done ? 1 : 0), ("$d", done ? DateTimeOffset.Now.ToString("O") : (object)DBNull.Value), ("$id", id));

    public void UpdateTodoText(long id, string text) =>
        Execute("UPDATE todo_items SET text_value=$t WHERE id=$id", ("$t", text.Trim()), ("$id", id));

    public void DeleteTodo(long id) => Execute("DELETE FROM todo_items WHERE id=$id", ("$id", id));

    public void ClearCompletedTodo() => Execute("DELETE FROM todo_items WHERE is_done<>0");

    /// <summary>整体重写 sort_order，而不是相邻交换：历史脏数据（重复或空缺的序号）不会让顺序错乱。</summary>
    public void ReorderTodo(IReadOnlyList<long> orderedIds)
    {
        if (orderedIds.Count == 0) return;
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE todo_items SET sort_order=$o WHERE id=$id";
        var order = cmd.Parameters.Add("$o", Microsoft.Data.Sqlite.SqliteType.Integer);
        var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        for (var i = 0; i < orderedIds.Count; i++) { order.Value = i + 1; id.Value = orderedIds[i]; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void SetClipboardFavorite(long id, bool value) => Execute("UPDATE clipboard_items SET is_favorite=$v WHERE id=$id", ("$v", value ? 1 : 0), ("$id", id));
    public void DeleteClipboard(long id) => Execute("DELETE FROM clipboard_items WHERE id=$id", ("$id", id));
    public void ClearClipboard() => Execute("DELETE FROM clipboard_items WHERE is_favorite=0");
    public long DatabaseSizeBytes => File.Exists(_file) ? new FileInfo(_file).Length : 0;

    private static string? GetNullable(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string EscapeFts(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value); cmd.ExecuteNonQuery();
    }
}
