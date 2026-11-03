using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalDiskBrowser.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace LocalDiskBrowser.Controllers
{
    // Conn = the key of a saved connection (see SqlConnStore). It is only a reference, never the connection string.
    public record SqlQueryRequest(string Sql, string? Conn = null);
    public record SqlObjectsRequest(string Kind, string? Conn = null);
    public record SqlDefRequest(string Kind, string Name, string? Conn = null);
    public record SqlConnRequest(string? Conn = null);
    // Encrypted in the browser: AES-256-GCM payload + its key wrapped with the server's RSA public key.
    public record SqlSaveRequest(string KeyId, string WrappedKey, string Iv, string Ciphertext, string? Conn = null);
    public record SqlSavePayload(string? ConnectionString, string? Label, long Ts, string? Nonce);

    // SQL Server editor API. The user enters a connection string once; the browser encrypts it before sending (api/sql/save),
    // the server tests it and stores it encrypted at rest under a random key. From then on the browser sends only that key.
    // The connection string is never returned or logged (the page gets server / ip / database, never the password).
    [ApiController]
    [Route("api/sql")]
    public class SqlApiController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly SqlConnStore _store;
        private static readonly object AuditLock = new();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public SqlApiController(IConfiguration config, IMemoryCache cache, SqlConnStore store) { _config = config; _cache = cache; _store = store; }

        // Set by Use() for the current request.
        private string? _cs, _key;
        private SqlConnRecord? _rec;
        private string? ConnStr => _cs;
        private int Timeout => _config.GetValue<int?>("SqlEditor:CommandTimeoutSeconds") ?? 120;
        private int DisplayRows => _config.GetValue<int?>("SqlEditor:DisplayRows") ?? 5000;
        private int ExportRows => _config.GetValue<int?>("SqlEditor:ExportRows") ?? 200000;

        private class SqlSet
        {
            public List<string> Columns = new();
            public List<string> Types = new();
            public List<object?[]> Rows = new();
            public long Total;
        }

        // ---------- connection info (no password) ----------

        [HttpGet("info")]
        public async Task<IActionResult> Info(string? conn)
        {
            if (Use(conn) != null) return Ok(new { configured = false });
            try
            {
                var b = new SqlConnectionStringBuilder(ConnStr);
                return Ok(new
                {
                    configured = true,
                    server = b.DataSource,
                    ip = await ResolveIp(b.DataSource),
                    database = b.InitialCatalog,
                    login = b.IntegratedSecurity ? "Windows account of the app" : "SQL login " + b.UserID
                });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        [HttpPost("connect")]
        public async Task<IActionResult> Connect(SqlConnRequest req, CancellationToken ct)
        {
            if (Use(req.Conn) is { } nc) return nc;
            // Warn only: the connection was last used from another IP. The new IP becomes the baseline, so it warns once per change.
            var ip = ClientIp();
            var changed = !string.IsNullOrEmpty(_rec!.Ip) && _rec.Ip != ip;
            var previous = _rec.Ip;
            if (changed) { Audit("ip-changed", $"{previous} -> {ip}"); _store.SetIp(_key!, _rec, ip); }
            try
            {
                await using var conn = await OpenAsync(ct);
                return Ok(new { version = conn.ServerVersion, database = conn.Database, server = conn.DataSource, ipChanged = changed, previousIp = previous, clientIp = ip });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ---------- saved connections ----------

        // The browser encrypts the connection string with this public key (see save).
        [HttpGet("pubkey")]
        public IActionResult PubKey()
        {
            var (id, spki, fp) = _store.PublicKey();
            return Ok(new { keyId = id, publicKey = spki, fingerprint = fp, user = User.Identity?.Name ?? "" });
        }

        [HttpPost("save")]
        public async Task<IActionResult> Save(SqlSaveRequest req, CancellationToken ct)
        {
            var user = User.Identity?.Name ?? "";
            SqlSavePayload? p;
            try
            {
                if (req.KeyId != _store.PublicKey().KeyId) return BadRequest(new { error = "The encryption key changed. Reload the page and try again." });
                var aesKey = _store.UnwrapKey(Convert.FromBase64String(req.WrappedKey));
                var iv = Convert.FromBase64String(req.Iv);
                var all = Convert.FromBase64String(req.Ciphertext);
                if (aesKey.Length != 32 || iv.Length != 12 || all.Length < 17) throw new CryptographicException();
                var plain = new byte[all.Length - 16];
                // The user name is authenticated data: a captured request cannot be replayed under another login.
                using (var gcm = new AesGcm(aesKey, 16)) gcm.Decrypt(iv, all.AsSpan(0, plain.Length), all.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(user));
                p = JsonSerializer.Deserialize<SqlSavePayload>(plain, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or JsonException)
            { return BadRequest(new { error = "Could not decrypt the request. Reload the page and try again." }); }

            if (p == null || string.IsNullOrWhiteSpace(p.ConnectionString) || string.IsNullOrWhiteSpace(p.Nonce) || p.Nonce.Length is < 16 or > 64)
                return BadRequest(new { error = "Invalid request." });
            if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.Ts) > 120_000)
                return BadRequest(new { error = "The request expired (check the clock of this computer) - try again." });
            if (_cache.TryGetValue("sqlnonce:" + p.Nonce, out _)) return BadRequest(new { error = "Request already used." });
            _cache.Set("sqlnonce:" + p.Nonce, true, TimeSpan.FromMinutes(5));

            string key;
            if (!string.IsNullOrWhiteSpace(req.Conn))
            {
                if (_store.Load(req.Conn, user) == null) return BadRequest(new { error = "Unknown connection. Add it again." });
                key = req.Conn; // update: same key, new connection string
            }
            else key = SqlConnStore.NewKey();

            SqlConnectionStringBuilder b;
            try { b = new SqlConnectionStringBuilder(p.ConnectionString); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return BadRequest(new { error = "Invalid connection string: " + ex.Message }); }
            if (string.IsNullOrWhiteSpace(b.DataSource)) return BadRequest(new { error = "The connection string has no Server / Data Source." });
            if (!string.IsNullOrEmpty(b.AttachDBFilename)) return BadRequest(new { error = "AttachDbFilename is not allowed." });
            var allowed = _config.GetSection("SqlEditor:AllowedServers").Get<string[]>() ?? Array.Empty<string>();
            if (allowed.Length > 0 && !allowed.Contains("*") && !allowed.Contains(HostOf(b.DataSource), StringComparer.OrdinalIgnoreCase))
                return BadRequest(new { error = $"Server '{HostOf(b.DataSource)}' is not in SqlEditor:AllowedServers." });
            if (b.ConnectTimeout == 0 || b.ConnectTimeout > 30) b.ConnectTimeout = 30;

            try { await using var test = new SqlConnection(b.ConnectionString); await test.OpenAsync(ct); }
            catch (Exception ex) { return Fail(ex); }

            var label = string.IsNullOrWhiteSpace(p.Label) ? b.InitialCatalog : p.Label.Trim();
            if (label.Length > 60) label = label[..60];
            _cs = b.ConnectionString;
            _store.Save(key, user, ClientIp(), label, _cs);
            Audit("connection-saved", label); // never the connection string
            return Ok(new
            {
                key, label,
                server = b.DataSource, ip = await ResolveIp(b.DataSource), database = b.InitialCatalog,
                login = b.IntegratedSecurity ? "Windows account of the app" : "SQL login " + b.UserID
            });
        }

        [HttpPost("forget")]
        public IActionResult Forget(SqlConnRequest req) =>
            _store.Delete(req.Conn, User.Identity?.Name ?? "") ? Ok(new { ok = true }) : BadRequest(new { error = "Unknown connection." });

        // "tcp:host,1433" / "host\INSTANCE" -> "host"
        private static string HostOf(string dataSource)
        {
            var h = dataSource.Trim();
            var i = h.IndexOf(':'); if (i > 0 && i <= 3) h = h[(i + 1)..]; // tcp: / np: / lpc:
            return h.Split(',', '\\')[0].Trim();
        }

        // Real client address for the "used from another IP" warning. Behind Cloudflare it is in CF-Connecting-IP. The headers can
        // be forged by a client that reaches the app directly, which is acceptable because the check only warns.
        private string ClientIp()
        {
            var cf = Request.Headers["CF-Connecting-IP"].ToString();
            if (cf.Length > 0) return cf;
            var xff = Request.Headers["X-Forwarded-For"].ToString();
            if (xff.Length > 0) return xff.Split(',')[0].Trim();
            return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        }

        // "tcp:host,1433" / "host\INSTANCE" / "." -> the host part, then its IPv4 address (best effort, 2 s).
        private static async Task<string> ResolveIp(string dataSource)
        {
            var host = dataSource.Trim();
            if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            host = host.Split(',')[0].Split('\\')[0].Trim();
            if (host is "" or "." or "(local)") host = "localhost";
            if (IPAddress.TryParse(host, out var ip)) return ip.ToString();
            try
            {
                var task = Dns.GetHostAddressesAsync(host);
                if (await Task.WhenAny(task, Task.Delay(2000)) != task) return "";
                var v4 = task.Result.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return (v4 ?? task.Result.FirstOrDefault())?.ToString() ?? "";
            }
            catch { return ""; }
        }

        // ---------- run SQL ----------

        [HttpPost("query")]
        public async Task<IActionResult> Query(SqlQueryRequest req, CancellationToken ct)
        {
            if (Use(req.Conn) is { } nc) return nc;
            if (string.IsNullOrWhiteSpace(req.Sql)) return BadRequest(new { error = "Nothing to run." });
            Audit("query", req.Sql);

            var sw = Stopwatch.StartNew();
            var sets = new List<SqlSet>();
            var messages = new List<string>();
            try
            {
                await using var conn = await OpenAsync(ct, e => { foreach (SqlError er in e.Errors) messages.Add(er.Message); });
                foreach (var batch in SplitBatches(req.Sql))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = batch;
                    cmd.CommandTimeout = Timeout;
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    do
                    {
                        if (rd.FieldCount == 0) continue;
                        var set = new SqlSet();
                        for (int i = 0; i < rd.FieldCount; i++)
                        {
                            var n = rd.GetName(i);
                            set.Columns.Add(string.IsNullOrEmpty(n) ? "(No column name)" : n);
                            set.Types.Add(rd.GetDataTypeName(i));
                        }
                        while (await rd.ReadAsync(ct))
                        {
                            set.Total++;
                            if (set.Rows.Count >= ExportRows) continue; // keep counting, stop storing
                            var row = new object?[rd.FieldCount];
                            for (int i = 0; i < row.Length; i++) row[i] = Norm(rd.GetValue(i), set.Types[i]);
                            set.Rows.Add(row);
                        }
                        sets.Add(set);
                    } while (await rd.NextResultAsync(ct));
                    await rd.CloseAsync();
                    if (rd.RecordsAffected >= 0) messages.Add($"({rd.RecordsAffected} row(s) affected)");
                }
            }
            catch (Exception ex) { return Fail(ex); }

            // Keep the full result for the Excel / CSV download (one result per user, 15 minutes).
            var token = Guid.NewGuid().ToString("N");
            var userKey = "sqllast:" + (User.Identity?.Name ?? "");
            if (_cache.TryGetValue(userKey, out string? old)) _cache.Remove("sqlq:" + old);
            var life = TimeSpan.FromMinutes(15);
            _cache.Set("sqlq:" + token, sets, life);
            _cache.Set(userKey, token, life);

            return Ok(new
            {
                token,
                elapsedMs = sw.ElapsedMilliseconds,
                messages,
                sets = sets.Select(s => new
                {
                    columns = s.Columns,
                    types = s.Types,
                    rows = s.Rows.Count > DisplayRows ? s.Rows.Take(DisplayRows) : s.Rows,
                    total = s.Total,
                    stored = s.Rows.Count,
                    shown = Math.Min(s.Rows.Count, DisplayRows)
                })
            });
        }

        // SSMS style: a line with only GO separates batches (needed e.g. before CREATE PROCEDURE).
        private static readonly Regex GoLine = new(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static List<string> SplitBatches(string sql)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            void Flush() { if (sb.ToString().Trim().Length > 0) list.Add(sb.ToString()); sb.Clear(); }
            foreach (var line in sql.Replace("\r\n", "\n").Split('\n'))
            {
                if (GoLine.IsMatch(line)) Flush();
                else sb.Append(line).Append('\n');
            }
            Flush();
            return list;
        }

        // JSON-safe cell values: no NaN/Infinity, no 64-bit numbers JavaScript would round, dates as text.
        private static object? Norm(object v, string typeName)
        {
            switch (v)
            {
                case DBNull: return null;
                case byte[] b: return "0x" + Convert.ToHexString(b.Length > 2000 ? b[..2000] : b) + (b.Length > 2000 ? "..." : "");
                case DateTime d: return d.ToString(typeName == "date" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.fff", Inv);
                case DateTimeOffset o: return o.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", Inv);
                case TimeSpan t: return t.ToString();
                case Guid g: return g.ToString();
                case long l when l > 9007199254740991L || l < -9007199254740991L: return l.ToString(Inv);
                case double dbl when !double.IsFinite(dbl): return dbl.ToString(Inv);
                case float f when !float.IsFinite(f): return f.ToString(Inv);
                case string or bool or byte or short or int or long or decimal or double or float: return v;
                default: return v.ToString();
            }
        }

        // ---------- object explorer ----------

        [HttpPost("objects")]
        public async Task<IActionResult> Objects(SqlObjectsRequest req, CancellationToken ct)
        {
            if (Use(req.Conn) is { } nc) return nc;
            string Named(string table, string where = "") =>
                $"SELECT s.name + '.' + o.name AS n FROM {table} o JOIN sys.schemas s ON s.schema_id = o.schema_id {where} ORDER BY s.name, o.name";
            var sql = req.Kind switch
            {
                "tables" => Named("sys.tables", "WHERE o.is_ms_shipped = 0"),
                "views" => Named("sys.views", "WHERE o.is_ms_shipped = 0"),
                "procs" => Named("sys.procedures", "WHERE o.is_ms_shipped = 0"),
                "functions" => Named("sys.objects", "WHERE o.type IN ('FN','IF','TF','FS','FT') AND o.is_ms_shipped = 0"),
                "tabletypes" => Named("sys.table_types", "WHERE o.is_user_defined = 1"),
                "jobs" => "SELECT name AS n FROM msdb.dbo.sysjobs ORDER BY name",
                _ => null
            };
            if (sql == null) return BadRequest(new { error = "Unknown object kind." });
            try
            {
                await using var conn = await OpenAsync(ct);
                var rows = await Rows(conn, sql, ct);
                return Ok(rows.Select(r => r["n"]));
            }
            catch (Exception ex) { return Fail(ex); }
        }

        [HttpPost("definition")]
        public async Task<IActionResult> Definition(SqlDefRequest req, CancellationToken ct)
        {
            if (Use(req.Conn) is { } nc) return nc;
            var (schema, name) = SplitName(req.Name);
            var full = "[" + schema.Replace("]", "]]") + "].[" + name.Replace("]", "]]") + "]";
            try
            {
                await using var conn = await OpenAsync(ct);
                switch (req.Kind)
                {
                    case "tables":
                    {
                        var cols = await Rows(conn,
                            @"SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.max_length, c.precision, c.scale,
                                     c.is_nullable AS IsNullable, c.is_identity AS IsIdentity, dc.definition AS DefaultValue
                              FROM sys.columns c
                              LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
                              WHERE c.object_id = OBJECT_ID(@full) ORDER BY c.column_id", ct, ("@full", full));
                        AddSizes(cols);
                        var pks = await Rows(conn,
                            @"SELECT kc.name AS ConstraintName, c.name AS ColumnName
                              FROM sys.key_constraints kc
                              JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE kc.parent_object_id = OBJECT_ID(@full) AND kc.type = 'PK' ORDER BY ic.key_ordinal", ct, ("@full", full));
                        var fks = await Rows(conn,
                            @"SELECT fk.name AS ConstraintName, pc.name AS ColumnName, rs.name + '.' + rt.name AS ReferencedTable, rc.name AS ReferencedColumn
                              FROM sys.foreign_keys fk
                              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                              JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id
                              JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                              WHERE fk.parent_object_id = OBJECT_ID(@full) ORDER BY fk.name, fkc.constraint_column_id", ct, ("@full", full));
                        var idx = await Rows(conn,
                            @"SELECT i.name AS IndexName, i.type_desc AS IndexType, i.is_unique AS IsUnique,
                                     STUFF((SELECT ', ' + c.name FROM sys.index_columns ic
                                            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                                            ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS Columns
                              FROM sys.indexes i WHERE i.object_id = OBJECT_ID(@full) AND i.index_id > 0 ORDER BY i.index_id", ct, ("@full", full));
                        return Ok(new { columns = cols, primaryKeys = pks, foreignKeys = fks, indexes = idx });
                    }
                    case "views":
                    case "procs":
                    case "functions":
                    {
                        var r = await Rows(conn, "SELECT OBJECT_DEFINITION(OBJECT_ID(@full)) AS t", ct, ("@full", full));
                        var text = r.Count > 0 ? r[0]["t"] as string : null;
                        return Ok(new { text = text ?? "-- No definition available (encrypted object, or no VIEW DEFINITION permission)." });
                    }
                    case "tabletypes":
                    {
                        var cols = await Rows(conn,
                            @"SELECT c.name AS ColumnName, TYPE_NAME(c.user_type_id) AS DataType, c.max_length, c.precision, c.scale, c.is_nullable AS IsNullable
                              FROM sys.table_types tt JOIN sys.schemas s ON s.schema_id = tt.schema_id
                              JOIN sys.columns c ON c.object_id = tt.type_table_object_id
                              WHERE s.name = @s AND tt.name = @n ORDER BY c.column_id", ct, ("@s", schema), ("@n", name));
                        AddSizes(cols);
                        var pks = await Rows(conn,
                            @"SELECT i.name AS ConstraintName, c.name AS ColumnName
                              FROM sys.table_types tt JOIN sys.schemas s ON s.schema_id = tt.schema_id
                              JOIN sys.indexes i ON i.object_id = tt.type_table_object_id AND i.is_primary_key = 1
                              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE s.name = @s AND tt.name = @n ORDER BY ic.key_ordinal", ct, ("@s", schema), ("@n", name));
                        return Ok(new { columns = cols, primaryKeys = pks });
                    }
                    case "jobs":
                    {
                        var jn = req.Name;
                        var info = await Rows(conn, "SELECT name AS Name, enabled AS Enabled, description AS Description FROM msdb.dbo.sysjobs WHERE name = @n", ct, ("@n", jn));
                        var steps = await Rows(conn,
                            @"SELECT st.step_id AS StepId, st.step_name AS StepName, st.subsystem AS Subsystem, st.command AS Command
                              FROM msdb.dbo.sysjobsteps st JOIN msdb.dbo.sysjobs j ON j.job_id = st.job_id WHERE j.name = @n ORDER BY st.step_id", ct, ("@n", jn));
                        var sch = await Rows(conn,
                            @"SELECT s.name AS ScheduleName, s.enabled AS Enabled,
                                     CASE s.freq_type WHEN 1 THEN 'Once' WHEN 4 THEN 'Daily' WHEN 8 THEN 'Weekly' WHEN 16 THEN 'Monthly'
                                          WHEN 32 THEN 'Monthly (relative)' WHEN 64 THEN 'When the Agent starts' WHEN 128 THEN 'When idle' ELSE '' END AS Frequency,
                                     STUFF(STUFF(RIGHT('000000' + CAST(s.active_start_time AS varchar(6)), 6), 3, 0, ':'), 6, 0, ':') AS StartTime
                              FROM msdb.dbo.sysjobschedules js
                              JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
                              JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id WHERE j.name = @n", ct, ("@n", jn));
                        return Ok(new { info = info.FirstOrDefault(), steps, schedules = sch });
                    }
                    default:
                        return BadRequest(new { error = "Unknown object kind." });
                }
            }
            catch (Exception ex) { return Fail(ex); }
        }

        private static (string schema, string name) SplitName(string full)
        {
            full = (full ?? "").Replace("[", "").Replace("]", "");
            var i = full.IndexOf('.');
            return i < 0 ? ("dbo", full) : (full[..i], full[(i + 1)..]);
        }

        // "Size" column of the definition tables: length, or precision/scale for numbers.
        private static void AddSizes(List<Dictionary<string, object?>> cols)
        {
            foreach (var c in cols)
            {
                var dt = (c["DataType"] as string ?? "").ToLowerInvariant();
                var max = Convert.ToInt32(c["max_length"] ?? 0, Inv);
                c["Size"] = dt switch
                {
                    "char" or "varchar" or "binary" or "varbinary" => max == -1 ? "MAX" : max.ToString(Inv),
                    "nchar" or "nvarchar" => max == -1 ? "MAX" : (max / 2).ToString(Inv),
                    "decimal" or "numeric" => $"{c["precision"]},{c["scale"]}",
                    "datetime2" or "time" or "datetimeoffset" => c["scale"]?.ToString() ?? "",
                    _ => ""
                };
            }
        }

        private async Task<List<Dictionary<string, object?>>> Rows(SqlConnection conn, string sql, CancellationToken ct, params (string, object)[] ps)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = Timeout;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<Dictionary<string, object?>>();
            while (await rd.ReadAsync(ct))
            {
                var d = new Dictionary<string, object?>();
                for (int i = 0; i < rd.FieldCount; i++) d[rd.GetName(i)] = Norm(rd.GetValue(i), rd.GetDataTypeName(i));
                list.Add(d);
            }
            return list;
        }

        // ---------- Excel / CSV download of the last result ----------

        [HttpGet("export")]
        public IActionResult Export(string token, int set, string format)
        {
            if (!_cache.TryGetValue("sqlq:" + token, out List<SqlSet>? sets) || sets == null || set < 0 || set >= sets.Count)
                return NotFound(new { error = "The result has expired. Run the query again." });
            var s = sets[set];
            var name = $"result{set + 1}-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (format == "csv")
                return File(Csv(s), "text/csv; charset=utf-8", name + ".csv");
            return File(Xlsx(s, "Result " + (set + 1)), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        }

        private static string CellText(object? v) => v switch
        {
            null => "",
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? ""
        };

        private static byte[] Csv(SqlSet s)
        {
            var sb = new StringBuilder();
            string Q(string t) => t.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
            sb.Append(string.Join(",", s.Columns.Select(Q))).Append("\r\n");
            foreach (var r in s.Rows) sb.Append(string.Join(",", r.Select(v => Q(CellText(v))))).Append("\r\n");
            return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(); // BOM so Excel reads UTF-8
        }

        private static string XmlText(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue; // not allowed in XML
                sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
            }
            return sb.ToString();
        }

        private static string ColName(int i) // 0 -> A, 26 -> AA
        {
            var s = "";
            for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
            return s;
        }

        // Minimal .xlsx written by hand (no extra library): one sheet, bold frozen header, numbers as numbers.
        private static byte[] Xlsx(SqlSet s, string sheetName)
        {
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Add(string path, string content)
                {
                    using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                    w.Write(content);
                }
                const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
                Add("[Content_Types].xml", head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                    + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                    + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                    + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                    + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
                Add("_rels/.rels", head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add("xl/workbook.xml", head + $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                    + $"<sheets><sheet name=\"{XmlText(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add("xl/_rels/workbook.xml.rels", head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                    + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                    + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Add("xl/styles.xml", head + $"<styleSheet xmlns=\"{ns}\">"
                    + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                    + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                    + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                    + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                    + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");

                using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(head + $"<worksheet xmlns=\"{ns}\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><sheetData>");
                w.Write("<row r=\"1\">");
                for (int c = 0; c < s.Columns.Count; c++)
                    w.Write($"<c r=\"{ColName(c)}1\" s=\"1\" t=\"inlineStr\"><is><t>{XmlText(s.Columns[c])}</t></is></c>");
                w.Write("</row>");
                int rn = 1;
                foreach (var row in s.Rows.Take(1048575)) // Excel's row limit minus the header
                {
                    rn++;
                    w.Write($"<row r=\"{rn}\">");
                    for (int c = 0; c < row.Length; c++)
                    {
                        var v = row[c];
                        var r = ColName(c) + rn;
                        switch (v)
                        {
                            case null: break;
                            case bool b: w.Write($"<c r=\"{r}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>"); break;
                            case byte or short or int or long or decimal or double or float: w.Write($"<c r=\"{r}\"><v>{CellText(v)}</v></c>"); break;
                            default: w.Write($"<c r=\"{r}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlText(CellText(v))}</t></is></c>"); break;
                        }
                    }
                    w.Write("</row>");
                }
                w.Write("</sheetData></worksheet>");
            }
            return ms.ToArray();
        }

        // ---------- helpers ----------

        // Resolves the saved connection for this request (key + logged-in user must match). Returns an error result, or null when ok.
        private IActionResult? Use(string? key)
        {
            var user = User.Identity?.Name ?? "";
            var fails = "sqlfail:" + user;
            if (_cache.TryGetValue(fails, out int n) && n >= 20)
                return StatusCode(429, new { error = "Too many unknown connection attempts. Wait a minute." });
            if (string.IsNullOrWhiteSpace(key))
                return BadRequest(new { error = "No database selected. Click Add database and enter a connection string." });
            var hit = _store.Load(key, user);
            if (hit == null)
            {
                _cache.Set(fails, n + 1, TimeSpan.FromMinutes(1));
                return BadRequest(new { error = "Unknown connection. Click Add database and enter the connection string again." });
            }
            (_rec, _cs) = (hit.Value.Rec, hit.Value.ConnectionString);
            _key = key;
            return null;
        }

        private async Task<SqlConnection> OpenAsync(CancellationToken ct, Action<SqlInfoMessageEventArgs>? onInfo = null)
        {
            var conn = new SqlConnection(ConnStr);
            if (onInfo != null) conn.InfoMessage += (_, e) => onInfo(e);
            try { await conn.OpenAsync(ct); }
            catch { await conn.DisposeAsync(); throw; }
            return conn;
        }

        private IActionResult Fail(Exception ex)
        {
            if (ex is OperationCanceledException) return StatusCode(499, new { error = "Cancelled." });
            var msg = ex is SqlException se
                ? string.Join("\n", se.Errors.Cast<SqlError>().Select(e => e.Message + (e.LineNumber > 0 && e.Class > 10 ? $" (line {e.LineNumber})" : "")))
                : ex.Message;
            return BadRequest(new { error = msg });
        }

        // Who ran what (never the connection string, never results). Best effort: the log folder may be read-only.
        private void Audit(string kind, string text)
        {
            try
            {
                var b = new SqlConnectionStringBuilder(ConnStr);
                var one = Regex.Replace(text, @"\s+", " ").Trim();
                if (one.Length > 2000) one = one[..2000] + "...";
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {User.Identity?.Name}@{ClientIp()} | {b.DataSource}/{b.InitialCatalog} | {kind} | {one}{Environment.NewLine}";
                var dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                lock (AuditLock) System.IO.File.AppendAllText(Path.Combine(dir, "sql-audit.log"), line);
            }
            catch { }
        }
    }
}
