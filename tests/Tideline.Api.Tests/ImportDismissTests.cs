using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Notify;

namespace Tideline.Api.Tests;

/// <summary>A failed import can be cleared from the Imports list, and then stops raising the alert.</summary>
public sealed class ImportDismissTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long FailedBatch(string file = "wrong-file.csv")
    {
        _api.Execute("INSERT INTO import_batches (source, file_name, started_at, status, error) VALUES ('csv', @file, @at, 'failed', 'No account has the bank number 7777.')",
            new { file, at = "2026-03-30T12:00:00Z" });
        return _api.Scalar<long>("SELECT MAX(id) FROM import_batches");
    }

    private async Task<List<long>> Listed() =>
        (await _api.Get("/api/v1/imports")).GetProperty("batches").EnumerateArray().Select(b => b.GetProperty("id").GetInt64()).ToList();

    [Fact]
    public async Task A_failed_import_is_cleared_from_the_list_but_the_record_and_the_audit_entry_stay()
    {
        var id = FailedBatch();
        (await Listed()).Should().Contain(id);

        var (status, _) = await _api.Send(HttpMethod.Post, $"/api/v1/imports/{id}/dismiss");

        status.Should().Be(HttpStatusCode.OK);
        (await Listed()).Should().NotContain(id);
        _api.Scalar<string?>("SELECT dismissed_at FROM import_batches WHERE id = @id", new { id }).Should().NotBeNull("the row stays");
        var audit = (await _api.Get("/api/v1/audit?action=import.dismissed")).GetProperty("items")[0];
        audit.GetProperty("beforeJson").GetString().Should().Contain("wrong-file.csv").And.Contain("\"dismissed\":false");
        audit.GetProperty("afterJson").GetString().Should().Contain("\"dismissed\":true");
    }

    [Fact]
    public async Task A_dismissed_failure_no_longer_raises_the_import_failed_alert()
    {
        var id = FailedBatch();
        using (var conn = _api.Open()) new Evaluator().Run(conn, new DateOnly(2026, 4, 1), _api.Clock);
        _api.Scalar<long>("SELECT COUNT(*) FROM flags WHERE kind = 'import_failed' AND resolved_at IS NULL").Should().Be(1);

        await _api.Send(HttpMethod.Post, $"/api/v1/imports/{id}/dismiss");
        using (var conn = _api.Open()) new Evaluator().Run(conn, new DateOnly(2026, 4, 1), _api.Clock);

        _api.Scalar<long>("SELECT COUNT(*) FROM flags WHERE kind = 'import_failed' AND resolved_at IS NULL").Should().Be(0, "the flag clears once nothing failed is left");
    }

    [Fact]
    public async Task Only_a_failed_import_can_be_dismissed_and_only_once()
    {
        var done = _api.Scalar<long>("SELECT id FROM import_batches WHERE status = 'done' LIMIT 1");
        var (status, json) = await _api.Send(HttpMethod.Post, $"/api/v1/imports/{done}/dismiss");
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        json.GetProperty("detail").GetString().Should().Contain("failed");

        var id = FailedBatch();
        (await _api.Send(HttpMethod.Post, $"/api/v1/imports/{id}/dismiss")).Status.Should().Be(HttpStatusCode.OK);
        (await _api.Send(HttpMethod.Post, $"/api/v1/imports/{id}/dismiss")).Status.Should().Be(HttpStatusCode.Conflict);
        (await _api.Send(HttpMethod.Post, "/api/v1/imports/999999/dismiss")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_an_owner_can_dismiss()
    {
        var id = FailedBatch();
        var (_, secret) = _api.AddUser("member@example.com", "Member", Tideline.Core.Security.Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, secret);

        (await member.PostAsync($"/api/v1/imports/{id}/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
