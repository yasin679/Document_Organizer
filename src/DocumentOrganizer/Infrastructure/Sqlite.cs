using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DocumentOrganizer.Infrastructure;

/// <summary>
/// Minimal SQLite access through the operating system's SQLite library.
/// Windows 10/11 and Windows Server 2016+ ship winsqlite3.dll, so no package is needed.
/// On Linux/macOS the system libsqlite3 is used.
/// </summary>
internal static class SqliteNative
{
    public const string Lib = "sqlite3";
    public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    public const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;
    public const int OPEN_READWRITE = 0x2, OPEN_CREATE = 0x4, OPEN_FULLMUTEX = 0x10000;
    public static readonly IntPtr TRANSIENT = new(-1);

    private static int _registered;

    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(SqliteNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? new[] { "winsqlite3", "e_sqlite3", "sqlite3" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libsqlite3.dylib", "libsqlite3" }
                : new[] { "libsqlite3.so.0", "libsqlite3.so", "libsqlite3" };
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, asm, path, out var h)) return h;
        throw new DllNotFoundException("SQLite library not found. On Windows, winsqlite3.dll is part of Windows 10 and later.");
    }

    [DllImport(Lib)] public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nBytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport(Lib)] public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
    [DllImport(Lib)] public static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] val, int n, IntPtr destructor);
    [DllImport(Lib)] public static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long val);
    [DllImport(Lib)] public static extern int sqlite3_bind_double(IntPtr stmt, int idx, double val);
    [DllImport(Lib)] public static extern int sqlite3_bind_null(IntPtr stmt, int idx);
    [DllImport(Lib)] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_name(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_type(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern double sqlite3_column_double(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern int sqlite3_column_bytes(IntPtr stmt, int col);
    [DllImport(Lib)] public static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport(Lib)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib)] public static extern long sqlite3_last_insert_rowid(IntPtr db);

    public static byte[] Utf8Z(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Array.Resize(ref b, b.Length + 1);
        return b;
    }

    public static string? PtrToString(IntPtr p, int len = -1)
    {
        if (p == IntPtr.Zero) return null;
        return len >= 0 ? Marshal.PtrToStringUTF8(p, len) : Marshal.PtrToStringUTF8(p);
    }
}

public sealed class SqliteException(string message) : Exception(message);

