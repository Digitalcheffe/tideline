using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

public sealed class EditingTests : IDisposable
{
    private const string March = "from=2026-03-01&to=2026-03-31";
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private async Task<(HttpStatusCode Status, JsonElement Body)> Patch(long id, object body) =>
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{id}", body);

    private async Task<(HttpStatusCode Status, JsonElement Body)> PutSplits(long id, string? version, params object[] splits) =>
        await _api.Send(HttpMethod.Put, $"/api/v1/transactions/{id}/splits", new { version, splits });

    // ---- PATCH ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Changing_the_category_type_payee_tags_and_notes_moves_the_numbers_and_marks_it_by_hand()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);

        var (status, body) = await Patch(id, new { version, categoryId = Category("Groceries"), type = "required", payee = "Corner Shop", tags = new[] { "Kids", "weekly shop" }, notes = "birthday" });

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("version").GetString().Should().NotBe(version);

        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("categoryName").GetString().Should().Be("Groceries");
        t.GetProperty("type").GetString().Should().Be("required");
        t.GetProperty("payee").GetString().Should().Be("Corner Shop");
        t.GetProperty("notes").GetString().Should().Be("birthday");
        t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Should().Equal("kids", "weekly-shop");
        t.GetProperty("isManualOverride").GetBoolean().Should().BeTrue();

