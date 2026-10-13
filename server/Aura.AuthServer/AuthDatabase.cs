using System.Security.Cryptography;
using System.Text;
using ColorForge.Models;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Aura.AuthServer;

// Each command uses parameters. A single writer gate also makes one-time activation atomic.
public sealed class AuthDatabase : IDisposable
{
    private readonly SqliteConnection _db;
    private SqliteTransaction? _transaction;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ECDsa _signer;
    private readonly TimeProvider _clock;
    public string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());
    public AuthDatabase(string directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) {
            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Windows account missing.");
            acl.SetOwner(owner);
            foreach (var sid in new[] { owner, new System.Security.Principal.SecurityIdentifier("S-1-5-18") })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory), acl);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string keyPath = Path.Combine(directory, "signing.key");
        _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(keyPath)) _signer.ImportPkcs8PrivateKey(File.ReadAllBytes(keyPath), out _);
        else WritePrivateFile(keyPath, _signer.ExportPkcs8PrivateKey());
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "auth.db"), Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; CREATE TABLE IF NOT EXISTS accounts(email TEXT PRIMARY KEY, salt BLOB NOT NULL, hash BLOB NOT NULL); CREATE TABLE IF NOT EXISTS logins(hash TEXT PRIMARY KEY, email TEXT NOT NULL, expires INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS licenses(hash TEXT PRIMARY KEY, days INTEGER NOT NULL, email TEXT, device TEXT, activated INTEGER, expires INTEGER, revoked INTEGER NOT NULL DEFAULT 0, suffix TEXT NOT NULL); CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, hash TEXT UNIQUE NOT NULL, license TEXT NOT NULL, email TEXT NOT NULL, device TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0);");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(directory, "auth.db"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public static void WritePrivateFile(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using FileStream file = new(path, options);
        file.Write(bytes); file.Flush(true);
    }
    private SqliteCommand Command(string sql, params object[] values)
    {
        SqliteCommand c = _db.CreateCommand(); c.CommandText = sql; c.Transaction = _transaction;
        for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$p" + i, values[i]);
        return c;
    }
    private int Execute(string sql, params object[] values) { using var c = Command(sql, values); return c.ExecuteNonQuery(); }
    private static bool EmailValid(string value) => value.Length is >= 3 and <= 254 && value.Contains('@') && !value.Any(char.IsWhiteSpace);
    private static bool DeviceValid(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Email(string value) => value.Trim().ToLowerInvariant();
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static async Task<byte[]> PasswordHash(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, MemorySize = 65536, Iterations = 3, DegreeOfParallelism = 2 };
        return await argon.GetBytesAsync(32);
    }
    public async Task CreateAccountAsync(string email, string password)
    {
        email = Email(email);
        if (!EmailValid(email) || password.Length is < 12 or > 256) throw new ArgumentException("Email or password invalid (password: 12–256 characters).");
        byte[] salt = RandomNumberGenerator.GetBytes(16), hash = await PasswordHash(password, salt);
        await _gate.WaitAsync(); try { Execute("INSERT INTO accounts VALUES($p0,$p1,$p2)", email, salt, hash); } finally { _gate.Release(); }
    }
    public async Task<string> CreateLicenseAsync(int days)
    {
        if (days is < 1 or > 3660) throw new ArgumentOutOfRangeException(nameof(days));
        string key = "AURA-" + Token();
        await _gate.WaitAsync(); try { Execute("INSERT INTO licenses(hash,days,suffix) VALUES($p0,$p1,$p2)", AuthProtocol.Hash(key), days, key[^4..]); }
        finally { _gate.Release(); }
        return key;
    }
    public async Task<AuthReply> LoginAsync(LoginRequest request)
    {
        string email = Email(request.Email);
        if (!EmailValid(email) || request.Password.Length is < 1 or > 256 || !DeviceValid(request.DeviceId)) return Failure();
        // Serialize memory-heavy password work and bound it with HTTP rate/concurrency limits.
        await _gate.WaitAsync();
        try {
            byte[] salt, expected;
            using (var c = Command("SELECT salt,hash FROM accounts WHERE email=$p0", email))
            using (var reader = c.ExecuteReader()) {
                bool found = reader.Read(); salt = found ? (byte[])reader[0] : new byte[16]; expected = found ? (byte[])reader[1] : new byte[32];
            }
            byte[] supplied = await PasswordHash(request.Password, salt);
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Failure();
            long now = _clock.GetUtcNow().ToUnixTimeSeconds();
            Execute("DELETE FROM logins WHERE expires <= $p0", now);
            string token = Token();
            Execute("INSERT INTO logins VALUES($p0,$p1,$p2)", AuthProtocol.Hash(token), email, now + 600);
            // An existing device-bound license can be resumed after logout without consuming a new key.
            using var query = Command("SELECT hash FROM licenses WHERE email=$p0 AND device=$p1 AND revoked=0 AND expires>$p2 ORDER BY expires DESC LIMIT 1", email, request.DeviceId, now);
            string? license = query.ExecuteScalar() as string;
            if (license != null) { Execute("DELETE FROM logins WHERE hash=$p0", AuthProtocol.Hash(token)); return new(true, "Подписка активна.", Email: email, Session: NewSession(license, email, request.DeviceId)); }
            return new(true, "Аккаунт подтверждён.", token, email);
        } finally { _gate.Release(); }
    }
    public async Task<AuthReply> ActivateAsync(ActivateRequest request)
    {
        if (request.LoginToken.Length != 64 || request.LicenseKey.Length > 128 || !DeviceValid(request.DeviceId)) return Failure();
        await _gate.WaitAsync();
        try {
            using var tx = _db.BeginTransaction();
            _transaction = tx;
            long now = _clock.GetUtcNow().ToUnixTimeSeconds();
            using var login = Command("SELECT email FROM logins WHERE hash=$p0 AND expires>$p1", AuthProtocol.Hash(request.LoginToken), now);
            string? email = login.ExecuteScalar() as string; if (email == null) return Failure();
            string key = request.LicenseKey.Trim().ToUpperInvariant(), hash = AuthProtocol.Hash(key);
            using var query = Command("SELECT days FROM licenses WHERE hash=$p0 AND email IS NULL AND revoked=0", hash);
            object? value = query.ExecuteScalar(); if (value == null) return Failure();
            Execute("UPDATE licenses SET email=$p0,device=$p1,activated=$p2,expires=$p3 WHERE hash=$p4 AND email IS NULL AND revoked=0", email, request.DeviceId, now, now + Convert.ToInt64(value) * 86400, hash);
            Execute("DELETE FROM logins WHERE hash=$p0", AuthProtocol.Hash(request.LoginToken));
            AuthSession session = NewSession(hash, email, request.DeviceId);
            tx.Commit(); _transaction = null; return new(true, "Подписка активирована.", Session: session);
        } finally { _transaction = null; _gate.Release(); }
    }
    private AuthSession NewSession(string license, string email, string device)
    {
        string token = Token(), id = Guid.NewGuid().ToString("N");
        Execute("INSERT INTO sessions VALUES($p0,$p1,$p2,$p3,$p4,0)", id, AuthProtocol.Hash(token), license, email, device);
        return Grant(token, id, license, email, device);
    }
    private AuthSession Grant(string token, string id, string license, string email, string device)
    {
        using var c = Command("SELECT activated,expires,suffix FROM licenses WHERE hash=$p0", license);
        using var r = c.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException();
        DateTimeOffset activated = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)), expires = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)), now = _clock.GetUtcNow();
        return AuthProtocol.Sign(new(AuthProtocol.Issuer, id, email, device, AuthProtocol.Hash(token), activated, expires, now, now.AddHours(24) < expires ? now.AddHours(24) : expires, r.GetString(2)), token, _signer);
    }
    public async Task<AuthReply> ValidateAsync(ValidateRequest request)
    {
        if (request.SessionToken.Length != 64 || !DeviceValid(request.DeviceId)) return Failure();
        await _gate.WaitAsync();
        try {
            string? id = null, license = null, email = null;
            using (var c = Command("SELECT s.id,s.license,s.email FROM sessions s JOIN licenses l ON l.hash=s.license WHERE s.hash=$p0 AND s.device=$p1 AND s.revoked=0 AND l.revoked=0 AND l.expires>$p2", AuthProtocol.Hash(request.SessionToken), request.DeviceId, _clock.GetUtcNow().ToUnixTimeSeconds()))
            using (var r = c.ExecuteReader()) { if (r.Read()) { id = r.GetString(0); license = r.GetString(1); email = r.GetString(2); } }
            return id == null ? Failure() : new(true, "Подписка проверена сервером.", Session: Grant(request.SessionToken, id, license!, email!, request.DeviceId));
        } finally { _gate.Release(); }
    }
    public async Task<AuthReply> RevokeSessionAsync(ValidateRequest request)
    {
        if (request.SessionToken.Length != 64 || !DeviceValid(request.DeviceId)) return Failure();
        await _gate.WaitAsync();
        try { int count = Execute("UPDATE sessions SET revoked=1 WHERE hash=$p0 AND device=$p1", AuthProtocol.Hash(request.SessionToken), request.DeviceId);
            return count == 1 ? new(true, "Сессия закрыта.") : Failure(); }
        finally { _gate.Release(); }
    }
    public async Task RevokeLicenseAsync(string key)
    {
        await _gate.WaitAsync(); try { Execute("UPDATE licenses SET revoked=1 WHERE hash=$p0", AuthProtocol.Hash(key.Trim().ToUpperInvariant())); } finally { _gate.Release(); }
    }
    private static AuthReply Failure() => new(false, "Данные неверны, лицензия истекла или доступ отозван.");
    public void Dispose() { _db.Dispose(); _signer.Dispose(); _gate.Dispose(); }
}
