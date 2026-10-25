using System.Text;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Core.Tests;

public class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]   // RFC 4648 test vectors, without padding
    public void Matches_the_rfc_vectors(string text, string expected)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(text)).Should().Be(expected);
        Encoding.ASCII.GetString(Base32.Decode(expected)).Should().Be(text);
    }

    [Fact]
    public void Decoding_ignores_spaces_dashes_padding_and_case() =>
        Encoding.ASCII.GetString(Base32.Decode("mzxw 6ytb-oi======")).Should().Be("foobar");

    [Fact]
    public void A_bad_character_is_refused() =>
        FluentActions.Invoking(() => Base32.Decode("MZXW1")).Should().Throw<FormatException>();
}

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1, secret "12345678901234567890"; the six digit codes are the last six of the published eight
    private static readonly string Secret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Matches_the_rfc_6238_vectors(long unixSeconds, string expected) =>
        Totp.Code(Secret, unixSeconds / 30).Should().Be(expected);

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    [Fact]
    public void Accepts_the_current_step_and_one_either_side_but_not_two()
    {
        var now = At(1_700_000_000);
        var step = Totp.StepOf(now);

        Totp.Verify(Secret, Totp.Code(Secret, step), now, 0).Should().Be(step);
        Totp.Verify(Secret, Totp.Code(Secret, step - 1), now, 0).Should().Be(step - 1);
        Totp.Verify(Secret, Totp.Code(Secret, step + 1), now, 0).Should().Be(step + 1);
        Totp.Verify(Secret, Totp.Code(Secret, step - 2), now, 0).Should().BeNull();
        Totp.Verify(Secret, Totp.Code(Secret, step + 2), now, 0).Should().BeNull();
    }

    [Fact]
    public void A_code_that_was_used_cannot_be_used_again_in_its_window()
    {
        var now = At(1_700_000_000);
        var step = Totp.StepOf(now);
        var code = Totp.Code(Secret, step);

        var first = Totp.Verify(Secret, code, now, lastUsedStep: 0);
        first.Should().Be(step);

        Totp.Verify(Secret, code, now, lastUsedStep: first!.Value).Should().BeNull("replay");
        Totp.Verify(Secret, Totp.Code(Secret, step - 1), now, lastUsedStep: first.Value).Should().BeNull("an older window after a newer one is also refused");
        Totp.Verify(Secret, Totp.Code(Secret, step + 1), now, lastUsedStep: first.Value).Should().Be(step + 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Anything_that_is_not_six_digits_is_refused(string typed) =>
        Totp.Verify(Secret, typed, At(1_700_000_000), 0).Should().BeNull();

    [Fact]
    public void Spaces_in_a_typed_code_are_fine()
    {
        var now = At(1_700_000_000);
        var code = Totp.Code(Secret, Totp.StepOf(now));
        Totp.Verify(Secret, $"{code[..3]} {code[3..]}", now, 0).Should().NotBeNull();
    }

    [Fact]
    public void New_secrets_are_random_and_160_bits()
    {
        var a = Totp.NewSecret();
        a.Should().NotBe(Totp.NewSecret());
        Base32.Decode(a).Should().HaveCount(20);
    }

    [Fact]
    public void The_otpauth_uri_carries_everything_an_authenticator_needs()
    {
        var uri = Totp.OtpAuthUri("Tideline", "alex@example.com", "ABCDEFGH");
        uri.Should().Be("otpauth://totp/Tideline:alex%40example.com?secret=ABCDEFGH&issuer=Tideline&algorithm=SHA1&digits=6&period=30");
    }
}

public class RecoveryCodeTests
{
    [Fact]
    public void Ten_distinct_codes_in_a_readable_shape()
    {
        var codes = RecoveryCodes.Generate();

        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z2-9]{5}-[a-z2-9]{5}$"));
        string.Concat(codes).Should().NotContainAny("0", "1", "i", "l", "o");
    }

    [Fact]
    public void Typing_style_does_not_matter_and_only_the_hash_is_kept()
    {
        RecoveryCodes.Hash("ABCDE-fghjk").Should().Be(RecoveryCodes.Hash("abcde fghjk"));
        RecoveryCodes.Hash("abcdefghjk").Should().Be(RecoveryCodes.Hash("abcde-fghjk"));
        RecoveryCodes.Hash("abcde-fghjk").Should().HaveLength(64).And.NotContain("abcde");
        RecoveryCodes.Hash("abcde-fghjk").Should().NotBe(RecoveryCodes.Hash("abcde-fghjm"));
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("a long passphrase of mine")]
    [InlineData("correct horse battery 7")]
    [InlineData("Tr0ub4dor&3-and-more")]
    public void Good_passwords_pass(string password) =>
        PasswordPolicy.Check(password, "alex@example.com", "Alex").Should().BeNull();

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Too_short_is_refused_with_advice(string password) =>
        PasswordPolicy.Check(password).Should().Contain("at least 12");

    [Theory]
    [InlineData("password1234")]
    [InlineData("PASSWORD1234")]
    [InlineData("qwertyuiop12")]
    [InlineData("123456789012")]
    public void Common_passwords_are_refused_whatever_the_case(string password) =>
        PasswordPolicy.Check(password).Should().Contain("common");

    [Fact]
    public void One_repeated_character_is_refused() =>
        PasswordPolicy.Check("aaaaaaaaaaaaaaaa").Should().Contain("few different");

    [Fact]
    public void The_email_and_the_name_are_not_allowed_inside()
    {
        PasswordPolicy.Check("my-alexmorgan-secret", "alexmorgan@example.com").Should().Contain("email");
        PasswordPolicy.Check("my-secret-for-Alex-Morgan", "x@example.com", "Alex Morgan").Should().Contain("name");
    }
}
