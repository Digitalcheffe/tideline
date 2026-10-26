using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace Tideline.Data.Security;

/// <summary>
/// The keys that protect account numbers. Only the last 4 digits are ever kept, and not as text: a keyed hash (to match a file name or a rule to
/// an account) and a sealed copy (to show as ••••1111). Without this key file, the database says nothing about an account's digits, and a
/// 4-digit number cannot be guessed back from its hash. The key file lives with the other keys and is backed up with them.
/// </summary>
public sealed class AccountNumberKeys
{
    private readonly byte[] _mac;
    private readonly byte[] _aes;

    public AccountNumberKeys(byte[] material)
    {
        if (material.Length != 64) throw new ArgumentException("The account number key must be 64 bytes.", nameof(material));
        _mac = material[..32];
        _aes = material[32..];
    }

    /// <summary>Reads the key file, or makes a new random one when there is none yet.</summary>
    public static AccountNumberKeys LoadOrCreate(string path)
    {
        if (File.Exists(path)) return new AccountNumberKeys(File.ReadAllBytes(path));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var material = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(path, material);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new AccountNumberKeys(material);
    }

    public string Hash(string lastFour) => Convert.ToHexStringLower(HMACSHA256.HashData(_mac, Encoding.UTF8.GetBytes("account:" + lastFour)));

    /// <summary>Proves the same key is in use as when the database was last written to.</summary>
    public string CheckValue() => Convert.ToHexStringLower(HMACSHA256.HashData(_mac, Encoding.UTF8.GetBytes("key check")));

    public string Seal(string lastFour)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(lastFour);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_aes, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The last 4 digits from their sealed form, or null when there is none or it cannot be opened with this key.</summary>
    public string? Open(string? sealedText)
    {
        if (string.IsNullOrEmpty(sealedText)) return null;
        try
        {
            var all = Convert.FromBase64String(sealedText);
            var nonce = all[..12];
            var tag = all[12..28];
            var cipher = all[28..];
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_aes, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed class AccountKeyMismatchException(string message) : InvalidOperationException(message);

/// <summary>The one place account numbers are turned into what is stored and what is shown (docs/ARCHITECTURE.md).</summary>
public static class AccountNumbers
{
    public const string CheckKey = "account_key_check";

    private static readonly object Gate = new();
    private static Func<AccountNumberKeys>? _factory;
    private static AccountNumberKeys? _keys;

    /// <summary>Says where the keys come from, once. A second call is ignored, so a test assembly can fix a key first. The key is only read when first needed.</summary>
    public static void ConfigureOnce(Func<AccountNumberKeys> factory)
    {
        lock (Gate) _factory ??= factory;
    }

    public static AccountNumberKeys Keys
    {
        get
        {
            lock (Gate)
                return _keys ??= (_factory ?? throw new InvalidOperationException("The account number key has not been set up.")).Invoke();
        }
    }

    /// <summary>What is kept of whatever a person types or a file name holds: only the digits, and only the last 4 of them. Null when there are fewer than 4.</summary>
    public static string? LastFour(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        return digits.Length < 4 ? null : digits[^4..];
    }

    public static string Mask(string? lastFour) => lastFour is { Length: 4 } ? "••••" + lastFour : "";

    /// <summary>The keyed hash used to find an account from digits, or null when they are not at least 4 digits.</summary>
    public static string? HashOf(string? digits) => LastFour(digits) is { } l ? Keys.Hash(l) : null;

    public static string? MaskFrom(string? sealedText) => Keys.Open(sealedText) is { } l ? Mask(l) : null;

    /// <summary>
    /// A value from a rule: a hash already, or digits or a mask like ••••1111. Returns the hash, or null when it is neither.
    /// </summary>
    public static string? RuleValue(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        if (t.Length == 64 && t.All(Uri.IsHexDigit)) return t.ToLowerInvariant();
        return HashOf(t);
    }

    /// <summary>Stops the app if the key on disk is not the one this database was written with: every account would otherwise quietly stop matching.</summary>
    public static void Verify(IDbConnection conn, TimeProvider clock, AccountNumberKeys? keys = null)
    {
        keys ??= Keys;
        var settings = new SettingsRepository(conn, clock);
        var stored = settings.Get(CheckKey);
        if (stored is null)
        {
            settings.Set(CheckKey, keys.CheckValue());
            return;
        }
        if (stored != keys.CheckValue())
            throw new AccountKeyMismatchException(
                "The account number key does not match this database, so no account could be found. Restore the keys folder (account-numbers.key) from the same backup as the database.");
    }

    /// <summary>
    /// Brings a database from before this change in line: plain digits become a hash and a sealed copy of the last 4, and the digits are removed;
    /// rules that name an account by digits name it by hash. Safe to run again. Returns how many accounts were changed.
    /// </summary>
    public static int SecureExisting(IDbConnection conn, TimeProvider clock, AccountNumberKeys? keys = null)
    {
        TypeHandlers.EnsureRegistered();
        keys ??= Keys;
        using var tx = conn.BeginTransaction();
        var changed = 0;

        foreach (var (id, number) in conn.Query<(long Id, string Number)>("SELECT id, bank_number FROM accounts WHERE bank_number IS NOT NULL", transaction: tx).ToList())
        {
            var last = LastFour(number);
            conn.Execute("UPDATE accounts SET bank_number = NULL, bank_number_hash = @hash, bank_number_enc = @sealedText WHERE id = @id",
                new { id, hash = last is null ? null : keys.Hash(last), sealedText = last is null ? null : keys.Seal(last) }, tx);
            changed++;
        }

        foreach (var (id, value) in conn.Query<(long Id, string Value)>("SELECT id, value FROM rule_conditions WHERE field = 'account'", transaction: tx).ToList())
        {
            var text = value.Trim().Trim('"');
            if (text.Length == 64 && text.All(Uri.IsHexDigit)) continue;
            if (LastFour(text) is { } l)
                conn.Execute("UPDATE rule_conditions SET value = @value WHERE id = @id", new { id, value = "\"" + keys.Hash(l) + "\"" }, tx);
        }

        var settings = new SettingsRepository(conn, clock, tx);
        if (settings.Get(CheckKey) is null) settings.Set(CheckKey, keys.CheckValue());
        tx.Commit();
        return changed;
    }
}
