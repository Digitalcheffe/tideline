using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class MovesByHandTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Account(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private async Task<long> Line(string account, long cents, string description, string date = "2026-03-20")
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/transactions",
            new { accountId = Account(account), postedOn = date, amountCents = cents, description, categoryId = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Groceries'"), type = "extra" });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> Detail(long id) => await _api.Get($"/api/v1/transactions/{id}");

    [Fact]
    public async Task Two_lines_for_the_same_amount_on_two_accounts_are_paired_and_stop_counting_as_spending()
    {
        var before = (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("extra").Cents();
        var outLeg = await Line("Checking", -4000, "HAND OUT");
        (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("extra").Cents().Should().Be(before + 4000, "on its own the out leg is Extra spending");
        var inLeg = await Line("Savings", 4000, "HAND IN");

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { inLeg, outLeg } });

        status.Should().Be(HttpStatusCode.Created, json.ToString());
        var move = json.GetProperty("id").GetInt64();
        foreach (var id in new[] { outLeg, inLeg })
        {
            var d = await Detail(id);
            d.GetProperty("moveId").GetInt64().Should().Be(move);
            d.GetProperty("type").ValueKind.Should().Be(JsonValueKind.Null);
            d.GetProperty("categoryId").ValueKind.Should().Be(JsonValueKind.Null);
        }
        (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("extra").Cents().Should().Be(before, "a move between our own accounts is not spending");
        _api.Scalar<string>("SELECT counts_as FROM moves WHERE id = @move", new { move }).Should().Be("savings", "one leg is on a savings account");
        (await _api.Get("/api/v1/audit?action=move.pair")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Taking_a_move_apart_gives_both_lines_back_to_the_rules_and_a_later_import_does_not_pair_them_again()
    {
        var outLeg = await Line("Checking", -2500, "APART OUT");
        var inLeg = await Line("Savings", 2500, "APART IN");
        var move = (await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { outLeg, inLeg } })).Body.GetProperty("id").GetInt64();

        var (status, json) = await _api.Send(HttpMethod.Delete, $"/api/v1/moves/{move}");

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("unpaired").GetInt32().Should().Be(2);
        foreach (var id in new[] { outLeg, inLeg })
        {
            var d = await Detail(id);
            d.GetProperty("moveId").ValueKind.Should().Be(JsonValueKind.Null);
            d.GetProperty("type").ValueKind.Should().Be(JsonValueKind.String, "the rules give every line a type again");
            d.GetProperty("isManualOverride").GetBoolean().Should().BeFalse();
        }
        _api.Scalar<long>("SELECT COUNT(*) FROM moves WHERE id = @move", new { move }).Should().Be(0);
        (await _api.Send(HttpMethod.Delete, $"/api/v1/moves/{move}")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_waiting_leg_can_be_paired_with_a_line_or_released()
    {
        var waiting = await Line("Checking", -1800, "WAITING OUT");
        var partner = await Line("Savings", 1800, "WAITING IN");
        _api.Execute("INSERT INTO moves (counts_as, created_at) VALUES ('none', '2026-04-01T00:00:00Z')");
        var waitingMove = _api.Scalar<long>("SELECT MAX(id) FROM moves");
        _api.Execute("UPDATE transactions SET move_id = @waitingMove, category_id = NULL, type = NULL WHERE id = @waiting", new { waitingMove, waiting });

        var pair = await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { waiting, partner } });

        pair.Status.Should().Be(HttpStatusCode.Created, pair.Body.ToString());
        pair.Body.GetProperty("id").GetInt64().Should().Be(waitingMove, "the waiting leg's own move is kept");
        _api.Scalar<string>("SELECT counts_as FROM moves WHERE id = @waitingMove", new { waitingMove }).Should().Be("savings");

        // a lone waiting leg is released the same way
        var lone = await Line("Checking", -999, "LONE OUT");
        _api.Execute("INSERT INTO moves (counts_as, created_at) VALUES ('none', '2026-04-01T00:00:00Z')");
        var loneMove = _api.Scalar<long>("SELECT MAX(id) FROM moves");
        _api.Execute("UPDATE transactions SET move_id = @loneMove, category_id = NULL, type = NULL WHERE id = @lone", new { loneMove, lone });

        (await _api.Send(HttpMethod.Delete, $"/api/v1/moves/{loneMove}")).Body.GetProperty("unpaired").GetInt32().Should().Be(1);
        (await Detail(lone)).GetProperty("moveId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Lines_that_do_not_make_a_move_are_refused_in_words()
    {
        var checkingOut = await Line("Checking", -3000, "BAD OUT");
        var checkingOut2 = await Line("Checking", 3000, "BAD IN SAME ACCOUNT");
        var savingsIn = await Line("Savings", 3100, "BAD IN OTHER AMOUNT");
        var savingsOut = await Line("Savings", -3000, "BAD OUT AGAIN");
        var alreadyPaired = _api.IdOf("TRANSFER TO SAVINGS");
        var freeIn = await Line("Savings", 30000, "BAD FREE IN");

        async Task<string> Refused(params long[] ids)
        {
            var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = ids });
            status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity, HttpStatusCode.NotFound);
            return json.GetProperty("detail").GetString()!;
        }

        (await Refused(checkingOut)).Should().Contain("exactly two");
        (await Refused(checkingOut, checkingOut)).Should().Contain("exactly two");
        (await Refused(checkingOut, checkingOut2)).Should().Contain("same account");
        (await Refused(checkingOut, savingsIn)).Should().Contain("same amount");
        (await Refused(checkingOut, savingsOut)).Should().Contain("one line going out and one coming in");
        (await Refused(alreadyPaired, freeIn)).Should().Contain("already part of a move");
        (await Refused(checkingOut, 999999)).Should().Contain("999999");
    }

    [Fact]
    public async Task A_split_line_cannot_be_paired_until_its_splits_are_removed()
    {
        var outLeg = await Line("Checking", -5000, "SPLIT OUT");
        var inLeg = await Line("Savings", 5000, "SPLIT IN");
        var groceries = _api.Scalar<long>("SELECT id FROM categories WHERE name = 'Groceries'");
        (await _api.Send(HttpMethod.Put, $"/api/v1/transactions/{outLeg}/splits", new { version = await _api.VersionOf(outLeg), splits = new[] { new { amountCents = -2000, categoryId = groceries, type = "extra" } } })).Status.Should().Be(HttpStatusCode.OK);

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { outLeg, inLeg } });

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        json.GetProperty("detail").GetString().Should().Contain("split");
    }

    [Fact]
    public async Task A_viewer_cannot_pair_or_unpair()
    {
        var viewer = _api.AddUser("view@example.com", "Viewer", Tideline.Core.Security.Role.Viewer);
        var client = await _api.SignInAsync("view@example.com", ApiFixture.Password, viewer.Secret);

        (await client.PostAsJsonAsync("/api/v1/moves", new { transactionIds = new long[] { 1, 2 } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync("/api/v1/moves/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
