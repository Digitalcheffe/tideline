using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tideline.Core.Security;
using Tideline.Data;
using Tideline.Data.Security;

namespace Tideline.Cli.Tests;

public sealed class UserCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tideline-users-{Guid.NewGuid():N}");
    private readonly string _cs;

    public UserCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        _cs = $"Data Source={Path.Combine(_dir, "t.db")};Pooling=False";
        Migrator.MigrateUp(_cs);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<(int Code, string Text)> Run(params string[] args)
    {
        var sw = new StringWriter();
        var code = await new CliApp(sw, TimeProvider.System).RunAsync([.. args, "--db", _cs]);
        return (code, sw.ToString());
    }

    private static string TokenIn(string text) => System.Text.RegularExpressions.Regex.Match(text, @"setup\?token=(\S+)").Groups[1].Value;

    [Fact]
    public async Task Adding_a_person_prints_a_one_time_link_that_resolves_to_them()
    {
        var (code, text) = await Run("user", "add", "--email", "Alex@Example.com", "--name", "Alex", "--role", "owner", "--url", "http://localhost:5173");

        code.Should().Be(0, text);
        text.Should().Contain("Added Alex (alex@example.com) as owner").And.Contain("http://localhost:5173/setup?token=");

        using var conn = new SqliteConnectionFactory(_cs).Open();
        var user = new UserRepository(conn, TimeProvider.System).ByEmail("ALEX@example.com")!;
        user.Role.Should().Be(Role.Owner);
        user.HasPassword.Should().BeFalse("they choose it from the link");
        new SetupLinks(conn, TimeProvider.System).Resolve(TokenIn(text)).Should().Be(user.Id);
        new SetupLinks(conn, TimeProvider.System).Resolve("something else").Should().BeNull();
        conn.ExecuteScalarLong("SELECT COUNT(*) FROM audit_log WHERE action IN ('user.created', 'user.setup_link_created')").Should().Be(2);
    }

    [Fact]
    public async Task The_token_is_not_stored_only_its_hash_and_a_new_link_replaces_the_old_one()
    {
        var first = TokenIn((await Run("user", "add", "--email", "a@example.com", "--name", "A")).Text);
        var second = TokenIn((await Run("user", "link", "--email", "a@example.com")).Text);

        using var conn = new SqliteConnectionFactory(_cs).Open();
        var links = new SetupLinks(conn, TimeProvider.System);
        links.Resolve(first).Should().BeNull("an older link stops working");
        links.Resolve(second).Should().NotBeNull();
        conn.ExecuteScalarLong($"SELECT COUNT(*) FROM settings WHERE value LIKE '%{second}%' OR key LIKE '%{second}%'").Should().Be(0);
    }

    [Fact]
    public async Task A_link_runs_out_after_fifteen_minutes()
    {
        var text = (await Run("user", "add", "--email", "a@example.com", "--name", "A")).Text;
        var token = TokenIn(text);

        using var conn = new SqliteConnectionFactory(_cs).Open();
        new SetupLinks(conn, new Later(TimeSpan.FromMinutes(14))).Resolve(token).Should().NotBeNull();
        new SetupLinks(conn, new Later(TimeSpan.FromMinutes(16))).Resolve(token).Should().BeNull();
    }

    [Fact]
    public async Task List_shows_everyone_and_who_is_still_waiting_to_set_up()
    {
        await Run("user", "add", "--email", "a@example.com", "--name", "Alice", "--role", "viewer");

        var (_, text) = await Run("user", "list");

        text.Should().Contain("a@example.com").And.Contain("Alice").And.Contain("viewer").And.Contain("not set up").And.Contain("waiting for setup");
    }

    [Fact]
    public async Task Bad_input_says_what_to_do()
    {
        (await Run("user", "add", "--email", "not-an-email", "--name", "X")).Text.Should().Contain("does not look like an email");
        (await Run("user", "add", "--email", "a@example.com")).Text.Should().Contain("Give a name");
        (await Run("user", "add", "--email", "a@example.com", "--name", "A", "--role", "boss")).Text.Should().Contain("not a role");
        (await Run("user", "link", "--email", "nobody@example.com")).Text.Should().Contain("no one with the email");

        await Run("user", "add", "--email", "a@example.com", "--name", "A");
        (await Run("user", "add", "--email", "A@EXAMPLE.com", "--name", "Again")).Text.Should().Contain("already a user");
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_or_deactivated()
    {
        await Run("user", "add", "--email", "o@example.com", "--name", "Owner", "--role", "owner");
        await Run("user", "add", "--email", "m@example.com", "--name", "Member");

        (await Run("user", "role", "--email", "o@example.com", "--role", "member")).Text.Should().Contain("no owner");
        (await Run("user", "deactivate", "--email", "o@example.com")).Text.Should().Contain("no owner");

        (await Run("user", "role", "--email", "m@example.com", "--role", "owner")).Code.Should().Be(0);
        (await Run("user", "role", "--email", "o@example.com", "--role", "member")).Code.Should().Be(0, "there is another owner now");
    }

    [Fact]
    public async Task Resetting_the_second_step_removes_the_authenticator_signs_them_out_and_gives_a_new_link()
    {
        await Run("user", "add", "--email", "a@example.com", "--name", "A");
        using (var conn = new SqliteConnectionFactory(_cs).Open())
        {
            var users = new UserRepository(conn, TimeProvider.System);
            var id = users.ByEmail("a@example.com")!.Id;
            new MfaRepository(conn, TimeProvider.System).AddTotp(id, "Authenticator app", "sealed");
            new MfaRepository(conn, TimeProvider.System).ReplaceRecoveryCodes(id, ["h1", "h2"]);
            users.SetMfaEnrolled(id, DateTimeOffset.UtcNow);
            new SessionRepository(conn, TimeProvider.System).Create(id, "hash", DateTimeOffset.UtcNow.AddDays(1), null, null, mfaPassed: true);
        }

        var (code, text) = await Run("user", "reset-mfa", "--email", "a@example.com");

        code.Should().Be(0, text);
        text.Should().Contain("signed them out everywhere").And.Contain("setup?token=");
        using var check = new SqliteConnectionFactory(_cs).Open();
        check.ExecuteScalarLong("SELECT COUNT(*) FROM user_mfa").Should().Be(0);
        check.ExecuteScalarLong("SELECT COUNT(*) FROM recovery_codes").Should().Be(0);
        check.ExecuteScalarLong("SELECT COUNT(*) FROM sessions WHERE revoked_at IS NULL").Should().Be(0);
        new UserRepository(check, TimeProvider.System).ByEmail("a@example.com")!.MfaEnrolledAt.Should().BeNull();
        check.ExecuteScalarLong("SELECT COUNT(*) FROM audit_log WHERE action = 'mfa.reset'").Should().Be(1);
    }

    private sealed class Later(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + by;
    }
}

internal static class ConnExtensions
{
    public static long ExecuteScalarLong(this System.Data.IDbConnection conn, string sql) =>
        Convert.ToInt64(Dapper.SqlMapper.ExecuteScalar(conn, sql));
}
