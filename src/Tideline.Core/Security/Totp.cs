using System.Security.Cryptography;
using System.Text;

namespace Tideline.Core.Security;

public enum Role { Viewer = 1, Member = 2, Owner = 3 }

/// <summary>RFC 4648 base32 without padding, the form authenticator apps expect.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"'{c}' is not a base32 character.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}

/// <summary>Time-based one-time passwords: RFC 6238 with SHA-1, six digits, 30 second steps (docs/09-security.md).</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(string base32Secret, long step)
    {
        var key = Base32.Decode(base32Secret);
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step the code belongs to, within one step either side of now, or null. A step at or before
    /// <paramref name="lastUsedStep"/> is refused, so a code that has been used cannot be used again.
    /// </summary>
    public static long? Verify(string base32Secret, string code, DateTimeOffset now, long lastUsedStep)
    {
        var typed = new string(code.Where(char.IsDigit).ToArray());
        if (typed.Length != Digits) return null;

        var current = StepOf(now);
        long? match = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            // compare every window in constant time, so timing does not say which one was close
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(base32Secret, step)), Encoding.ASCII.GetBytes(typed));
            if (equal && step > lastUsedStep) match = step;
        }
        return match;
    }

    public static string OtpAuthUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>Ten single-use codes for when the authenticator is lost. Shown once; only a hash is stored.</summary>
public static class RecoveryCodes
{
    private const string Alphabet = "abcdefghjkmnpqrstvwxyz23456789"; // no 0/o, 1/i/l
    public const int Count = 10;

    public static IReadOnlyList<string> Generate(int count = Count)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chars = new char[10];
            for (var j = 0; j < chars.Length; j++) chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            codes.Add($"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}");
        }
        return codes;
    }

    public static string Normalize(string code) => new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}