/// <summary>A row returned from a query: column name → value (long, double, string or null).</summary>
public sealed class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.OrdinalIgnoreCase) { }
    public string? Str(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    public long Long(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : 0;
    public long? LongN(string k) => TryGetValue(k, out var v) && v != null ? Convert.ToInt64(v) : null;
}

/// <summary>
/// Thread-safe SQLite database. All access goes through one connection guarded by a lock,
/// which is simple and safe for this workload (writes are batched in transactions).
/// </summary>
public sealed class SqliteDb : IDisposable
{
    private IntPtr _db;
    private readonly object _gate = new();
    public object Gate => _gate;

    public SqliteDb(string path)
    {
        SqliteNative.EnsureResolver();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        int rc = SqliteNative.sqlite3_open_v2(SqliteNative.Utf8Z(path), out _db,
            SqliteNative.OPEN_READWRITE | SqliteNative.OPEN_CREATE | SqliteNative.OPEN_FULLMUTEX, IntPtr.Zero);
        if (rc != SqliteNative.SQLITE_OK) throw new SqliteException($"Cannot open database '{path}' (code {rc}).");
        SqliteNative.sqlite3_busy_timeout(_db, 10000);
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA foreign_keys=ON;");
    }

    private string Err() => SqliteNative.PtrToString(SqliteNative.sqlite3_errmsg(_db)) ?? "unknown error";

    private IntPtr Prepare(string sql)
    {
        int rc = SqliteNative.sqlite3_prepare_v2(_db, SqliteNative.Utf8Z(sql), -1, out var stmt, IntPtr.Zero);
        if (rc != SqliteNative.SQLITE_OK) throw new SqliteException($"{Err()} in SQL: {sql}");
        return stmt;
    }

    private static void Bind(IntPtr stmt, (string name, object? value)[] args)
    {
        foreach (var (name, value) in args)
        {
            int idx = SqliteNative.sqlite3_bind_parameter_index(stmt, SqliteNative.Utf8Z(name.StartsWith('@') ? name : "@" + name));
            if (idx == 0) continue;
            switch (value)
            {
                case null: SqliteNative.sqlite3_bind_null(stmt, idx); break;
                case bool b: SqliteNative.sqlite3_bind_int64(stmt, idx, b ? 1 : 0); break;
                case int i: SqliteNative.sqlite3_bind_int64(stmt, idx, i); break;
                case long l: SqliteNative.sqlite3_bind_int64(stmt, idx, l); break;
                case double d: SqliteNative.sqlite3_bind_double(stmt, idx, d); break;
                case DateTime dt: BindText(stmt, idx, dt.ToUniversalTime().ToString("o")); break;
                default: BindText(stmt, idx, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
            }
        }
    }

    private static void BindText(IntPtr stmt, int idx, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        SqliteNative.sqlite3_bind_text(stmt, idx, bytes, bytes.Length, SqliteNative.TRANSIENT);
    }

    public int Execute(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql);
            try
            {
                Bind(stmt, args);
                int rc;
                while ((rc = SqliteNative.sqlite3_step(stmt)) == SqliteNative.SQLITE_ROW) { }
                if (rc != SqliteNative.SQLITE_DONE) throw new SqliteException($"{Err()} in SQL: {sql}");
                return SqliteNative.sqlite3_changes(_db);
            }
            finally { SqliteNative.sqlite3_finalize(stmt); }
        }
    }

    public long Insert(string sql, params (string, object?)[] args)
    {
        lock (_gate) { Execute(sql, args); return SqliteNative.sqlite3_last_insert_rowid(_db); }
    }

    public List<Row> Query(string sql, params (string, object?)[] args)
    {
        var list = new List<Row>();
        QueryEach(sql, r => list.Add(r), args);
        return list;
    }

    public void QueryEach(string sql, Action<Row> onRow, params (string, object?)[] args)
    {
        lock (_gate)
        {
            var stmt = Prepare(sql);
            try
            {
                Bind(stmt, args);
                int cols = SqliteNative.sqlite3_column_count(stmt);
                var names = new string[cols];
                for (int i = 0; i < cols; i++) names[i] = SqliteNative.PtrToString(SqliteNative.sqlite3_column_name(stmt, i)) ?? $"c{i}";
                int rc;
                while ((rc = SqliteNative.sqlite3_step(stmt)) == SqliteNative.SQLITE_ROW)
                {
                    var row = new Row();
                    for (int i = 0; i < cols; i++)
                    {
                        row[names[i]] = SqliteNative.sqlite3_column_type(stmt, i) switch
                        {
                            SqliteNative.SQLITE_INTEGER => SqliteNative.sqlite3_column_int64(stmt, i),
                            SqliteNative.SQLITE_FLOAT => SqliteNative.sqlite3_column_double(stmt, i),
                            SqliteNative.SQLITE_NULL => null,
                            _ => SqliteNative.PtrToString(SqliteNative.sqlite3_column_text(stmt, i), SqliteNative.sqlite3_column_bytes(stmt, i))
                        };
                    }
                    onRow(row);
                }
                if (rc != SqliteNative.SQLITE_DONE) throw new SqliteException($"{Err()} in SQL: {sql}");
            }
            finally { SqliteNative.sqlite3_finalize(stmt); }
        }
    }

    public Row? QueryOne(string sql, params (string, object?)[] args) => Query(sql, args).FirstOrDefault();

    public object? Scalar(string sql, params (string, object?)[] args)
    {
        var r = QueryOne(sql, args);
        return r?.Values.FirstOrDefault();
    }

    /// <summary>Runs work inside one transaction while holding the database lock.</summary>
    public void InTransaction(Action work)
    {
        lock (_gate)
        {
            Execute("BEGIN IMMEDIATE;");
            try { work(); Execute("COMMIT;"); }
            catch { try { Execute("ROLLBACK;"); } catch { } throw; }
        }
    }

    /// <summary>Prepared statement reused for many rows (fast bulk inserts). Use inside InTransaction.</summary>
    public sealed class Prepared : IDisposable
    {
        private readonly SqliteDb _owner; private IntPtr _stmt;
        internal Prepared(SqliteDb owner, string sql) { _owner = owner; _stmt = owner.Prepare(sql); }
        public void Run(params (string, object?)[] args)
        {
            SqliteNative.sqlite3_reset(_stmt);
            SqliteNative.sqlite3_clear_bindings(_stmt);
            Bind(_stmt, args);
            int rc = SqliteNative.sqlite3_step(_stmt);
            if (rc != SqliteNative.SQLITE_DONE && rc != SqliteNative.SQLITE_ROW) throw new SqliteException(_owner.Err());
        }
        public void Dispose() { if (_stmt != IntPtr.Zero) { SqliteNative.sqlite3_finalize(_stmt); _stmt = IntPtr.Zero; } }
    }

    public Prepared Prepare2(string sql) { lock (_gate) return new Prepared(this, sql); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db != IntPtr.Zero) { SqliteNative.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        }
    }
}
