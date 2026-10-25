using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class DataTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task A_backup_can_be_made_from_the_app_and_is_listed_checked_and_audited()
    {
        var before = await _api.Get("/api/v1/backups");
        before.GetProperty("files").GetArrayLength().Should().Be(0);
        before.GetProperty("overdue").GetBoolean().Should().BeTrue();

        var (status, made) = await _api.Send(HttpMethod.Post, "/api/v1/backups");

        status.Should().Be(HttpStatusCode.OK, made.ToString());
        made.GetProperty("ok").GetBoolean().Should().BeTrue();
        made.GetProperty("keysIncluded").GetBoolean().Should().BeTrue();
        var after = await _api.Get("/api/v1/backups");
        after.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("kind").GetString()).Should().BeEquivalentTo("database", "keys");
        after.GetProperty("overdue").GetBoolean().Should().BeFalse();
        after.GetProperty("keepDays").GetInt32().Should().Be(30);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'backup.created'").Should().Be(1);
    }

    [Fact]
    public async Task The_export_is_a_zip_of_tables_without_secrets_and_is_audited()
    {
        var response = await _api.Client.PostAsync("/api/v1/export", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var names = archive.Entries.Select(e => e.FullName).ToList();
        names.Should().Contain(["transactions.csv", "accounts.csv", "users.csv"]);
        names.Should().NotContain(["sessions.csv", "api_tokens.csv", "user_mfa.csv", "recovery_codes.csv", "settings.csv"]);
        using var users = new StreamReader(archive.GetEntry("users.csv")!.Open());
        (await users.ReadToEndAsync()).Should().NotContain("password_hash").And.NotContain("$");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'export.created'").Should().Be(1);
    }

    [Fact]
    public async Task Only_an_owner_can_back_up_or_export()
    {
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);

        (await member.GetAsync("/api/v1/backups")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync("/api/v1/backups", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsync("/api/v1/export", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _api.Anonymous.PostAsync("/api/v1/export", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
