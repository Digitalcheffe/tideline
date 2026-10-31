using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>Using a saved transfer pattern on lines already in the ledger, which imports never go back to.</summary>
public sealed class MovePatternApplyTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Account(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private async Task<long> Line(string account, long cents, string description, string date)
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/transactions",
            new { accountId = Account(account), postedOn = date, amountCents = cents, description, categoryId = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Groceries'"), type = "extra" });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private async Task<long> Pattern(int days = 3)
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/move-patterns",
            new { fromAccountId = Account("Checking"), fromContains = "SWEEP OUT", toAccountId = Account("Savings"), toContains = "SWEEP IN", maxDaysApart = days });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private long? MoveOf(long line) => _api.Scalar<long?>("SELECT move_id FROM transactions WHERE id = @line", new { line });

    [Fact]
    public async Task A_pattern_pairs_the_matching_lines_already_in_the_ledger_and_stops_them_counting_as_spending()
    {
        var out1 = await Line("Checking", -5000, "SWEEP OUT 1", "2026-03-02");
        var in1 = await Line("Savings", 5000, "SWEEP IN 1", "2026-03-03");
        var out2 = await Line("Checking", -7500, "SWEEP OUT 2", "2026-03-10");
        var in2 = await Line("Savings", 7500, "SWEEP IN 2", "2026-03-10");
        var other = await Line("Checking", -999, "SWEEP OUT 3", "2026-03-12"); // no partner of the same amount

        var id = await Pattern();
        var (status, json) = await _api.Send(HttpMethod.Post, $"/api/v1/move-patterns/{id}/apply");

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("paired").GetInt32().Should().Be(2);
        json.GetProperty("skipped").GetInt32().Should().Be(0);
        MoveOf(out1).Should().NotBeNull().And.Be(MoveOf(in1));
        MoveOf(out2).Should().NotBeNull().And.Be(MoveOf(in2));
        MoveOf(other).Should().BeNull("nothing of the same amount to pair with");
        foreach (var leg in new[] { out1, in1, out2, in2 })
            _api.Scalar<string?>("SELECT type FROM transactions WHERE id = @leg", new { leg }).Should().BeNull("a line in a move has no type, so it is not spending");
        _api.Scalar<string>("SELECT type FROM transactions WHERE id = @other", new { other }).Should().Be("extra");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'move.pair'").Should().Be(2);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'move_pattern.applied'").Should().Be(1);

        var (_, again) = await _api.Send(HttpMethod.Post, $"/api/v1/move-patterns/{id}/apply");
        again.GetProperty("paired").GetInt32().Should().Be(0, "using it twice pairs nothing new");
    }

    [Fact]
    public async Task Lines_left_out_split_or_too_far_apart_are_not_paired()
    {
        var farOut = await Line("Checking", -1000, "SWEEP OUT FAR", "2026-03-01");
        var farIn = await Line("Savings", 1000, "SWEEP IN FAR", "2026-03-20"); // 19 days apart, the window is 3
        var leftOut = await Line("Checking", -2000, "SWEEP OUT GONE", "2026-03-05");
        var leftIn = await Line("Savings", 2000, "SWEEP IN GONE", "2026-03-05");
        await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { leftOut }, reason = "duplicate" });
        var splitOut = await Line("Checking", -3000, "SWEEP OUT SPLIT", "2026-03-06");
        var splitIn = await Line("Savings", 3000, "SWEEP IN SPLIT", "2026-03-06");
        var version = await _api.VersionOf(splitOut);
        var uncategorized = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Uncategorized'");
        (await _api.Send(HttpMethod.Put, $"/api/v1/transactions/{splitOut}/splits", new { version, splits = new[] { new { amountCents = -1000, categoryId = uncategorized, type = "extra" } } })).Status.Should().Be(HttpStatusCode.OK);

        var id = await Pattern();
        var (_, json) = await _api.Send(HttpMethod.Post, $"/api/v1/move-patterns/{id}/apply");

        json.GetProperty("paired").GetInt32().Should().Be(0);
        json.GetProperty("skipped").GetInt32().Should().Be(1, "the split line is found by the pattern but cannot be a move");
        foreach (var line in new[] { farOut, farIn, leftOut, leftIn, splitOut, splitIn }) MoveOf(line).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_pattern_is_not_found()
    {
        (await _api.Send(HttpMethod.Post, "/api/v1/move-patterns/99999/apply")).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
