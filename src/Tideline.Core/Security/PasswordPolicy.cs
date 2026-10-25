namespace Tideline.Core.Security;

/// <summary>Minimum 12 characters and not on a short local list of common passwords (docs/09-security.md).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;

    // A small list kept in the code on purpose: nothing is sent anywhere to check a password.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "password12345", "password123456", "passw0rd1234", "password!234", "letmein12345", "welcome12345", "welcome123456",
        "qwertyuiop12", "qwertyuiop123", "qwerty123456", "qwertyuiopas", "asdfghjkl123", "asdfghjklqwe", "zxcvbnm12345", "1qaz2wsx3edc",
        "1q2w3e4r5t6y", "123456789012", "1234567890123", "12345678901234", "111111111111", "000000000000", "123123123123", "abcdefghijkl",
        "abcd12345678", "iloveyou1234", "iloveyou12345", "monkey123456", "dragon123456", "sunshine1234", "football1234", "baseball1234",
        "superman1234", "trustno12345", "changeme1234", "changeme12345", "administrator", "administrator1", "adminadmin12", "admin1234567",
        "admin123456!", "letmeinletmein", "passwordpassword", "passwordpass1", "correcthorsebatterystaple", "thequickbrownfox", "iamthewalrus1",
        "mypassword123", "mypassword1234", "mynameisjohn1", "welcometohome1", "startrek12345", "starwars12345", "princess12345", "michael12345",
        "jennifer12345", "charlie12345", "shadow123456", "master123456", "hello1234567", "freedom12345", "whatever12345", "computer1234",
        "internet1234", "summer123456", "winter123456", "spring123456", "autumn123456", "january12345", "february1234", "ilovemyfamily", "familyfamily1",
        "tideline12345", "tideline1234!", "ledger123456", "budget123456", "moneymoney123", "finance12345",
    };

    /// <returns>Why the password is not good enough, in plain words, or null when it is fine.</returns>
    public static string? Check(string? password, string? email = null, string? displayName = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Use at least {MinLength} characters. A few random words strung together works well.";

        if (password.Distinct().Count() < 4) return "That uses too few different characters.";
        if (Common.Contains(password.Trim())) return "That password is on the list of common ones. Pick something only you would think of.";

        // compare letters only, so "alex-morgan" is caught as well as "alexmorgan"
        var letters = Letters(password);
        var local = Letters((email ?? "").Split('@')[0]);
        if (local.Length >= 4 && letters.Contains(local)) return "Leave your email address out of the password.";
        var name = Letters(displayName);
        if (name.Length >= 4 && letters.Contains(name)) return "Leave your name out of the password.";

        return null;
    }

    private static string Letters(string? text) => new string((text ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
}
