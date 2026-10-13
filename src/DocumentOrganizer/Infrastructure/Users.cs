using System.Collections.Concurrent;
using System.Security.Cryptography;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Infrastructure;

public static class Passwords
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || p[0] != "pbkdf2-sha256" || !int.TryParse(p[1], out var it)) return false;
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string? Weakness(string? pw)
    {
        if (string.IsNullOrEmpty(pw) || pw.Length < 10) return "Use at least 10 characters.";
        int classes = (pw.Any(char.IsUpper) ? 1 : 0) + (pw.Any(char.IsLower) ? 1 : 0) + (pw.Any(char.IsDigit) ? 1 : 0) + (pw.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        return classes < 3 ? "Use at least three of: upper-case, lower-case, digits, symbols." : null;
    }

    public static string Generate()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return "Do-" + RandomNumberGenerator.GetString(chars, 10) + "-" + RandomNumberGenerator.GetInt32(10, 99);
    }
}

public sealed class UserStore(AppDb app)
{
    private readonly ConcurrentDictionary<string, (int fails, DateTime until)> _lockout = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the first administrator when the user table is empty. Returns the generated password, if any.</summary>
    public string? EnsureAdmin()
    {
        if (Convert.ToInt64(app.Db.Scalar("SELECT COUNT(*) FROM users") ?? 0L) > 0) return null;
        var pw = Environment.GetEnvironmentVariable("DOCORG_ADMIN_PASSWORD");
        bool generated = string.IsNullOrWhiteSpace(pw);
        if (generated) pw = Passwords.Generate();
        Create("admin", "Administrator", Roles.Administrator, pw!);
        app.Audit("System", null, "User created", null, "Initial administrator account 'admin'.");
        return generated ? pw : null;
    }

    /// <summary>
    /// Sets the 'admin' password from DOCORG_ADMIN_PASSWORD on every start-up (if set), and re-enables the account.
    /// Lets an administrator recover access by restarting the app with a new password.
    /// </summary>
    public string? ApplyAdminPasswordFromEnvironment()
    {
        var pw = Environment.GetEnvironmentVariable("DOCORG_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(pw)) return null;
        if (pw.Length < 8) return "DOCORG_ADMIN_PASSWORD is shorter than 8 characters, so it was ignored.";
        if (Get("admin") == null) { Create("admin", "Administrator", Roles.Administrator, pw); }
        else
        {
            SetPassword("admin", pw);
            app.Db.Execute("UPDATE users SET active=1, role=@r WHERE user_name='admin'", ("r", Roles.Administrator));
        }
        _lockout.TryRemove("admin", out _);
        app.Audit("System", null, "Admin password set", null, "Password for 'admin' set from DOCORG_ADMIN_PASSWORD at start-up.");
        return "ok";
    }

    public void Create(string userName, string? displayName, string role, string password) =>
        app.Db.Execute("INSERT INTO users(user_name,display_name,role,password_hash,active,created_at) VALUES(@u,@d,@r,@p,1,@t)",
            ("u", userName.Trim()), ("d", displayName ?? userName), ("r", role), ("p", Passwords.Hash(password)), ("t", DateTime.UtcNow.ToString("o")));

    public Row? Get(string userName) => app.Db.QueryOne("SELECT * FROM users WHERE user_name=@u", ("u", userName));

    public (Row? user, string? error) Authenticate(string userName, string password)
    {
        if (_lockout.TryGetValue(userName, out var l) && l.until > DateTime.UtcNow)
            return (null, $"Too many failed attempts. Try again after {l.until.ToLocalTime():HH:mm}.");
        var u = Get(userName);
        if (u == null || u.Long("active") == 0 || !Passwords.Verify(password, u.Str("password_hash")!))
        {
            var f = _lockout.AddOrUpdate(userName, _ => (1, DateTime.MinValue), (_, o) => (o.fails + 1, o.fails + 1 >= 5 ? DateTime.UtcNow.AddMinutes(10) : DateTime.MinValue));
            return (null, f.fails >= 5 ? "Too many failed attempts. The account is locked for 10 minutes." : "User name or password is incorrect.");
        }
        _lockout.TryRemove(userName, out _);
        app.Db.Execute("UPDATE users SET last_login=@t WHERE user_name=@u", ("t", DateTime.UtcNow.ToString("o")), ("u", userName));
        return (u, null);
    }

    public void SetPassword(string userName, string password) =>
        app.Db.Execute("UPDATE users SET password_hash=@p WHERE user_name=@u", ("p", Passwords.Hash(password)), ("u", userName));
}
