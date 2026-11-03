using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace LocalDiskBrowser.Services
{
    public record SqlConnRecord(string Owner, string Ip, string Label, DateTime Created, string Data);

    // Saved SQL Server connections. One file per connection in App_Data/sqlconn, named by the SHA-256 of a random key,
    // so the folder listing never reveals a usable key. The connection string inside is encrypted with Data Protection
    // (keys in App_Data/keys, protected with DPAPI). Also owns the RSA key pair the browser uses to encrypt a connection
    // string before sending it (the private key is kept encrypted on disk and never leaves the server).
    public class SqlConnStore
    {
        private static readonly Regex KeyFormat = new(@"^[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);
        private readonly IDataProtector _prot;
        private readonly ILogger<SqlConnStore> _log;
        private readonly string _dir;
        private readonly object _lock = new();
        private RSA? _rsa;
        private byte[] _spki = Array.Empty<byte>();

        public SqlConnStore(IWebHostEnvironment env, IDataProtectionProvider dp, ILogger<SqlConnStore> log)
        {
            _prot = dp.CreateProtector("LocalDiskBrowser.SqlConn.v1");
            _log = log;
            _dir = Path.Combine(env.ContentRootPath, "App_Data", "sqlconn");
            Directory.CreateDirectory(_dir);
        }

        // ---------- RSA key pair (browser -> server envelope) ----------

        private void EnsureRsa()
        {
            if (_rsa != null) return;
            lock (_lock)
            {
                if (_rsa != null) return;
                var path = Path.Combine(_dir, "_rsa.bin");
                var rsa = RSA.Create(2048);
                var loaded = false;
                if (File.Exists(path))
                {
                    try { rsa.ImportPkcs8PrivateKey(_prot.Unprotect(File.ReadAllBytes(path)), out _); loaded = true; }
                    catch { /* unreadable (machine / account changed): make a new pair, saved connections are separate files */ }
                }
                if (!loaded) File.WriteAllBytes(path, _prot.Protect(rsa.ExportPkcs8PrivateKey()));
                _spki = rsa.ExportSubjectPublicKeyInfo();
                _rsa = rsa;
                _log.LogInformation("SQL editor encryption key fingerprint: {Fp}", Fingerprint());
            }
        }

        private string Fingerprint() => string.Join(":", Convert.ToHexString(SHA256.HashData(_spki)).Chunk(4).Select(c => new string(c)).Take(8));

        public (string KeyId, string PublicKey, string Fingerprint) PublicKey()
        {
            EnsureRsa();
            return (Convert.ToHexString(SHA256.HashData(_spki))[..16], Convert.ToBase64String(_spki), Fingerprint());
        }

        public byte[] UnwrapKey(byte[] wrapped)
        {
            EnsureRsa();
            return _rsa!.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        }

        // ---------- saved connections ----------

        public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // Only a well-formed key ever reaches the file system.
        private string? PathFor(string? key) =>
            key != null && KeyFormat.IsMatch(key)
                ? Path.Combine(_dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json")
                : null;

        public void Save(string key, string owner, string ip, string label, string connectionString)
        {
            var path = PathFor(key) ?? throw new ArgumentException("Bad key.");
            var rec = new SqlConnRecord(owner, ip, label, DateTime.UtcNow, _prot.Protect(connectionString));
            lock (_lock) File.WriteAllText(path, JsonSerializer.Serialize(rec));
        }

        // null when the key is unknown, the file is unreadable, or the connection belongs to another user.
        public (SqlConnRecord Rec, string ConnectionString)? Load(string? key, string owner)
        {
            var path = PathFor(key);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var rec = JsonSerializer.Deserialize<SqlConnRecord>(File.ReadAllText(path));
                if (rec == null || !string.Equals(rec.Owner, owner, StringComparison.Ordinal)) return null;
                return (rec, _prot.Unprotect(rec.Data));
            }
            catch { return null; }
        }

        public void SetIp(string key, SqlConnRecord rec, string ip)
        {
            var path = PathFor(key); if (path == null) return;
            lock (_lock) File.WriteAllText(path, JsonSerializer.Serialize(rec with { Ip = ip }));
        }

        public bool Delete(string? key, string owner)
        {
            var path = PathFor(key);
            if (path == null || Load(key, owner) == null) return false;
            File.Delete(path);
            return true;
        }
    }
}
