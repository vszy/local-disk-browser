using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

namespace LocalDiskBrowser.Services
{
    public record ProfEvent(long Seq, string Time, string Type, double DurationMs, double CpuMs, long Reads, long Rows,
        string Database, string Login, string Host, string App, string Sql);

    // SQL profiler: one Extended Events session per profiler tab (ring buffer target, SQL 2012+, no sysadmin needed).
    // The profiler page polls it every few seconds. The session is dropped when the page says stop (also on tab close),
    // when the page stops polling for LeaseSeconds (crashed browser / lost network), at app shutdown, and at the next app
    // start for sessions a previous run left behind (they are listed in App_Data/profiler, protected, never in clear text).
    // Captured SQL text can contain sensitive literals: it is only returned to the user who started the session and is never logged.
    public class SqlProfilerService : BackgroundService
    {
        // A background browser tab can be throttled to one timer tick per minute, so the lease must be well above that.
        public const int LeaseSeconds = 120;
        public const int MaxSessions = 10;
        public const string AppName = "LDB-Profiler";
        private const int MaxPerPoll = 500;
        private const int MaxSqlChars = 4000;

        private class Sess
        {
            public string Id = "", Name = "", User = "", ConnKey = "", ConnStr = "";
            public DateTime LastSeen;
            public long LastSeq;
        }

        private readonly Dictionary<string, Sess> _sessions = new();
        private readonly object _lock = new();
        private readonly ILogger<SqlProfilerService> _log;
        private readonly SqlConnStore _store;
        private readonly IDataProtector _prot;
        private readonly string _dir;

        public SqlProfilerService(IWebHostEnvironment env, IDataProtectionProvider dp, SqlConnStore store, ILogger<SqlProfilerService> log)
        {
            _log = log; _store = store;
            _prot = dp.CreateProtector("LocalDiskBrowser.SqlProfiler.v1");
            _dir = Path.Combine(env.ContentRootPath, "App_Data", "profiler");
            Directory.CreateDirectory(_dir);
        }

        // Profiler connections carry their own application name so the session can ignore the profiler's own queries.
        private static string Cs(string connStr) => new SqlConnectionStringBuilder(connStr) { ApplicationName = AppName, Pooling = true }.ConnectionString;

        private static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";

        // ---------- start / poll / stop ----------

        // Starts a session for this user + connection (an older one of the same user and connection is replaced, e.g. a reloaded tab).
        public async Task<(string Id, string? Database)> StartAsync(string user, string connKey, string connStr, bool thisDbOnly, int minMs, CancellationToken ct)
        {
            var b = new SqlConnectionStringBuilder(connStr);
            string? db = thisDbOnly && !string.IsNullOrWhiteSpace(b.InitialCatalog) ? b.InitialCatalog : null;
            minMs = Math.Clamp(minMs, 0, 3_600_000);

            List<Sess> old;
            lock (_lock)
            {
                old = _sessions.Values.Where(s => s.User == user && s.ConnKey == connKey).ToList();
                if (_sessions.Count - old.Count >= MaxSessions) throw new InvalidOperationException("Too many profilers are running on this server. Stop one first.");
            }
            foreach (var o in old) await StopSessionAsync(o);

            var id = Guid.NewGuid().ToString("N");
            var name = "ldb_prof_" + id[..16];
            var pred = $"sqlserver.client_app_name <> {Lit(AppName)}" + (db != null ? $" AND sqlserver.database_name = {Lit(db)}" : "")
                     + (minMs > 0 ? $" AND duration >= {(long)minMs * 1000}" : "");
            const string acts = "ACTION(package0.event_sequence, sqlserver.database_name, sqlserver.client_app_name, sqlserver.client_hostname, sqlserver.username)";
            var ddl = $@"CREATE EVENT SESSION [{name}] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(SET collect_batch_text=(1) {acts} WHERE ({pred})),
ADD EVENT sqlserver.rpc_completed(SET collect_statement=(1) {acts} WHERE ({pred}))
ADD TARGET package0.ring_buffer(SET max_memory=4096)
WITH (MAX_DISPATCH_LATENCY=1 SECONDS, EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS, STARTUP_STATE=OFF);
ALTER EVENT SESSION [{name}] ON SERVER STATE = START;";

            try
            {
                await using var conn = new SqlConnection(Cs(connStr));
                await conn.OpenAsync(ct);
                await using (var chk = conn.CreateCommand())
                {
                    chk.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)";
                    if ((int)(await chk.ExecuteScalarAsync(ct))! == 5)
                        throw new InvalidOperationException("The profiler does not support Azure SQL Database (it needs server-level Extended Events).");
                }
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = ddl;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number is 262 or 297 or 300 or 371 or 15151)
            {
                throw new InvalidOperationException("The SQL login needs the ALTER ANY EVENT SESSION and VIEW SERVER STATE permissions to run the profiler. " + ex.Message);
            }

            var s = new Sess { Id = id, Name = name, User = user, ConnKey = connKey, ConnStr = connStr, LastSeen = DateTime.UtcNow };
            lock (_lock) _sessions[id] = s;
            SaveMarker(s);
            return (id, db);
        }

