using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Core;
using Tideline.Data.Services;
using Tideline.Notify;

namespace Tideline.Api.Tests;

/// <summary>Find my bills: the bills and subscriptions hiding in untracked lines, adding them, and the three bill coverage suggestions.</summary>
public sealed class FindMyBillsTests : IDisposable
{
    private readonly ApiFixture _api = new(importFixtures: false);
    private int _n;

    // the test app's clock reads 2026-04-01
    private const string Today = "2026-04-01";

    public void Dispose() => _api.Dispose();

    private long Cat(string name, string type)
    {
        _api.Execute("INSERT OR IGNORE INTO categories (name, default_type) VALUES (@name, @type)", new { name, type });
        return _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });
    }

    private void Line(string date, long cents, string description, string? payee, string category, string categoryType, string type, string account = "Checking")
    {
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, payee, external_id, source, category_id, type, created_at, updated_at)
            VALUES ((SELECT id FROM accounts WHERE name = @account), @date, @cents, @description, @payee, @external, 'manual', @categoryId, @type, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
            """, new { account, date, cents, description, payee, external = $"manual:b{++_n}", categoryId = Cat(category, categoryType), type });
    }

    private void Monthly(string description, string? payee, string category, string categoryType, string type, long cents, int day, params string[] months) =>
        months.ToList().ForEach(m => Line($"{m}-{day:00}", -cents, description, payee, category, categoryType, type));

    private static readonly string[] LastFour = ["2025-12", "2026-01", "2026-02", "2026-03"];

    private void NothingTrackedYet() => _api.Execute("UPDATE recurring SET is_active = 0");

    private async Task<List<JsonElement>> Candidates() => (await _api.Get("/api/v1/recurring/candidates")).EnumerateArray().ToList();

    private static string Name(JsonElement c) => c.GetProperty("name").GetString()!;

    [Fact]
    public async Task Bills_subscriptions_every_other_month_and_yearly_charges_are_found_and_spending_that_is_not_regular_is_not()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        Monthly("TUNES PLUS USA", "Tunes Plus", "Streaming", "extra", "extra", 1_099, 12, LastFour);
        Monthly("WATER AUTHORITY", "Water Authority", "Utilities", "bill", "bill", 6_000, 5, "2025-09", "2025-11", "2026-01", "2026-03");
        Line("2025-03-15", -61_200, "HOME INSURANCE CO", "Home Insurance Co", "Insurance", "bill", "bill");
        Line("2026-03-15", -63_000, "HOME INSURANCE CO", "Home Insurance Co", "Insurance", "bill", "bill");
        Line("2026-04-01", 0, "ignore", null, "Insurance", "bill", "bill"); // a $0 line is not a payment
        Monthly("GROCERY STORE", "Grocery Store", "Groceries", "required", "required", 8_500, 3, "2026-02", "2026-03"); // two months only
        Line("2026-03-02", -4_000, "CORNER DINER", "Corner Diner", "Dining", "extra", "extra");
        Line("2026-03-09", -3_000, "CORNER DINER", "Corner Diner", "Dining", "extra", "extra");
        Line("2026-03-16", -9_000, "CORNER DINER", "Corner Diner", "Dining", "extra", "extra"); // three in one month, no rhythm

        var found = (await Candidates()).ToDictionary(Name);

        found.Keys.Should().BeEquivalentTo("Metro Electric", "Tunes Plus", "Water Authority", "Home Insurance Co");
        found["Metro Electric"].GetProperty("kind").GetString().Should().Be("bill");
        found["Metro Electric"].GetProperty("frequency").GetString().Should().Be("monthly");
        found["Metro Electric"].GetProperty("dueDay").GetInt32().Should().Be(8);
        found["Metro Electric"].GetProperty("occurrences").GetInt32().Should().Be(4);
        found["Tunes Plus"].GetProperty("kind").GetString().Should().Be("subscription");
        found["Water Authority"].GetProperty("everyN").GetInt32().Should().Be(2);
        found["Home Insurance Co"].GetProperty("frequency").GetString().Should().Be("yearly");
        found["Metro Electric"].GetProperty("linksNextOnes").GetBoolean().Should().BeTrue();
        (await Candidates()).Select(Name).Should().Equal(["Metro Electric", "Home Insurance Co", "Water Authority", "Tunes Plus"], "bills come before subscriptions, dearest a month first");
    }

    [Fact]
    public async Task What_is_already_tracked_linked_uncategorized_one_time_or_left_out_is_not_offered()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        Monthly("OLD GYM", "Old Gym", "Fitness", "extra", "extra", 3_000, 4, LastFour);
        Monthly("MYSTERY", "Mystery", LineClassifier.UncategorizedName, "extra", "extra", 2_000, 6, LastFour);
        Monthly("A LOAN", "Big Loan Co", "Loans", "bill", "one_time", 5_000, 7, LastFour);
        Monthly("TRACKED CO", "Tracked Co", "Utilities", "bill", "bill", 7_000, 9, LastFour);
        _api.Execute("INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, is_active) VALUES ('Tracked Co', (SELECT id FROM categories WHERE name = 'Utilities'), 6000, 8000, 'monthly', 1, 9, '2025-12-09', 3, 1)");
        _api.Execute("UPDATE transactions SET left_out_at = '2026-03-20T00:00:00Z' WHERE original_description = 'OLD GYM'");

        (await Candidates()).Select(Name).Should().Equal("Metro Electric");

        _api.Execute("UPDATE transactions SET recurring_id = (SELECT id FROM recurring WHERE name = 'Tracked Co') WHERE original_description = 'METRO ELECTRIC 8841'");
        (await Candidates()).Should().BeEmpty("lines that are linked already belong to an item");
    }

    [Fact]
    public async Task Adding_a_bill_makes_the_item_links_its_past_lines_and_adds_a_rule_that_links_the_next_one()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        var key = (await Candidates()).Single().GetProperty("key").GetString();

        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/recurring/candidates/add", new { items = new[] { new { key, name = "Electric" } } });

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var added = body.GetProperty("added").EnumerateArray().Single();
        added.GetProperty("name").GetString().Should().Be("Electric", "the name can be changed before adding");
        added.GetProperty("linesLinked").GetInt32().Should().Be(4);
        var id = added.GetProperty("recurringId").GetInt64();
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE recurring_id = @id", new { id }).Should().Be(4);
        using (var conn = _api.Open())
        {
            var item = conn.QuerySingle<(string Frequency, int EveryN, int DueDay, long Min, long Max, string First)>(
                "SELECT frequency, every_n, due_day, expected_min_cents, expected_max_cents, first_due_on FROM recurring WHERE id = @id", new { id });
            item.Should().Be(("monthly", 1, 8, 12_796L, 15_640L, "2025-12-08"));
        }
        (await Candidates()).Should().BeEmpty("it is tracked now");

        // next month's line is linked as it arrives, by the rule that was added
        var rule = _api.Scalar<long>("SELECT id FROM rules WHERE set_recurring_id = @id", new { id });
        _api.Scalar<string>("SELECT name FROM rules WHERE id = @rule", new { rule }).Should().Be("Bill: Electric");
        Line("2026-04-08", -14_500, "METRO ELECTRIC 9920 ONLINE", null, "Uncategorized", "extra", "extra");
        var next = _api.IdOf("METRO ELECTRIC 9920 ONLINE");
        (await _api.Send(HttpMethod.Post, $"/api/v1/transactions/{next}/reset-to-rules")).Status.Should().Be(HttpStatusCode.OK);
        _api.Scalar<long?>("SELECT recurring_id FROM transactions WHERE id = @next", new { next }).Should().Be(id);

        (await _api.Get("/api/v1/audit?action=recurring.created")).GetProperty("items").GetArrayLength().Should().Be(1);
        (await _api.Get("/api/v1/audit?action=recurring.lines_linked")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_name_that_is_taken_gets_a_number_and_a_stale_or_missing_pick_is_refused_in_words()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        _api.Execute("INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, is_active) VALUES ('Electric', (SELECT id FROM categories WHERE name = 'Utilities'), 1, 2, 'monthly', 1, 9, '2025-12-09', 3, 1)");
        var key = (await Candidates()).Single().GetProperty("key").GetString();

        var (status, body) = await _api.Send(HttpMethod.Post, "/api/v1/recurring/candidates/add", new { items = new[] { new { key, name = "Electric" } } });
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        body.GetProperty("added")[0].GetProperty("name").GetString().Should().Be("Electric (2)");

        (await _api.Send(HttpMethod.Post, "/api/v1/recurring/candidates/add", new { items = new[] { new { key = "12345", name = (string?)null } } })).Status.Should().Be(HttpStatusCode.NotFound);
        var none = await _api.Send(HttpMethod.Post, "/api/v1/recurring/candidates/add", new { items = Array.Empty<object>() });
        none.Status.Should().Be(HttpStatusCode.BadRequest);
        none.Body.GetProperty("detail").GetString().Should().Contain("Pick at least one");
    }

    [Fact]
    public async Task A_viewer_can_see_what_was_found_but_not_add_it()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        var viewer = _api.AddUser("view3@example.com", "Viewer", Tideline.Core.Security.Role.Viewer);
        var client = await _api.SignInAsync("view3@example.com", ApiFixture.Password, viewer.Secret);

        (await client.GetAsync("/api/v1/recurring/candidates")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/recurring/candidates/add", new { items = new[] { new { key = "1" } } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- the three coverage suggestions ---------------------------------------------------------------------------

    private List<(string Kind, string Subject, string Message)> Run(string today = Today)
    {
        using var conn = _api.Open();
        new Evaluator().Run(conn, DateOnly.ParseExact(today, "yyyy-MM-dd"), TimeProvider.System);
        return conn.Query<(string Kind, string Subject, long Id, string Message)>("SELECT kind, subject_type, subject_id, message FROM flags WHERE resolved_at IS NULL")
            .Select(f => (f.Kind, f.Subject, f.Message)).ToList();
    }

    private static List<(string Kind, string Subject, string Message)> Only(List<(string Kind, string Subject, string Message)> all, params string[] kinds) =>
        all.Where(c => kinds.Contains(c.Kind)).ToList();

    [Fact]
    public void Nothing_is_marked_as_a_bill_waits_for_a_month_of_lines_and_clears_when_a_bill_exists()
    {
        Only(Run(), FlagKinds.NoBills, FlagKinds.NoRecurring).Should().BeEmpty("no lines at all yet");

        Line("2026-03-20", -2_000, "SOMETHING", null, "Dining", "extra", "extra");
        Only(Run("2026-03-25"), FlagKinds.NoBills).Should().BeEmpty("not a full month of lines yet");

        Line("2025-12-20", -2_000, "OLDER", null, "Dining", "extra", "extra");
        Only(Run(), FlagKinds.NoBills).Should().ContainSingle();

        Line("2026-03-21", -9_000, "A BILL", null, "Utilities", "bill", "bill");
        Only(Run(), FlagKinds.NoBills).Should().BeEmpty();
    }

    [Fact]
    public void Bills_but_no_recurring_items_is_raised_and_says_how_many_could_be_tracked_and_clears_once_one_exists()
    {
        NothingTrackedYet();
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);

        var card = Only(Run(), FlagKinds.NoRecurring).Should().ContainSingle().Subject;
        card.Message.Should().Contain("4 bill lines").And.Contain("1 look like");

        _api.Execute("INSERT INTO recurring (name, category_id, expected_min_cents, expected_max_cents, frequency, every_n, due_day, first_due_on, grace_days, is_active) VALUES ('Any', (SELECT id FROM categories WHERE name = 'Utilities'), 1, 2, 'monthly', 1, 9, '2025-12-09', 3, 1)");
        Only(Run(), FlagKinds.NoRecurring).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_bill_found_is_its_own_card_and_its_button_adds_it_and_clears_the_card()
    {
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        Monthly("TUNES PLUS USA", "Tunes Plus", "Streaming", "extra", "extra", 1_099, 12, LastFour);

        var cards = Only(Run(), FlagKinds.RecurringCandidate);
        cards.Should().HaveCount(2);
        cards.Select(c => c.Subject).Should().OnlyContain(s => s == "candidate");
        cards.Single(c => c.Message.StartsWith("Metro Electric")).Message.Should().Contain("looks like a bill").And.Contain("$142.18 a month").And.Contain("4 times");
        cards.Single(c => c.Message.StartsWith("Tunes Plus")).Message.Should().Contain("looks like a subscription");

        var flag = _api.Scalar<long>("SELECT id FROM flags WHERE kind = 'recurring_candidate' AND message LIKE 'Metro Electric%'");
        var done = await _api.Send(HttpMethod.Post, "/api/v1/recommendations/action", new { flagId = flag });

        done.Status.Should().Be(HttpStatusCode.OK, done.Body.ToString());
        done.Body.GetProperty("added").GetString().Should().Be("Metro Electric");
        done.Body.GetProperty("linked").GetInt32().Should().Be(4);
        _api.Scalar<long>("SELECT COUNT(*) FROM recurring WHERE name = 'Metro Electric'").Should().Be(1);
        _api.Scalar<string?>("SELECT resolved_at FROM flags WHERE id = @flag", new { flag }).Should().NotBeNull();
        Only(Run(), FlagKinds.RecurringCandidate).Should().ContainSingle().Which.Message.Should().StartWith("Tunes Plus");
    }

    [Fact]
    public void The_three_can_each_be_switched_off()
    {
        NothingTrackedYet();
        Monthly("METRO ELECTRIC 8841", "Metro Electric", "Utilities", "bill", "bill", 14_218, 8, LastFour);
        foreach (var k in new[] { FlagKinds.NoRecurring, FlagKinds.RecurringCandidate })
            _api.Execute("UPDATE recommendation_rules SET is_active = 0 WHERE flag_kind = @k", new { k });

        Only(Run(), FlagKinds.NoRecurring, FlagKinds.RecurringCandidate).Should().BeEmpty();
    }
}
