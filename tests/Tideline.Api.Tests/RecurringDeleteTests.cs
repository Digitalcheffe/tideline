using System.Net;
using Dapper;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

/// <summary>Deleting a recurring item: the item goes, its lines stay in the ledger unlinked, and nothing is left pointing at it.</summary>
public sealed class RecurringDeleteTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private async Task<long> NewItem(string name)
    {
        var category = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Utilities'");
        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/recurring", new { name, categoryId = category, expectedMinCents = 1000, expectedMaxCents = 2000, dueDay = 8, firstDueOn = "2026-01-08" });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        return body.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Deleting_an_item_keeps_its_lines_unlinks_them_clears_the_rule_link_and_alerts_and_is_audited()
    {
        var id = await NewItem("Delete me");
        List<long> lineIds;
        using (var conn = _api.Open()) lineIds = conn.Query<long>("SELECT id FROM transactions ORDER BY id LIMIT 3").ToList();
        foreach (var line in lineIds) _api.Execute("UPDATE transactions SET recurring_id = @id WHERE id = @line", new { id, line });
        var rule = _api.Scalar<long>("SELECT id FROM rules ORDER BY id LIMIT 1");
        _api.Execute("UPDATE rules SET set_recurring_id = @id WHERE id = @rule", new { id, rule });
        _api.Execute("INSERT INTO flags (kind, subject_type, subject_id, period, severity, message, created_at) VALUES ('recurring_missing', 'recurring', @id, '2026-03', 'warn', 'x', '2026-04-01T00:00:00Z')", new { id });
        var lines = _api.Scalar<long>("SELECT COUNT(*) FROM transactions");

        var (status, body) = await _api.Send(HttpMethod.Delete, $"/api/v1/recurring/{id}");

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        body.GetProperty("linesUnlinked").GetInt32().Should().Be(3);
        body.GetProperty("rulesChanged").GetInt32().Should().Be(1);
        body.GetProperty("alertsCleared").GetInt32().Should().Be(1);
        _api.Scalar<long>("SELECT COUNT(*) FROM recurring WHERE id = @id", new { id }).Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(lines, "no line is deleted");
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id = @id", new { id }).Should().Be(0);
        _api.Scalar<long?>("SELECT set_recurring_id FROM rules WHERE id = @rule", new { rule }).Should().BeNull();
        _api.Scalar<long>("SELECT COUNT(*) FROM rules WHERE id = @rule", new { rule }).Should().Be(1, "the rule stays");
        _api.Scalar<string?>("SELECT resolved_at FROM flags WHERE subject_type = 'recurring' AND subject_id = @id", new { id }).Should().NotBeNull();
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'recurring.deleted' AND entity_id = @id", new { id = id.ToString() }).Should().Be(1);

        (await _api.Get("/api/v1/recurring")).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Should().NotContain(id);
        (await _api.Send(HttpMethod.Delete, $"/api/v1/recurring/{id}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_viewer_cannot_delete_an_item()
    {
        var id = await NewItem("Keep me");
        var (_, secret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, secret);

        (await viewer.DeleteAsync($"/api/v1/recurring/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _api.Scalar<long>("SELECT COUNT(*) FROM recurring WHERE id = @id", new { id }).Should().Be(1);
    }
}
