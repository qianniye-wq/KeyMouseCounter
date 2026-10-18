using Microsoft.Data.Sqlite;
using KeyMouseCounter.Core;
using KeyMouseCounter.Diagnostics;

namespace KeyMouseCounter.Storage;

public readonly record struct LogCursor(long ByteOffset, int LineCount, long TotalKey, long TotalClick);

public sealed record DayRow(DateOnly Day, MinuteBuckets Total, string? FirstTs, string? LastTs, int UpdatedAtOffsetMin);

/// <summary>
/// SQLite 存储层（§6.2 DDL / §6.3 upsert / §6.6 重建）。
///
/// 连接策略：单连接 + 一把锁。写入只有刷盘线程，读取是 UI 线程，
/// 都是毫秒级；WAL + busy_timeout 负责多会话/多进程（§13 R9）。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly string _dbPath;
    private readonly object _gate = new();
    private SqliteConnection? _conn;

    public string DatabasePath => _dbPath;

    public SqliteStore(string dbPath)
    {
        _dbPath = dbPath;
        UserSid = ResolveUserSid();
    }

    /// <summary>数据按 user_sid 隔离（§2.3 通用边界规则 3）。</summary>
    public string UserSid { get; }

    public static string ResolveUserSid()
    {
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid)) return sid!;
        }
        catch { }
        return "unknown";
    }

    // ────────────────────────── 打开与 DDL ──────────────────────────

    public void Open()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString();

            _conn = new SqliteConnection(cs);
            _conn.Open();

            Exec("PRAGMA journal_mode = WAL;");
            Exec("PRAGMA synchronous  = NORMAL;");
            Exec("PRAGMA busy_timeout = 3000;");

            Migrate();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        // schema_meta 可能不存在（全新库）
        Exec(@"CREATE TABLE IF NOT EXISTS schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        int version = 0;
        using (var cmd = _conn!.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM schema_meta WHERE key='schema_version'";
            var v = cmd.ExecuteScalar() as string;
            if (v is not null) int.TryParse(v, out version);
        }

        CreateCoreTables();

        if (version == 0)
        {
            SetMeta("schema_version", SchemaVersion.ToString());
            SetMeta("created_at", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            Log.Info($"sqlite initialized schema v{SchemaVersion}");
        }
        else if (version < SchemaVersion)
        {
            // v1 → v2：minute_stat 是新增的；day_stat 数据保留为"无分钟明细的旧数据"
            SetMeta("migrated_from_v1", "1");
            SetMeta("schema_version", SchemaVersion.ToString());
            Log.Info($"sqlite migrated schema v{version} -> v{SchemaVersion}");
        }
        else if (version > SchemaVersion)
        {
            Log.Warn($"sqlite schema v{version} 高于本程序支持的 v{SchemaVersion}，按 v{SchemaVersion} 只读使用");
        }

        // v2.0 → v2.1：mouse_move_px 列不删除，仅停止写入并在查询层忽略（§6.6 步骤 1）
        SetMeta("app_version", AppPaths.Version);
    }

    private void CreateCoreTables()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS minute_stat (
                stat_date     TEXT    NOT NULL,
                minute_of_day INTEGER NOT NULL,
                user_sid      TEXT    NOT NULL,
                key_press     INTEGER NOT NULL DEFAULT 0,
                mouse_left    INTEGER NOT NULL DEFAULT 0,
                mouse_right   INTEGER NOT NULL DEFAULT 0,
                mouse_middle  INTEGER NOT NULL DEFAULT 0,
                wheel_up      INTEGER NOT NULL DEFAULT 0,
                wheel_down    INTEGER NOT NULL DEFAULT 0,
                wheel_left    INTEGER NOT NULL DEFAULT 0,
                wheel_right   INTEGER NOT NULL DEFAULT 0,
                active_seconds INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (stat_date, minute_of_day, user_sid)
            ) WITHOUT ROWID;
            """);

        Exec("CREATE INDEX IF NOT EXISTS ix_minute_date ON minute_stat(stat_date);");

        Exec("""
            CREATE TABLE IF NOT EXISTS day_stat (
                stat_date        TEXT    NOT NULL,
                user_sid         TEXT    NOT NULL,
                key_press        INTEGER NOT NULL DEFAULT 0,
                mouse_left       INTEGER NOT NULL DEFAULT 0,
                mouse_right      INTEGER NOT NULL DEFAULT 0,
                mouse_middle     INTEGER NOT NULL DEFAULT 0,
                wheel_up         INTEGER NOT NULL DEFAULT 0,
                wheel_down       INTEGER NOT NULL DEFAULT 0,
                wheel_left       INTEGER NOT NULL DEFAULT 0,
                wheel_right      INTEGER NOT NULL DEFAULT 0,
                active_seconds   INTEGER NOT NULL DEFAULT 0,
                first_ts         TEXT,
                last_ts          TEXT,
                utc_offset_min   INTEGER NOT NULL DEFAULT 0,
                updated_at       TEXT    NOT NULL,
                PRIMARY KEY (stat_date, user_sid)
            );
            """);

        Exec("""
            CREATE TABLE IF NOT EXISTS purge_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                purged_at TEXT NOT NULL,
                scope TEXT NOT NULL,
                from_date TEXT NOT NULL,
                to_date   TEXT NOT NULL
            );
            """);

        Exec("""
            CREATE TABLE IF NOT EXISTS log_gap (
                stat_date TEXT PRIMARY KEY,
                reason    TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """);

        Exec("""
            CREATE TABLE IF NOT EXISTS log_cursor (
                stat_date TEXT PRIMARY KEY,
                byte_offset INTEGER NOT NULL DEFAULT 0,
                line_count  INTEGER NOT NULL DEFAULT 0,
                total_key   INTEGER NOT NULL DEFAULT 0,
                total_click INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = "INSERT INTO schema_meta(key,value) VALUES(@k,@v) " +
                          "ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT value FROM schema_meta WHERE key=@k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public int IntegrityCheck()
    {
        lock (_gate)
        {
            try
            {
                using var cmd = _conn!.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                var v = cmd.ExecuteScalar() as string;
                return string.Equals(v, "ok", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            }
            catch
            {
                return 1;
            }
        }
    }

    // ────────────────────────── 写入 ──────────────────────────

    /// <summary>幂等增量合并写入：分钟明细 + 天汇总，同一事务（§6.3）。</summary>
    public void SaveBatch(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items,
                          int utcOffsetMin, string? firstTs, string? lastTs)
    {
        if (items.Count == 0) return;

        lock (_gate)
        {
            using var tx = _conn!.BeginTransaction();

            using (var cmd = UpsertMinuteCommand(tx))
                BindMinute(cmd, day, items);   // 内部按分钟逐条绑定并执行

            InsertOrAccumulateDay(tx, day, items, utcOffsetMin, firstTs, lastTs);
            tx.Commit();
        }
    }

    /// <summary>用指定内容整日替换（重建/清空用，幂等，§6.6）。</summary>
    public void ReplaceDay(DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items, int utcOffsetMin)
    {
        lock (_gate)
        {
            using var tx = _conn!.BeginTransaction();

            DeleteDayRows(tx, day);

            if (items.Count > 0)
            {
                using (var cmd = InsertMinuteCommand(tx))
                    BindMinute(cmd, day, items);   // 内部按分钟逐条绑定并执行

                var total = default(MinuteBuckets);
                int first = int.MaxValue, last = int.MinValue;
                foreach (var (m, b) in items)
                {
                    total.AddFrom(b);
                    if (m < first) first = m;
                    if (m > last) last = m;
                }
                InsertDayRow(tx, day, total, HhMm(first), HhMm(last), utcOffsetMin);
            }

            tx.Commit();
        }
    }

    private SqliteCommand UpsertMinuteCommand(SqliteTransaction tx)
    {
        var cmd = _conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO minute_stat (stat_date, minute_of_day, user_sid, key_press, mouse_left,
                    mouse_right, mouse_middle, wheel_up, wheel_down, wheel_left, wheel_right,
                    active_seconds)
            VALUES (@d,@m,@u,@k,@ml,@mr,@mm,@wu,@wd,@wl,@wr,@as)
            ON CONFLICT(stat_date, minute_of_day, user_sid) DO UPDATE SET
                key_press      = key_press      + excluded.key_press,
                mouse_left     = mouse_left     + excluded.mouse_left,
                mouse_right    = mouse_right    + excluded.mouse_right,
                mouse_middle   = mouse_middle   + excluded.mouse_middle,
                wheel_up       = wheel_up       + excluded.wheel_up,
                wheel_down     = wheel_down     + excluded.wheel_down,
                wheel_left     = wheel_left     + excluded.wheel_left,
                wheel_right    = wheel_right    + excluded.wheel_right,
                active_seconds = active_seconds + excluded.active_seconds;
            """;
        AddMinuteParams(cmd);
        return cmd;
    }

    private SqliteCommand InsertMinuteCommand(SqliteTransaction tx)
    {
        var cmd = _conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO minute_stat (stat_date, minute_of_day, user_sid, key_press, mouse_left,
                    mouse_right, mouse_middle, wheel_up, wheel_down, wheel_left, wheel_right,
                    active_seconds)
            VALUES (@d,@m,@u,@k,@ml,@mr,@mm,@wu,@wd,@wl,@wr,@as);
            """;
        AddMinuteParams(cmd);
        return cmd;
    }

    private void AddMinuteParams(SqliteCommand cmd)
    {
        cmd.Parameters.Add("@d", SqliteType.Text);
        cmd.Parameters.Add("@m", SqliteType.Integer);
        cmd.Parameters.Add("@u", SqliteType.Text);
        cmd.Parameters.Add("@k", SqliteType.Integer);
        cmd.Parameters.Add("@ml", SqliteType.Integer);
        cmd.Parameters.Add("@mr", SqliteType.Integer);
        cmd.Parameters.Add("@mm", SqliteType.Integer);
        cmd.Parameters.Add("@wu", SqliteType.Integer);
        cmd.Parameters.Add("@wd", SqliteType.Integer);
        cmd.Parameters.Add("@wl", SqliteType.Integer);
        cmd.Parameters.Add("@wr", SqliteType.Integer);
        cmd.Parameters.Add("@as", SqliteType.Integer);
    }

    private void BindMinute(SqliteCommand cmd, DateOnly day, IReadOnlyList<(int Minute, MinuteBuckets Data)> items)
    {
        var date = DateOnly2(day);
        foreach (var (minute, b) in items)
        {
            cmd.Parameters["@d"].Value = date;
            cmd.Parameters["@m"].Value = minute;
            cmd.Parameters["@u"].Value = UserSid;
            cmd.Parameters["@k"].Value = b.K;
            cmd.Parameters["@ml"].Value = b.ML;
            cmd.Parameters["@mr"].Value = b.MR;
            cmd.Parameters["@mm"].Value = b.MM;
            cmd.Parameters["@wu"].Value = b.WU;
            cmd.Parameters["@wd"].Value = b.WD;
            cmd.Parameters["@wl"].Value = b.WL;
            cmd.Parameters["@wr"].Value = b.WR;
            cmd.Parameters["@as"].Value = b.AS;
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertOrAccumulateDay(SqliteTransaction tx, DateOnly day,
        IReadOnlyList<(int Minute, MinuteBuckets Data)> items, int utcOffsetMin, string? firstTs, string? lastTs)
    {
        var total = default(MinuteBuckets);
        int first = int.MaxValue, last = int.MinValue;
        foreach (var (m, b) in items)
        {
            total.AddFrom(b);
            if (m < first) first = m;
            if (m > last) last = m;
        }

        using var cmd = _conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO day_stat (stat_date, user_sid, key_press, mouse_left, mouse_right, mouse_middle,
                    wheel_up, wheel_down, wheel_left, wheel_right, active_seconds,
                    first_ts, last_ts, utc_offset_min, updated_at)
            VALUES (@d,@u,@k,@ml,@mr,@mm,@wu,@wd,@wl,@wr,@as,@first,@last,@off,@upd)
            ON CONFLICT(stat_date, user_sid) DO UPDATE SET
                key_press      = key_press      + excluded.key_press,
                mouse_left     = mouse_left     + excluded.mouse_left,
                mouse_right    = mouse_right    + excluded.mouse_right,
                mouse_middle   = mouse_middle   + excluded.mouse_middle,
                wheel_up       = wheel_up       + excluded.wheel_up,
                wheel_down     = wheel_down     + excluded.wheel_down,
                wheel_left     = wheel_left     + excluded.wheel_left,
                wheel_right    = wheel_right    + excluded.wheel_right,
                active_seconds = active_seconds + excluded.active_seconds,
                first_ts       = COALESCE(day_stat.first_ts, excluded.first_ts),
                last_ts        = excluded.last_ts,
                updated_at     = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@d", DateOnly2(day));
        cmd.Parameters.AddWithValue("@u", UserSid);
        cmd.Parameters.AddWithValue("@k", total.K);
        cmd.Parameters.AddWithValue("@ml", total.ML);
        cmd.Parameters.AddWithValue("@mr", total.MR);
        cmd.Parameters.AddWithValue("@mm", total.MM);
        cmd.Parameters.AddWithValue("@wu", total.WU);
        cmd.Parameters.AddWithValue("@wd", total.WD);
        cmd.Parameters.AddWithValue("@wl", total.WL);
        cmd.Parameters.AddWithValue("@wr", total.WR);
        cmd.Parameters.AddWithValue("@as", total.AS);
        cmd.Parameters.AddWithValue("@first", (object?)firstTs ?? HhMm(first));
        cmd.Parameters.AddWithValue("@last", (object?)lastTs ?? HhMm(last));
        cmd.Parameters.AddWithValue("@off", utcOffsetMin);
        cmd.Parameters.AddWithValue("@upd", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        cmd.ExecuteNonQuery();
    }

    private void InsertDayRow(SqliteTransaction tx, DateOnly day, in MinuteBuckets total,
                              string? firstTs, string? lastTs, int utcOffsetMin)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO day_stat (stat_date, user_sid, key_press, mouse_left, mouse_right, mouse_middle,
                    wheel_up, wheel_down, wheel_left, wheel_right, active_seconds,
                    first_ts, last_ts, utc_offset_min, updated_at)
            VALUES (@d,@u,@k,@ml,@mr,@mm,@wu,@wd,@wl,@wr,@as,@first,@last,@off,@upd)
            ON CONFLICT(stat_date, user_sid) DO UPDATE SET
                key_press=excluded.key_press, mouse_left=excluded.mouse_left,
                mouse_right=excluded.mouse_right, mouse_middle=excluded.mouse_middle,
                wheel_up=excluded.wheel_up, wheel_down=excluded.wheel_down,
                wheel_left=excluded.wheel_left, wheel_right=excluded.wheel_right,
                active_seconds=excluded.active_seconds,
                first_ts=excluded.first_ts, last_ts=excluded.last_ts,
                utc_offset_min=excluded.utc_offset_min, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@d", DateOnly2(day));
        cmd.Parameters.AddWithValue("@u", UserSid);
        cmd.Parameters.AddWithValue("@k", total.K);
        cmd.Parameters.AddWithValue("@ml", total.ML);
        cmd.Parameters.AddWithValue("@mr", total.MR);
        cmd.Parameters.AddWithValue("@mm", total.MM);
        cmd.Parameters.AddWithValue("@wu", total.WU);
        cmd.Parameters.AddWithValue("@wd", total.WD);
        cmd.Parameters.AddWithValue("@wl", total.WL);
        cmd.Parameters.AddWithValue("@wr", total.WR);
        cmd.Parameters.AddWithValue("@as", total.AS);
        cmd.Parameters.AddWithValue("@first", (object?)firstTs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last", (object?)lastTs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@off", utcOffsetMin);
        cmd.Parameters.AddWithValue("@upd", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
        cmd.ExecuteNonQuery();
    }

    // ────────────────────────── 查询 ──────────────────────────

    public long CountMinuteRows(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM minute_stat WHERE stat_date=@d AND user_sid=@u";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@u", UserSid);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public bool HasDayRow(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM day_stat WHERE stat_date=@d AND user_sid=@u LIMIT 1";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@u", UserSid);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public DayRow? GetDay(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT key_press, mouse_left, mouse_right, mouse_middle, wheel_up, wheel_down,
                       wheel_left, wheel_right, active_seconds, first_ts, last_ts, utc_offset_min
                FROM day_stat WHERE stat_date=@d AND user_sid=@u;
                """;
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@u", UserSid);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var total = ReadBuckets(r, 0);
            return new DayRow(day, total,
                r.IsDBNull(9) ? null : r.GetString(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? 0 : r.GetInt32(11));
        }
    }

    public List<DayRow> GetDays(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT stat_date, key_press, mouse_left, mouse_right, mouse_middle, wheel_up, wheel_down,
                       wheel_left, wheel_right, active_seconds, first_ts, last_ts, utc_offset_min
                FROM day_stat WHERE user_sid=@u AND stat_date >= @f AND stat_date <= @t
                ORDER BY stat_date;
                """;
            cmd.Parameters.AddWithValue("@u", UserSid);
            cmd.Parameters.AddWithValue("@f", DateOnly2(from));
            cmd.Parameters.AddWithValue("@t", DateOnly2(to));
            var list = new List<DayRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var d = DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd");
                list.Add(new DayRow(d, ReadBuckets(r, 1),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetString(11),
                    r.IsDBNull(12) ? 0 : r.GetInt32(12)));
            }
            return list;
        }
    }

    public List<DateOnly> GetDayDates()
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT stat_date FROM day_stat WHERE user_sid=@u ORDER BY stat_date;";
            cmd.Parameters.AddWithValue("@u", UserSid);
            var list = new List<DateOnly>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd"));
            return list;
        }
    }

    /// <summary>某天的小时聚合（由 minute_stat 用 minute_of_day/60 得到，§6.2 注）。</summary>
    public List<HourPoint> GetHours(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT minute_of_day / 60 AS h,
                       SUM(key_press), SUM(mouse_left + mouse_right + mouse_middle),
                       SUM(wheel_up + wheel_down + wheel_left + wheel_right),
                       SUM(active_seconds)
                FROM minute_stat WHERE stat_date=@d AND user_sid=@u
                GROUP BY h ORDER BY h;
                """;
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@u", UserSid);
            var map = new HourPoint[24];
            for (int i = 0; i < 24; i++) map[i] = new HourPoint(i, 0, 0, 0, 0);
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    int h = r.GetInt32(0);
                    if (h is < 0 or > 23) continue;
                    map[h] = new HourPoint(h, r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
                }
            }
            return map.ToList();
        }
    }

    /// <summary>某天的分钟明细（只返回非零分钟）。</summary>
    public List<MinutePoint> GetMinutes(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT minute_of_day, key_press, mouse_left + mouse_right + mouse_middle,
                       wheel_up + wheel_down + wheel_left + wheel_right, active_seconds
                FROM minute_stat WHERE stat_date=@d AND user_sid=@u ORDER BY minute_of_day;
                """;
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@u", UserSid);
            var list = new List<MinutePoint>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new MinutePoint(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
            return list;
        }
    }

    /// <summary>多日 × 小时矩阵数据：Row=小时，Col=日期在 [from,to] 中的序号。</summary>
    public List<HeatCell> GetHeat(DateOnly from, DateOnly to, Metric metric = Metric.Key)
    {
        string col = MetricColumn(metric);
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"""
                SELECT stat_date, minute_of_day / 60 AS h, SUM({col})
                FROM minute_stat WHERE user_sid=@u AND stat_date >= @f AND stat_date <= @t
                GROUP BY stat_date, h;
                """;
            cmd.Parameters.AddWithValue("@u", UserSid);
            cmd.Parameters.AddWithValue("@f", DateOnly2(from));
            cmd.Parameters.AddWithValue("@t", DateOnly2(to));
            var list = new List<HeatCell>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var d = DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd");
                int col2 = d.DayNumber - from.DayNumber;
                int hour = r.GetInt32(1);
                if (col2 < 0) continue;
                list.Add(new HeatCell(hour, col2, r.GetInt64(2)));
            }
            return list;
        }
    }

    /// <summary>指标 → SQL 列表达式。只来自枚举，不含外部输入。</summary>
    public static string MetricColumn(Metric metric) => metric switch
    {
        Metric.Key => "key_press",
        Metric.Click => "(mouse_left + mouse_right + mouse_middle)",
        Metric.Wheel => "(wheel_up + wheel_down + wheel_left + wheel_right)",
        _ => "active_seconds"
    };

    // ────────────────────────── 删除 ──────────────────────────

    private void DeleteDayRows(SqliteTransaction tx, DateOnly day)
    {
        using var cmd = _conn!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM minute_stat WHERE stat_date=@d AND user_sid=@u;" +
                          "DELETE FROM day_stat WHERE stat_date=@d AND user_sid=@u;";
        cmd.Parameters.AddWithValue("@d", DateOnly2(day));
        cmd.Parameters.AddWithValue("@u", UserSid);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDay(DateOnly day)
    {
        lock (_gate)
        {
            using var tx = _conn!.BeginTransaction();
            DeleteDayRows(tx, day);
            tx.Commit();
        }
    }

    public int DeleteRange(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText =
                "DELETE FROM minute_stat WHERE user_sid=@u AND stat_date>=@f AND stat_date<=@t;" +
                "DELETE FROM day_stat WHERE user_sid=@u AND stat_date>=@f AND stat_date<=@t;";
            cmd.Parameters.AddWithValue("@u", UserSid);
            cmd.Parameters.AddWithValue("@f", DateOnly2(from));
            cmd.Parameters.AddWithValue("@t", DateOnly2(to));
            return cmd.ExecuteNonQuery();
        }
    }

    public void DeleteAll()
    {
        lock (_gate)
        {
            Exec("DELETE FROM minute_stat; DELETE FROM day_stat; DELETE FROM log_cursor;");
        }
    }

    // ────────────────────────── log_gap / log_cursor / purge_log ──────────────────────────

    public bool HasLogGap(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM log_gap WHERE stat_date=@d LIMIT 1";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void AddLogGap(DateOnly day, string reason)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO log_gap(stat_date, reason, created_at) VALUES(@d,@r,@c) " +
                              "ON CONFLICT(stat_date) DO UPDATE SET reason=excluded.reason, created_at=excluded.created_at";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@r", reason);
            cmd.Parameters.AddWithValue("@c", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveLogGap(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM log_gap WHERE stat_date=@d";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.ExecuteNonQuery();
        }
    }

    public LogCursor? GetCursor(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT byte_offset, line_count, total_key, total_click FROM log_cursor WHERE stat_date=@d";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new LogCursor(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3));
        }
    }

    public void SetCursor(DateOnly day, LogCursor cursor)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO log_cursor(stat_date, byte_offset, line_count, total_key, total_click)
                VALUES(@d,@o,@l,@k,@c)
                ON CONFLICT(stat_date) DO UPDATE SET
                    byte_offset=excluded.byte_offset, line_count=excluded.line_count,
                    total_key=excluded.total_key, total_click=excluded.total_click;
                """;
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.Parameters.AddWithValue("@o", cursor.ByteOffset);
            cmd.Parameters.AddWithValue("@l", cursor.LineCount);
            cmd.Parameters.AddWithValue("@k", cursor.TotalKey);
            cmd.Parameters.AddWithValue("@c", cursor.TotalClick);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteCursor(DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM log_cursor WHERE stat_date=@d";
            cmd.Parameters.AddWithValue("@d", DateOnly2(day));
            cmd.ExecuteNonQuery();
        }
    }

    public void WritePurgeLog(string scope, DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "INSERT INTO purge_log(purged_at, scope, from_date, to_date) VALUES(@p,@s,@f,@t)";
            cmd.Parameters.AddWithValue("@p", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"));
            cmd.Parameters.AddWithValue("@s", scope);
            cmd.Parameters.AddWithValue("@f", DateOnly2(from));
            cmd.Parameters.AddWithValue("@t", DateOnly2(to));
            cmd.ExecuteNonQuery();
        }
    }

    // ────────────────────────── 工具 ──────────────────────────

    private static string DateOnly2(DateOnly d) => d.ToString("yyyy-MM-dd");

    private static string HhMm(int minute) => minute < 0 ? "" : $"{minute / 60:00}:{minute % 60:00}";

    private static MinuteBuckets ReadBuckets(SqliteDataReader r, int offset) => MinuteBuckets.From(
        r.GetInt64(offset + 0), r.GetInt64(offset + 1), r.GetInt64(offset + 2), r.GetInt64(offset + 3),
        r.GetInt64(offset + 4), r.GetInt64(offset + 5), r.GetInt64(offset + 6), r.GetInt64(offset + 7),
        r.GetInt64(offset + 8));

    /// <summary>WAL checkpoint，退出前调用，避免 -wal 文件堆积。</summary>
    public void Checkpoint()
    {
        lock (_gate)
        {
            try { Exec("PRAGMA wal_checkpoint(TRUNCATE);"); } catch (Exception ex) { Log.Warn($"checkpoint failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _conn?.Dispose(); } catch { }
            _conn = null;
        }
        SqliteConnection.ClearAllPools();
    }
}