        // gone = the session no longer exists (stopped, expired, or replaced by another tab).
        public async Task<(bool Gone, List<ProfEvent> Events, bool More)> PollAsync(string user, string id, CancellationToken ct)
        {
            Sess? s;
            lock (_lock) { _sessions.TryGetValue(id, out s); if (s != null && s.User != user) s = null; if (s != null) s.LastSeen = DateTime.UtcNow; }
            if (s == null) return (true, new(), false);

            string? xml;
            await using (var conn = new SqlConnection(Cs(s.ConnStr)))
            {
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT CAST(t.target_data AS nvarchar(max)) FROM sys.dm_xe_sessions x
JOIN sys.dm_xe_session_targets t ON t.event_session_address = x.address
WHERE x.name = @n AND t.target_name = 'ring_buffer'";
                cmd.Parameters.AddWithValue("@n", s.Name);
                xml = await cmd.ExecuteScalarAsync(ct) as string;
            }
            if (xml == null) return (true, new(), false); // dropped on the server by someone else

            var all = new List<ProfEvent>();
            foreach (var e in XDocument.Parse(xml).Descendants("event"))
            {
                long seq = (long)Num(e.Elements("action").FirstOrDefault(a => (string?)a.Attribute("name") == "event_sequence"));
                if (seq <= s.LastSeq) continue;
                string Act(string n) => (string?)e.Elements("action").FirstOrDefault(a => (string?)a.Attribute("name") == n)?.Element("value") ?? "";
                string Dat(string n) => (string?)e.Elements("data").FirstOrDefault(a => (string?)a.Attribute("name") == n)?.Element("value") ?? "";
                var type = (string?)e.Attribute("name") == "rpc_completed" ? "RPC" : "Batch";
                var sql = type == "RPC" ? Dat("statement") : Dat("batch_text");
                if (type == "RPC" && Dat("object_name") is { Length: > 0 } obj) sql = obj + (sql.Length > 0 ? "  -- " + sql : "");
                if (sql.Length > MaxSqlChars) sql = sql[..MaxSqlChars] + "...";
                var ts = DateTime.TryParse((string?)e.Attribute("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : DateTime.Now;
                all.Add(new ProfEvent(seq, ts.ToString("HH:mm:ss.fff"), type,
                    Math.Round(Num(e.Elements("data").FirstOrDefault(a => (string?)a.Attribute("name") == "duration")) / 1000.0, 1),
                    Math.Round(Num(e.Elements("data").FirstOrDefault(a => (string?)a.Attribute("name") == "cpu_time")) / 1000.0, 1),
                    (long)Num(e.Elements("data").FirstOrDefault(a => (string?)a.Attribute("name") == "logical_reads")),
                    (long)Num(e.Elements("data").FirstOrDefault(a => (string?)a.Attribute("name") == "row_count")),
                    Act("database_name"), Act("username"), Act("client_hostname"), Act("client_app_name"), sql));
            }
            all.Sort((a, b) => a.Seq.CompareTo(b.Seq));
            var more = all.Count > MaxPerPoll;
            var page = all.Take(MaxPerPoll).ToList();
            if (page.Count > 0) s.LastSeq = page[^1].Seq;
            return (false, page, more);
        }

        private static double Num(XElement? el) =>
            double.TryParse((string?)el?.Element("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        // Unknown id = already stopped = fine.
        public async Task<bool> StopAsync(string user, string id)
        {
            Sess? s;
            lock (_lock) { _sessions.TryGetValue(id, out s); if (s != null && s.User != user) s = null; }
            if (s == null) return true;
            return await StopSessionAsync(s);
        }

        private async Task<bool> StopSessionAsync(Sess s)
        {
            lock (_lock) _sessions.Remove(s.Id);
            var ok = await DropAsync(s.ConnStr, s.Name);
            if (ok) DeleteMarker(s.Id);
            return ok;
        }

        private async Task<bool> DropAsync(string connStr, string name)
        {
            try
            {
                await using var conn = new SqlConnection(Cs(connStr));
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = {Lit(name)}) DROP EVENT SESSION [{name}] ON SERVER";
                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex) { _log.LogWarning("Could not drop profiler session {Name}: {Msg}", name, ex.Message); return false; }
        }

        // ---------- markers: sessions to clean up if the app dies without stopping them ----------

        private string MarkerPath(string id) => Path.Combine(_dir, id + ".bin");
        private void SaveMarker(Sess s)
        {
            try { File.WriteAllBytes(MarkerPath(s.Id), System.Text.Encoding.UTF8.GetBytes(_prot.Protect(JsonSerializer.Serialize(new[] { s.User, s.ConnKey, s.Name })))); }
            catch (Exception ex) { _log.LogWarning("Could not save profiler marker: {Msg}", ex.Message); }
        }
        private void DeleteMarker(string id) { try { File.Delete(MarkerPath(id)); } catch { } }

        private async Task CleanLeftoversAsync()
        {
            foreach (var f in Directory.GetFiles(_dir, "*.bin"))
            {
                try
                {
                    var v = JsonSerializer.Deserialize<string[]>(_prot.Unprotect(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f))));
                    var hit = v == null ? null : _store.Load(v[1], v[0]);
                    if (hit == null || await DropAsync(hit.Value.ConnectionString, v![2])) File.Delete(f); // keep the marker when the drop failed, retry next start
                }
                catch { try { File.Delete(f); } catch { } }
            }
        }

        // ---------- background: expire sessions whose page stopped polling ----------

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            await CleanLeftoversAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(stop))
                {
                    List<Sess> dead;
                    lock (_lock) dead = _sessions.Values.Where(s => (DateTime.UtcNow - s.LastSeen).TotalSeconds > LeaseSeconds).ToList();
                    foreach (var s in dead) { _log.LogInformation("Profiler session {Name} expired (no poll for {N}s), dropping it.", s.Name, LeaseSeconds); await StopSessionAsync(s); }
                }
            }
            catch (OperationCanceledException) { }
        }

        public override async Task StopAsync(CancellationToken ct)
        {
            await base.StopAsync(ct);
            List<Sess> all; lock (_lock) all = _sessions.Values.ToList();
            foreach (var s in all) await StopSessionAsync(s);
        }
    }
}