        var spread = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");
        spread.GetProperty("required").Cents().Should().Be(58540 + 2000);
        spread.GetProperty("extra").Cents().Should().Be(2325 - 2000);
        spread.GetProperty("leftOver").Cents().Should().Be(96036, "moving a line between types does not change what is left over");
    }

    [Fact]
    public async Task The_bank_fields_do_not_change_and_cannot_be_sent()
    {
        var id = _api.IdOf("CITY POWER AUTOPAY");
        var before = (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("bank").GetRawText();

        await Patch(id, new { version = await _api.VersionOf(id), notes = "x", amountCents = 1, postedOn = "2020-01-01", description = "HACKED" });

        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("bank").GetRawText().Should().Be(before);
    }

    [Fact]
    public async Task Every_edit_is_audited_with_before_and_after()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        await Patch(id, new { version = await _api.VersionOf(id), categoryId = Category("Groceries"), type = "required" });

        var t = await _api.Get($"/api/v1/transactions/{id}");
        var entry = t.GetProperty("audit")[0];
        entry.GetProperty("action").GetString().Should().Be("transaction.update");
        entry.GetProperty("beforeJson").GetString().Should().Contain("\"type\":\"extra\"").And.Contain("\"isManualOverride\":false");
        entry.GetProperty("afterJson").GetString().Should().Contain("\"type\":\"required\"").And.Contain("\"isManualOverride\":true");
    }

    [Fact]
    public async Task A_stale_version_is_refused_with_409_and_a_missing_one_with_400()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);
        (await Patch(id, new { version, notes = "first" })).Status.Should().Be(HttpStatusCode.OK);

        var stale = await Patch(id, new { version, notes = "second" });
        stale.Status.Should().Be(HttpStatusCode.Conflict);
        stale.Body.GetProperty("detail").GetString().Should().Contain("changed");

        (await Patch(id, new { notes = "no version" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("notes").GetString().Should().Be("first");
    }

    [Fact]
    public async Task Bad_values_and_transfers_are_refused_with_a_reason()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        var version = await _api.VersionOf(id);

        var badType = await Patch(id, new { version, type = "luxury" });
        badType.Status.Should().Be(HttpStatusCode.BadRequest);
        badType.Body.GetProperty("detail").GetString().Should().Contain("one_time");

        (await Patch(id, new { version, categoryId = 99999 })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await Patch(id, new { version, tags = new[] { "  " } })).Status.Should().Be(HttpStatusCode.BadRequest);

        var transfer = _api.IdOf("TRANSFER TO SAVINGS");
        var move = await Patch(transfer, new { version = await _api.VersionOf(transfer), type = "extra" });
        move.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        move.Body.GetProperty("detail").GetString().Should().Contain("transfer");

        (await Patch(424242, new { version = "x" })).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_text_clears_a_value()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        await Patch(id, new { version = await _api.VersionOf(id), notes = "keep" });

        await Patch(id, new { version = await _api.VersionOf(id), notes = "", payee = "" });

        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        t.GetProperty("payee").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_manual_choice_survives_importing_the_same_file_again()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        await Patch(id, new { version = await _api.VersionOf(id), categoryId = Category("Groceries"), type = "required" });

        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", "acct_1111_03_01_2026_to_03_31_2026.csv");
        var again = await _api.Pipeline.ImportTruistAsync("acct_1111_03_01_2026_to_03_31_2026.csv", await File.ReadAllTextAsync(path));

        again.RowsInserted.Should().Be(0);
        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("categoryName").GetString().Should().Be("Groceries");
    }

    [Fact]
    public async Task Reset_to_rules_hands_the_line_back_to_the_rules()
    {
        var id = _api.IdOf("CITY POWER AUTOPAY");
        await Patch(id, new { version = await _api.VersionOf(id), categoryId = Category("Groceries"), type = "required", tags = new[] { "oops" } });

        var (status, _) = await _api.Send(HttpMethod.Post, $"/api/v1/transactions/{id}/reset-to-rules");

        status.Should().Be(HttpStatusCode.OK);
        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("categoryName").GetString().Should().Be("Utilities");
        t.GetProperty("type").GetString().Should().Be("bill");
        t.GetProperty("rule").GetProperty("name").GetString().Should().Be("Bill: power");
        t.GetProperty("isManualOverride").GetBoolean().Should().BeFalse();
        t.GetProperty("tags").GetArrayLength().Should().Be(0);
        t.GetProperty("audit")[0].GetProperty("action").GetString().Should().Be("transaction.reset");
    }

    [Fact]
    public async Task Reset_on_an_unmatched_line_goes_back_to_the_catch_all_with_review()
    {
        var id = _api.IdOf("MYSTERY VENDOR 9");
        await Patch(id, new { version = await _api.VersionOf(id), categoryId = Category("Groceries"), type = "required", tags = new string[0] });

        await _api.Send(HttpMethod.Post, $"/api/v1/transactions/{id}/reset-to-rules");

        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("categoryName").GetString().Should().Be("Uncategorized");
        t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Should().Equal("review");
    }

    // ---- bulk ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Several_lines_can_be_fixed_at_once_and_each_change_is_audited()
    {
        var ids = new[] { "MYSTERY VENDOR 9", "COFFEE SHOP", "INTEREST PAID" }.Select(_api.IdOf).ToArray();
        var groceries = Category("Groceries");

        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk",
            new { ids, set = new { categoryId = groceries, type = "required", addTags = new[] { "Fixed", "checked" }, removeTags = new[] { "review" } } });

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("updated").GetInt32().Should().Be(3);
        foreach (var id in ids)
        {
            var t = await _api.Get($"/api/v1/transactions/{id}");
            t.GetProperty("categoryName").GetString().Should().Be("Groceries");
            t.GetProperty("type").GetString().Should().Be("required");
            t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Should().Equal("checked", "fixed");
            t.GetProperty("isManualOverride").GetBoolean().Should().BeTrue();
            t.GetProperty("audit")[0].GetProperty("action").GetString().Should().Be("transaction.update");
        }
        (await _api.Get($"/api/v1/lines?{March}&review=true")).GetProperty("total").GetInt64().Should().Be(0, "the review tag was removed from all three");
    }

    [Fact]
    public async Task Bulk_edit_skips_transfers_counts_unknown_lines_and_changes_nothing_when_the_request_is_bad()
    {
        var mystery = _api.IdOf("MYSTERY VENDOR 9");
        var transfer = _api.IdOf("TRANSFER TO SAVINGS");

        var (_, body) = await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk",
            new { ids = new[] { mystery, transfer, 999999 }, set = new { type = "one_time" } });
        body.GetProperty("updated").GetInt32().Should().Be(1);
        body.GetProperty("skippedTransfers").GetInt32().Should().Be(1);
        body.GetProperty("notFound").GetInt32().Should().Be(1);

        var other = _api.IdOf("COFFEE SHOP");
        var bad = await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new[] { other }, set = new { categoryId = 424242 } });
        bad.Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Get($"/api/v1/transactions/{other}")).GetProperty("isManualOverride").GetBoolean().Should().BeFalse();

        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new long[0], set = new { type = "extra" } })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new[] { other }, set = new { } })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = Enumerable.Range(1, 501).Select(i => (long)i).ToArray(), set = new { type = "extra" } })).Status
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- splits --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Splitting_ten_dollars_of_groceries_to_clothing_moves_it_from_required_to_extra_and_the_total_is_unchanged()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var before = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");
        var id = _api.IdOf("GROCERY STORE #12");

        var (status, _) = await PutSplits(id, await _api.VersionOf(id), new { amountCents = -1000, categoryId = 900, type = "extra", note = "socks" });

        status.Should().Be(HttpStatusCode.OK);
        var after = (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread");
        after.GetProperty("required").Cents().Should().Be(before.GetProperty("required").Cents() - 1000);
        after.GetProperty("extra").Cents().Should().Be(before.GetProperty("extra").Cents() + 1000);
        after.GetProperty("leftOver").Cents().Should().Be(before.GetProperty("leftOver").Cents());

        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("unassignedCents").GetInt64().Should().Be(-7540);
        t.GetProperty("audit")[0].GetProperty("action").GetString().Should().Be("transaction.splits");
    }

    [Fact]
    public async Task Splits_may_not_exceed_the_line_or_point_the_other_way()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var id = _api.IdOf("GROCERY STORE #12");
        var version = await _api.VersionOf(id);

        var over = await PutSplits(id, version, new { amountCents = -5000, categoryId = 900, type = "extra" }, new { amountCents = -3541, categoryId = 900, type = "extra" });
        over.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        over.Body.GetProperty("detail").GetString().Should().Contain("$85.41").And.Contain("$85.40");

        (await PutSplits(id, version, new { amountCents = 1000, categoryId = 900, type = "extra" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await PutSplits(id, version, new { amountCents = 0, categoryId = 900, type = "extra" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await PutSplits(id, version, new { amountCents = -100, categoryId = 900, type = "nope" })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await PutSplits(id, version, new { amountCents = -100, categoryId = 424242, type = "extra" })).Status.Should().Be(HttpStatusCode.BadRequest);

        _api.Scalar<long>("SELECT COUNT(*) FROM split_lines").Should().Be(0, "a refused edit changes nothing");
    }

    [Fact]
    public async Task Splitting_the_whole_line_is_allowed_and_a_new_split_replaces_the_old_ones()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var id = _api.IdOf("GROCERY STORE #12");

        await PutSplits(id, await _api.VersionOf(id), new { amountCents = -4000, categoryId = 900, type = "extra" }, new { amountCents = -4540, categoryId = Category("Groceries"), type = "required", tags = new[] { "weekly" } });
        (await _api.Get($"/api/v1/transactions/{id}")).GetProperty("unassignedCents").GetInt64().Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM split_lines").Should().Be(2);

        await PutSplits(id, await _api.VersionOf(id), new { amountCents = -1000, categoryId = 900, type = "extra" });
        _api.Scalar<long>("SELECT COUNT(*) FROM split_lines").Should().Be(1);
        _api.Scalar<long>("SELECT COUNT(*) FROM split_line_tags").Should().Be(0, "the old splits' tags went with them");

        await PutSplits(id, await _api.VersionOf(id));
        _api.Scalar<long>("SELECT COUNT(*) FROM split_lines").Should().Be(0);
        (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread").GetProperty("required").Cents().Should().Be(58540);
    }

    [Fact]
    public async Task Splitting_a_transfer_or_with_a_stale_version_is_refused()
    {
        _api.Execute("INSERT INTO categories (id, name, default_type) VALUES (900, 'Clothing', 'extra')");
        var transfer = _api.IdOf("TRANSFER TO SAVINGS");
        (await PutSplits(transfer, await _api.VersionOf(transfer), new { amountCents = -100, categoryId = 900, type = "extra" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);

        var id = _api.IdOf("GROCERY STORE #12");
        var version = await _api.VersionOf(id);
        await Patch(id, new { version, notes = "touched" });
        (await PutSplits(id, version, new { amountCents = -100, categoryId = 900, type = "extra" })).Status.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- manual entry --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_manual_entry_is_a_line_like_any_other_and_can_be_deleted_again()
    {
        var checking = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'");

        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/transactions", new
        {
            accountId = checking, postedOn = "2026-03-20", amountCents = -4500, description = "Cash for the plumber",
            categoryId = Category("Groceries"), payee = "Plumber", tags = new[] { "Cash" }, notes = "paid in cash",
        });

        status.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("id").GetInt64();
        var t = await _api.Get($"/api/v1/transactions/{id}");
        t.GetProperty("bank").GetProperty("source").GetString().Should().Be("manual");
        t.GetProperty("bank").GetProperty("externalId").GetString().Should().StartWith("manual:");
        t.GetProperty("type").GetString().Should().Be("required", "the category's default type");
        t.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).Should().Equal("cash");
        (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread").GetProperty("required").Cents().Should().Be(58540 + 4500);

        var (deleted, _) = await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{id}");
        deleted.Should().Be(HttpStatusCode.OK);
        (await _api.Get($"/api/v1/overview?{March}")).GetProperty("spread").GetProperty("required").Cents().Should().Be(58540);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'transaction' AND action IN ('transaction.create', 'transaction.delete')").Should().Be(2);
    }

    [Fact]
    public async Task Lines_from_the_bank_cannot_be_deleted_and_bad_manual_entries_are_refused()
    {
        var imported = await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{_api.IdOf("MYSTERY VENDOR 9")}");
        imported.Status.Should().Be(HttpStatusCode.Conflict);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE original_description = 'MYSTERY VENDOR 9'").Should().Be(1);

        var checking = _api.Scalar<long>("SELECT id FROM accounts WHERE name = 'Checking'");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions", new { accountId = checking, postedOn = "2026-03-20", amountCents = -100, description = "x" })).Status
            .Should().Be(HttpStatusCode.BadRequest, "a category or a type is needed");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions", new { accountId = checking, postedOn = "2026-03-20", amountCents = 0, description = "x", type = "extra" })).Status
            .Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions", new { accountId = 99999, postedOn = "2026-03-20", amountCents = -5, description = "x", type = "extra" })).Status
            .Should().Be(HttpStatusCode.BadRequest);
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions", new { accountId = checking, postedOn = "2026-03-20", amountCents = -5, description = " ", type = "extra" })).Status
            .Should().Be(HttpStatusCode.BadRequest);
    }
}

