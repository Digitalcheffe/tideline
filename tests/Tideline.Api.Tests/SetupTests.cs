using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;

namespace Tideline.Api.Tests;

public sealed class SetupTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Id(string table, string name) => _api.Scalar<long>($"SELECT id FROM {table} WHERE name = @name", new { name });
    private long Count(string sql, object? args = null) => _api.Scalar<long>(sql, args);

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, json.ToString());
        return json;
    }

    private async Task Refused(HttpMethod method, string url, object? body, HttpStatusCode status, string? mention = null)
    {
        var (got, json) = await _api.Send(method, url, body);
        got.Should().Be(status, json.ToString());
        if (mention is not null) json.GetProperty("detail").GetString().Should().Contain(mention);
    }

    private long AuditCount(string action) => Count("SELECT COUNT(*) FROM audit_log WHERE action = @action", new { action });

    // ---- accounts ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_credit_card_is_added_with_its_terms_and_the_change_is_audited()
    {
        var created = await Ok(HttpMethod.Post, "/api/v1/accounts",
            new { name = "Travel Card", kind = "credit_card", institution = "Truist", bankNumber = "6666", card = new { isPayInFull = true, statementDay = 12, dueDay = 6, creditLimitCents = 1500000 } }, HttpStatusCode.Created);

        var list = (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == created.GetProperty("id").GetInt64());
        list.GetProperty("kind").GetString().Should().Be("credit_card");
        list.GetProperty("isInSpread").GetBoolean().Should().BeTrue();
        list.GetProperty("card").GetProperty("dueDay").GetInt32().Should().Be(6);
        list.GetProperty("card").GetProperty("creditLimitCents").GetInt64().Should().Be(1500000);

        var entry = (await _api.Get("/api/v1/audit?action=account.created")).GetProperty("items")[0];
        entry.GetProperty("beforeJson").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("afterJson").GetString().Should().Contain("Travel Card").And.Contain("\"dueDay\":6");
    }

    [Fact]
    public async Task Loans_and_assets_are_not_in_the_spread_unless_asked()
    {
        var loan = await Ok(HttpMethod.Post, "/api/v1/accounts", new { name = "Car loan", kind = "loan" }, HttpStatusCode.Created);

        var row = (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == loan.GetProperty("id").GetInt64());
        row.GetProperty("isInSpread").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Bad_accounts_are_refused_with_a_reason()
    {
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "", kind = "checking" }, HttpStatusCode.BadRequest, "name");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "X", kind = "boat" }, HttpStatusCode.BadRequest, "kind");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "X", kind = "checking", bankNumber = "123" }, HttpStatusCode.BadRequest, "last 4 digits");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "X", kind = "checking", openingBalanceCents = 5000 }, HttpStatusCode.BadRequest, "which day");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "X", kind = "checking", openingBalanceCents = 5000, openingOn = "March" }, HttpStatusCode.BadRequest, "date");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "X", kind = "credit_card", card = new { dueDay = 40 } }, HttpStatusCode.BadRequest, "due day");
        await Refused(HttpMethod.Post, "/api/v1/accounts", new { name = "Checking", kind = "checking" }, HttpStatusCode.Conflict, "already used");
        await Refused(HttpMethod.Patch, "/api/v1/accounts/99999", new { name = "Y" }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_opening_balance_and_the_csv_cutover_can_be_set_and_cleared()
    {
        var checking = Id("accounts", "Checking");

        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { openingBalanceCents = 250000, openingOn = "2026-02-28", csvUntilOn = "2026-06-30" });
        var set = (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == checking);
        set.GetProperty("openingBalanceCents").GetInt64().Should().Be(250000);
        set.GetProperty("openingOn").GetString().Should().Be("2026-02-28");
        set.GetProperty("csvUntilOn").GetString().Should().Be("2026-06-30");

        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { clearOpening = true, csvUntilOn = "" });
        var cleared = (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == checking);
        cleared.GetProperty("openingBalanceCents").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("csvUntilOn").ValueKind.Should().Be(JsonValueKind.Null);

        var entry = (await _api.Get("/api/v1/audit?action=account.updated")).GetProperty("items")[0];
        entry.GetProperty("beforeJson").GetString().Should().Contain("250000");
    }

    [Fact]
    public async Task Card_terms_belong_to_cards_only()
    {
        var card = Id("accounts", "Card");
        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{card}", new { card = new { isPayInFull = false, statementDay = 3, dueDay = 28 } });
        var row = (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == card);
        row.GetProperty("card").GetProperty("isPayInFull").GetBoolean().Should().BeFalse();
        row.GetProperty("card").GetProperty("statementDay").GetInt32().Should().Be(3);

        await Refused(HttpMethod.Patch, $"/api/v1/accounts/{Id("accounts", "Checking")}", new { card = new { dueDay = 5 } }, HttpStatusCode.BadRequest, "credit card");
    }

    // ---- transfer patterns ---------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_patterns_can_be_added_changed_and_removed()
    {
        var checking = Id("accounts", "Checking");
        var savings = Id("accounts", "Savings");

        var created = await Ok(HttpMethod.Post, "/api/v1/move-patterns", new { fromAccountId = savings, fromContains = "OUT", toAccountId = checking, toContains = "IN", maxDaysApart = 3 }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        (await _api.Get("/api/v1/move-patterns")).EnumerateArray().Should().Contain(p => p.GetProperty("id").GetInt64() == id && p.GetProperty("fromAccountName").GetString() == "Savings");

        await Ok(HttpMethod.Patch, $"/api/v1/move-patterns/{id}", new { fromAccountId = savings, fromContains = "SWEEP", toAccountId = checking, toContains = "SWEEP", maxDaysApart = 4 });
        Count("SELECT COUNT(*) FROM move_patterns WHERE id = @id AND from_contains = 'SWEEP' AND max_days_apart = 4", new { id }).Should().Be(1);

        await Ok(HttpMethod.Delete, $"/api/v1/move-patterns/{id}");
        Count("SELECT COUNT(*) FROM move_patterns WHERE id = @id", new { id }).Should().Be(0);
        AuditCount("move_pattern.created").Should().Be(1);
        AuditCount("move_pattern.deleted").Should().Be(1);
    }

    [Fact]
    public async Task Bad_patterns_are_refused()
    {
        var checking = Id("accounts", "Checking");
        await Refused(HttpMethod.Post, "/api/v1/move-patterns", new { fromAccountId = checking, fromContains = "A", toAccountId = checking, toContains = "B" }, HttpStatusCode.BadRequest, "two different");
        await Refused(HttpMethod.Post, "/api/v1/move-patterns", new { fromAccountId = checking, fromContains = "", toAccountId = Id("accounts", "Savings"), toContains = "B" }, HttpStatusCode.BadRequest, "text to look for");
        await Refused(HttpMethod.Post, "/api/v1/move-patterns", new { fromAccountId = checking, fromContains = "A", toAccountId = 99999, toContains = "B" }, HttpStatusCode.BadRequest, "no account");
        await Refused(HttpMethod.Delete, "/api/v1/move-patterns/99999", null, HttpStatusCode.NotFound);
    }

    // ---- categories ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Categories_can_be_added_renamed_and_archived_and_archived_ones_are_hidden_by_default()
    {
        var id = (await Ok(HttpMethod.Post, "/api/v1/categories", new { name = "Pets", defaultType = "required" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        await Ok(HttpMethod.Patch, $"/api/v1/categories/{id}", new { name = "Pet care", defaultType = "extra" });
        (await _api.Get("/api/v1/categories")).EnumerateArray().Should().Contain(c => c.GetProperty("name").GetString() == "Pet care" && c.GetProperty("type").GetString() == "extra");

        await Ok(HttpMethod.Patch, $"/api/v1/categories/{id}", new { isArchived = true });
        (await _api.Get("/api/v1/categories")).EnumerateArray().Should().NotContain(c => c.GetProperty("id").GetInt64() == id);
        (await _api.Get("/api/v1/categories?includeArchived=true")).EnumerateArray().Should().Contain(c => c.GetProperty("id").GetInt64() == id);

        await Refused(HttpMethod.Post, "/api/v1/categories", new { name = "Groceries", defaultType = "required" }, HttpStatusCode.Conflict);
        await Refused(HttpMethod.Post, "/api/v1/categories", new { name = "X", defaultType = "luxury" }, HttpStatusCode.BadRequest, "one_time");
        await Refused(HttpMethod.Post, "/api/v1/categories", new { name = " ", defaultType = "extra" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Merging_moves_every_line_split_rule_and_recurring_item_and_archives_the_old_category()
    {
        var utilities = Id("categories", "Utilities");
        var subs = Id("categories", "Subscriptions");
        var billsBefore = (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("bills").GetProperty("cents").GetInt64();

        var merged = await Ok(HttpMethod.Post, $"/api/v1/categories/{utilities}/merge", new { intoId = subs });

        merged.GetProperty("rowsMoved").GetInt64().Should().BeGreaterThan(0);
        Count("SELECT COUNT(*) FROM transactions WHERE category_id = @id", new { id = utilities }).Should().Be(0);
        Count("SELECT COUNT(*) FROM rules WHERE set_category_id = @id", new { id = utilities }).Should().Be(0);
        Count("SELECT COUNT(*) FROM recurring WHERE category_id = @id", new { id = utilities }).Should().Be(0);
        Count("SELECT COUNT(*) FROM categories WHERE id = @id AND is_archived = 1 AND merged_into_id = @into", new { id = utilities, into = subs }).Should().Be(1);

        var billsAfter = (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("bills").GetProperty("cents").GetInt64();
        billsAfter.Should().Be(billsBefore, "merging changes names, not money");
        AuditCount("category.merged").Should().Be(1);

        await Refused(HttpMethod.Post, $"/api/v1/categories/{subs}/merge", new { intoId = subs }, HttpStatusCode.BadRequest, "different");
        await Refused(HttpMethod.Post, $"/api/v1/categories/{subs}/merge", new { intoId = 99999 }, HttpStatusCode.BadRequest, "does not exist");
    }

    // ---- tags ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tags_can_be_made_renamed_and_deleted_and_deleting_takes_them_off_every_line()
    {
        var id = (await Ok(HttpMethod.Post, "/api/v1/tags", new { name = "Road Trip", color = "#336699" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
        (await _api.Get("/api/v1/tags")).EnumerateArray().Should().Contain(t => t.GetProperty("name").GetString() == "road-trip" && t.GetProperty("color").GetString() == "#336699");
        await Ok(HttpMethod.Patch, $"/api/v1/tags/{id}", new { name = "Vacation 2026" });
        Id("tags", "vacation-2026").Should().Be(id);

        var line = _api.IdOf("MYSTERY VENDOR 9");
        await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), tags = new[] { "vacation-2026" } });
        (await _api.Get("/api/v1/tags")).EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == id).GetProperty("lineCount").GetInt64().Should().Be(1);

        var deleted = await Ok(HttpMethod.Delete, $"/api/v1/tags/{id}");
        deleted.GetProperty("removedFrom").GetInt64().Should().Be(1);
        (await _api.Get($"/api/v1/transactions/{line}")).GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().NotContain("vacation-2026");
        await Refused(HttpMethod.Post, "/api/v1/tags", new { name = "review" }, HttpStatusCode.Conflict);
        await Refused(HttpMethod.Post, "/api/v1/tags", new { name = "  " }, HttpStatusCode.BadRequest);
    }

    // ---- recurring -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_recurring_item_is_added_then_changed_and_the_bills_grid_picks_it_up()
    {
        var id = (await Ok(HttpMethod.Post, "/api/v1/recurring", new
        {
            name = "Trash pickup", categoryId = Id("categories", "Utilities"), expectedMinCents = 4500, expectedMaxCents = 5500, frequency = "monthly", dueDay = 20, firstDueOn = "2026-01-20",
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        (await _api.Get("/api/v1/bills?year=2026")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()).Should().Contain("Trash pickup");

        await Ok(HttpMethod.Patch, $"/api/v1/recurring/{id}", new { expectedMaxCents = 6000, isActive = false, endedOn = "2026-02-28" });
        var row = (await _api.Get("/api/v1/recurring")).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == id);
        row.GetProperty("expectedMaxCents").GetInt64().Should().Be(6000);
        row.GetProperty("isActive").GetBoolean().Should().BeFalse();
        row.GetProperty("endedOn").GetString().Should().Be("2026-02-28");
        row.GetProperty("expectedMinCents").GetInt64().Should().Be(4500, "what was not sent is left alone");
    }

    [Fact]
    public async Task Bad_recurring_items_are_refused()
    {
        var utilities = Id("categories", "Utilities");
        await Refused(HttpMethod.Post, "/api/v1/recurring", new { name = "X", categoryId = utilities, expectedMinCents = 5000, expectedMaxCents = 4000, dueDay = 5, firstDueOn = "2026-01-05" }, HttpStatusCode.BadRequest, "highest");
        await Refused(HttpMethod.Post, "/api/v1/recurring", new { name = "X", categoryId = utilities, expectedMinCents = 1, expectedMaxCents = 2, dueDay = 40, firstDueOn = "2026-01-05" }, HttpStatusCode.BadRequest, "due day");
        await Refused(HttpMethod.Post, "/api/v1/recurring", new { name = "X", categoryId = 99999, expectedMinCents = 1, expectedMaxCents = 2, dueDay = 5, firstDueOn = "2026-01-05" }, HttpStatusCode.BadRequest, "category");
        await Refused(HttpMethod.Post, "/api/v1/recurring", new { name = "X", categoryId = utilities, expectedMinCents = 1, expectedMaxCents = 2, dueDay = 5, frequency = "weekly", firstDueOn = "2026-01-05" }, HttpStatusCode.BadRequest, "monthly or yearly");
        await Refused(HttpMethod.Post, "/api/v1/recurring", new { name = "City Power", categoryId = utilities, expectedMinCents = 1, expectedMaxCents = 2, dueDay = 5, firstDueOn = "2026-01-05" }, HttpStatusCode.Conflict);
    }

    // ---- app settings --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_one_time_threshold_defaults_to_five_hundred_dollars_and_changes_what_the_bills_page_lists()
    {
        (await _api.Get("/api/v1/settings")).GetProperty("oneTimeAttentionCents").GetInt64().Should().Be(50000);
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, category_id, type, created_at, updated_at)
            VALUES (1, '2026-03-20', -30000, 'DENTIST', 'manual:dent', 'manual', 1, 'one_time', '2026-04-01T00:00:00Z', '2026-04-01T00:00:00Z')
            """);
        (await _api.Get("/api/v1/bills?year=2026")).GetProperty("oneTimeAttention").GetProperty("items").GetArrayLength().Should().Be(0);

        await Ok(HttpMethod.Put, "/api/v1/settings", new { oneTimeAttentionCents = 20000 });

        var attention = (await _api.Get("/api/v1/bills?year=2026")).GetProperty("oneTimeAttention");
        attention.GetProperty("thresholdCents").GetInt64().Should().Be(20000);
        attention.GetProperty("items").GetArrayLength().Should().Be(1);
        AuditCount("settings.updated").Should().Be(1);
        await Refused(HttpMethod.Put, "/api/v1/settings", new { oneTimeAttentionCents = -1 }, HttpStatusCode.BadRequest);
    }

    // ---- who may change what -------------------------------------------------------------------------------

    [Fact]
    public async Task A_member_can_edit_categories_tags_recurring_and_rules_but_not_accounts_patterns_or_settings_and_a_viewer_nothing()
    {
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Role.Viewer);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);

        (await member.PostAsJsonAsync("/api/v1/categories", new { name = "Gifts", defaultType = "extra" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await member.PostAsJsonAsync("/api/v1/tags", new { name = "birthday" })).StatusCode.Should().Be(HttpStatusCode.Created);
        (await member.GetAsync("/api/v1/rules")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await member.PostAsJsonAsync("/api/v1/accounts", new { name = "X", kind = "checking" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/move-patterns", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.GetAsync("/api/v1/move-patterns")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync("/api/v1/settings", new { oneTimeAttentionCents = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await viewer.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/v1/categories", new { name = "Nope", defaultType = "extra" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/v1/rules", new { name = "Nope" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
