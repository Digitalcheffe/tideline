using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Core.Security;
using Tideline.Import.SimpleFin;

namespace Tideline.Api.Tests;

public sealed class SimpleFinTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private static string Token(string claim = "https://bridge.example.test/simplefin/claim/abc123") => Convert.ToBase64String(Encoding.UTF8.GetBytes(claim));

    private long Id(string table, string name) => _api.Scalar<long>($"SELECT id FROM {table} WHERE name = @name", new { name });

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    private async Task Connect() => await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = Token() }, HttpStatusCode.Created);

    private static long Unix(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeSeconds();

    private static object Tx(string id, string posted, string amount, string description, bool pending = false) =>
        new { id, posted = Unix(posted), amount, description, pending };

    private void Remote(string id, string name, string balance, string balanceDate, IEnumerable<object> transactions, params string[] errors) =>
        _api.SimpleFin.AccountsJson = JsonSerializer.Serialize(new
        {
            errlist = errors.Select(e => new { code = "gen.error", msg = e }),
            accounts = new[] { new { org = new { name = "Truist" }, id, name, currency = "USD", balance, balanceDate = Unix(balanceDate), transactions } },
        }).Replace("balanceDate", "balance-date");

    private async Task Match(string ours, string remote, string takesOver = "2026-04-01") =>
        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{Id("accounts", ours)}", new { simplefinAccountId = remote, simplefinFromOn = takesOver });

    private long LedgerBalance(string account) => _api.Scalar<long>("SELECT COALESCE(opening_balance_cents, 0) + COALESCE((SELECT SUM(amount_cents) FROM transactions WHERE account_id = accounts.id), 0) FROM accounts WHERE name = @account", new { account });

    // ---- the connection ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_setup_token_is_claimed_once_and_the_access_address_is_sealed_and_never_shown()
    {
        await Connect();

        _api.SimpleFin.Requests.Single().Url.ToString().Should().Be("https://bridge.example.test/simplefin/claim/abc123");
        var stored = _api.Scalar<string>("SELECT access_url_enc FROM simplefin_connections");
        stored.Should().NotContain("demopass").And.NotContain("bridge.example.test");
        var status = await Ok(HttpMethod.Get, "/api/v1/imports/simplefin");
        status.GetProperty("connected").GetBoolean().Should().BeTrue();
        status.ToString().Should().NotContain("demopass");
        (await _api.Get("/api/v1/audit")).ToString().Should().NotContain("demopass").And.Contain("simplefin.connected");
    }

    [Fact]
    public async Task A_token_that_was_already_used_says_so_in_plain_words_and_a_bad_one_is_refused()
    {
        _api.SimpleFin.ClaimStatus = HttpStatusCode.Forbidden;
        var used = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = Token() }, HttpStatusCode.BadRequest);
        used.GetProperty("detail").GetString().Should().Contain("already claimed").And.Contain("someone else may have");

        var junk = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = "not a token!!" }, HttpStatusCode.BadRequest);
        junk.GetProperty("detail").GetString().Should().Contain("setup token");
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = Token("http://insecure.example.test/claim") }, HttpStatusCode.BadRequest))
            .GetProperty("detail").GetString().Should().Contain("secure");
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = "" }, HttpStatusCode.BadRequest)).GetProperty("detail").GetString().Should().Contain("Paste");
    }

    [Fact]
    public async Task Disconnecting_forgets_the_address_and_stops_syncing()
    {
        await Connect();

        await Ok(HttpMethod.Delete, "/api/v1/imports/simplefin");

        (await Ok(HttpMethod.Get, "/api/v1/imports/simplefin")).GetProperty("connected").GetBoolean().Should().BeFalse();
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { }, HttpStatusCode.NotFound)).GetProperty("detail").GetString().Should().Contain("not connected");
        (await _api.Get("/api/v1/audit?action=simplefin.disconnected")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ---- matching accounts -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Their_accounts_are_listed_with_a_suggestion_and_matching_needs_a_takeover_day_and_is_one_to_one()
    {
        await Connect();
        Remote("ACT-1", "Everyday Checking 1111", "1895.51", "2026-04-01T12:00:00Z", []);

        var remote = await Ok(HttpMethod.Get, "/api/v1/imports/simplefin/accounts");
        remote[0].GetProperty("id").GetString().Should().Be("ACT-1");
        remote[0].GetProperty("balanceCents").GetInt64().Should().Be(189_551);
        remote[0].GetProperty("suggestedAccountId").GetInt64().Should().Be(Id("accounts", "Checking"));

        var checking = Id("accounts", "Checking");
        var (status, json) = await _api.Send(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { simplefinAccountId = "ACT-1" });
        status.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("detail").GetString().Should().Contain("takes over");

        await Match("Checking", "ACT-1");
        var again = await _api.Send(HttpMethod.Patch, $"/api/v1/accounts/{Id("accounts", "Savings")}", new { simplefinAccountId = "ACT-1", simplefinFromOn = "2026-04-01" });
        again.Status.Should().Be(HttpStatusCode.BadRequest);
        again.Body.GetProperty("detail").GetString().Should().Contain("already matched");

        (await Ok(HttpMethod.Get, "/api/v1/imports/simplefin/accounts"))[0].GetProperty("mappedAccountName").GetString().Should().Be("Checking");
        var accounts = await _api.Get("/api/v1/accounts");
        accounts.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Checking").GetProperty("simplefinFromOn").GetString().Should().Be("2026-04-01");
    }

    [Fact]
    public async Task An_account_is_suggested_from_the_last_four_digits_in_its_name_even_when_the_words_say_nothing()
    {
        await Connect();
        Remote("ACT-9", "Rainy day money 3333", "10.00", "2026-04-01T12:00:00Z", []); // no account is called anything like that; 3333 is the savings account's number

        var remote = await Ok(HttpMethod.Get, "/api/v1/imports/simplefin/accounts");

        remote[0].GetProperty("suggestedAccountId").GetInt64().Should().Be(Id("accounts", "Savings"));

        Remote("ACT-8", "Rainy day money 33334", "10.00", "2026-04-01T12:00:00Z", []); // five digits in a row are not a last four
        (await Ok(HttpMethod.Get, "/api/v1/imports/simplefin/accounts"))[0].GetProperty("suggestedAccountId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- syncing -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sync_asks_for_ninety_day_windows_with_the_credentials_in_a_header_and_puts_new_lines_through_the_rules()
    {
        await Connect();
        await Match("Checking", "ACT-1");
        var before = LedgerBalance("Checking");
        Remote("ACT-1", "Checking", ((before - 2_199 + 10_000) / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), "2026-04-01T12:00:00Z",
        [
            Tx("t0", "2026-03-30T10:00:00Z", "-5.00", "BEFORE THE CUTOVER"),
            Tx("t1", "2026-04-01T09:00:00Z", "-21.99", "SPOTIFY USA"),
            Tx("t2", "2026-04-01T10:00:00Z", "100.00", "SOME DEPOSIT"),
            Tx("t3", "2026-04-01T11:00:00Z", "-9.99", "STILL PENDING", pending: true),
        ], "Bank login needs attention");

        var report = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        var checking = report.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountName").GetString() == "Checking");
        checking.GetProperty("inserted").GetInt32().Should().Be(2);
        checking.GetProperty("pendingSkipped").GetInt32().Should().Be(1);
        checking.GetProperty("beforeCutover").GetInt32().Should().Be(1);
        checking.GetProperty("balanceMatches").GetBoolean().Should().BeTrue("the bank's balance and the ledger agree");
        report.GetProperty("errors")[0].GetString().Should().Be("Bank login needs attention");
        report.GetProperty("requests").GetInt32().Should().Be(2, "95 days is two windows");

        var gets = _api.SimpleFin.Requests.Where(r => r.Method == HttpMethod.Get).ToList();
        gets.Should().HaveCount(2);
        gets.Should().OnlyContain(r => r.Url.Query.Contains("version=2") && r.Url.Query.Contains("start-date=") && r.Url.Query.Contains("end-date="));
        gets.Should().OnlyContain(r => !r.Url.ToString().Contains("demopass") && r.Url.Host == "bridge.example.test" && r.Url.AbsolutePath == "/simplefin/accounts");
        gets.Should().OnlyContain(r => r.Auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("demouser:demopass")));

        // the new lines are real ledger lines with the SimpleFIN id, source and rules applied
        using var conn = _api.Open();
        var spotify = conn.QuerySingle<(string External, string Source, long? Category)>("SELECT external_id, source, category_id FROM transactions WHERE original_description = 'SPOTIFY USA' AND posted_on = '2026-04-01'");
        spotify.Source.Should().Be("simplefin");
        spotify.External.Should().MatchRegex(@"^sfin:\d+:ACT-1:t1$");
        spotify.Category.Should().NotBeNull().And.NotBe(Id("categories", "Uncategorized"));
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE original_description IN ('BEFORE THE CUTOVER', 'STILL PENDING')").Should().Be(0);

        var status = await Ok(HttpMethod.Get, "/api/v1/imports/simplefin");
        status.GetProperty("lastError").GetString().Should().Be("Bank login needs attention", "the same message from both windows is told once");
        status.GetProperty("requestsToday").GetInt32().Should().Be(2);
        _api.Scalar<long>("SELECT COUNT(*) FROM system_events WHERE source = 'simplefin' AND level = 'warn'").Should().Be(1);
        (await Ok(HttpMethod.Get, "/api/v1/imports")).GetProperty("batches").EnumerateArray().Should().Contain(b => b.GetProperty("source").GetString() == "simplefin");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'simplefin.sync'").Should().Be(1);
    }

    [Fact]
    public async Task Syncing_again_adds_nothing_and_a_late_posting_in_the_overlap_is_still_picked_up()
    {
        await Connect();
        await Match("Checking", "ACT-1");
        Remote("ACT-1", "Checking", "0.00", "2026-04-01T12:00:00Z", [Tx("t1", "2026-04-01T09:00:00Z", "-21.99", "SPOTIFY USA")]);
        await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });
        var count = _api.Scalar<long>("SELECT COUNT(*) FROM transactions");

        var second = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(count);
        second.GetProperty("accounts")[0].GetProperty("inserted").GetInt32().Should().Be(0);
        second.GetProperty("accounts")[0].GetProperty("existing").GetInt32().Should().Be(1);

        // a line that posted a few days late shows up in the next window and is added
        Remote("ACT-1", "Checking", "0.00", "2026-04-01T12:00:00Z",
            [Tx("t1", "2026-04-01T09:00:00Z", "-21.99", "SPOTIFY USA"), Tx("t9", "2026-04-01T13:00:00Z", "-3.00", "LATE ONE")]);
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { })).GetProperty("accounts")[0].GetProperty("inserted").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_difference_from_the_bank_after_a_sync_is_reported_and_raises_the_balance_flag()
    {
        await Connect();
        await Match("Checking", "ACT-1");
        Remote("ACT-1", "Checking", "1.00", "2026-04-01T12:00:00Z", [Tx("t1", "2026-04-01T09:00:00Z", "-21.99", "SPOTIFY USA")]);

        var report = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        report.GetProperty("accounts")[0].GetProperty("balanceMatches").GetBoolean().Should().BeFalse();
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().Should().Contain(f => f.GetProperty("kind").GetString() == "balance_mismatch" && f.GetProperty("title").GetString()!.StartsWith("Checking"));
    }

    [Fact]
    public async Task More_than_twenty_four_requests_in_a_day_stops_the_sync()
    {
        await Connect();
        await Match("Checking", "ACT-1");
        Remote("ACT-1", "Checking", "0.00", "2026-04-01T12:00:00Z", []);
        _api.Execute("INSERT INTO settings (key, value, updated_at) VALUES ('simplefin_requests:2026-04-01', '23', '2026-04-01T00:00:00Z')");

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        json.GetProperty("detail").GetString().Should().Contain("limit").And.Contain("bug");
        _api.SimpleFin.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(0);
    }

    [Fact]
    public async Task When_simplefin_refuses_the_connection_the_message_says_what_to_do()
    {
        await Connect();
        await Match("Checking", "ACT-1");
        _api.SimpleFin.AccountsStatus = HttpStatusCode.Forbidden;

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        json.GetProperty("detail").GetString().Should().Contain("new setup token");
        (await Ok(HttpMethod.Get, "/api/v1/imports/simplefin")).GetProperty("lastError").GetString().Should().Contain("new setup token");
    }

    [Fact]
    public async Task Nothing_is_synced_until_an_account_is_matched()
    {
        await Connect();

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });

        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        json.GetProperty("detail").GetString().Should().Contain("matched");
    }

    // ---- the dry run ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_sets_simplefin_beside_the_csv_for_the_same_days_and_writes_nothing()
    {
        await Connect();
        await Match("Checking", "ACT-1", "2026-04-01");
        var checking = Id("accounts", "Checking");

        // what SimpleFIN would say about March: the same lines, except one described differently, one missing, and one extra
        using var conn = _api.Open();
        var rows = conn.Query<(string On, long Cents, string Description)>("""
            SELECT posted_on, amount_cents, original_description FROM transactions WHERE account_id = @checking ORDER BY posted_on, id
            """, new { checking }).ToList();
        var power = rows.First(r => r.Description.StartsWith("CITY POWER"));
        var gone = rows.First(r => r.Description.StartsWith("SPOTIFY") == false && r != power && r.Cents < 0);
        var remote = rows.Where(r => r != gone).Select((r, i) => Tx($"d{i}", r.On + "T12:00:00Z", (r.Cents / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            r == power ? "CITYPOWER" : r.Description)).Append(Tx("extra", "2026-03-20T12:00:00Z", "-1.11", "SOMETHING NEW")).ToList();
        Remote("ACT-1", "Checking", "0.00", "2026-04-01T12:00:00Z", remote);
        var count = _api.Scalar<long>("SELECT COUNT(*) FROM transactions");

        var report = await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/dry-run", new { accountId = checking, from = "2026-03-01", to = "2026-03-31" });

        report.GetProperty("accountName").GetString().Should().Be("Checking");
        report.GetProperty("simpleFinRows").GetInt32().Should().Be(remote.Count);
        report.GetProperty("matched").GetInt32().Should().Be(rows.Count - 1);
        var different = report.GetProperty("different").EnumerateArray().Single();
        different.GetProperty("description").GetString().Should().Be("CITYPOWER");
        different.GetProperty("ledgerDescription").GetString().Should().Be(power.Description);
        different.GetProperty("difference").GetString().Should().Contain("becomes");
        report.GetProperty("onlyInSimpleFin").EnumerateArray().Single().GetProperty("description").GetString().Should().Be("SOMETHING NEW");
        report.GetProperty("onlyInLedger").EnumerateArray().Single().GetProperty("description").GetString().Should().Be(gone.Description);
        report.GetProperty("sameMeaning").GetInt32().Should().Be(rows.Count - 2);

        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(count, "a dry run writes nothing");
    }

    [Fact]
    public async Task A_dry_run_refuses_a_bad_period_or_an_unmatched_account()
    {
        await Connect();
        var checking = Id("accounts", "Checking");

        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/dry-run", new { accountId = checking, from = "2026-03-01", to = "2026-03-31" }, HttpStatusCode.BadRequest))
            .GetProperty("detail").GetString().Should().Contain("not matched");
        await Match("Checking", "ACT-1");
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/dry-run", new { accountId = checking, from = "2026-01-01", to = "2026-06-30" }, HttpStatusCode.BadRequest))
            .GetProperty("detail").GetString().Should().Contain("90 days");
        (await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/dry-run", new { accountId = checking, from = "March", to = "2026-03-31" }, HttpStatusCode.BadRequest))
            .GetProperty("detail").GetString().Should().Contain("dates");
    }

    // ---- the schedule and who may --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_schedule_syncs_when_connected_and_then_waits_six_hours()
    {
        var host = _api.Service<SimpleFinHost>();
        (await host.TickAsync()).Should().BeNull("not connected, nothing to do");

        await Connect();
        await Match("Checking", "ACT-1");
        Remote("ACT-1", "Checking", "0.00", "2026-04-01T12:00:00Z", []);

        (await host.TickAsync()).Should().NotBeNull();
        (await host.TickAsync()).Should().BeNull("six hours have not passed");
        _api.Clock.Advance(TimeSpan.FromHours(6.5));
        (await host.TickAsync()).Should().NotBeNull();

        SimpleFinHost.MinuteFor(1).Should().BeInRange(17, 23);
        SimpleFinHost.MinuteFor(1).Should().Be(SimpleFinHost.MinuteFor(1), "the same each time");
    }

    [Fact]
    public async Task Only_an_owner_can_connect_sync_or_look()
    {
        var (_, secret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, secret);

        (await member.GetAsync("/api/v1/imports/simplefin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/imports/simplefin/connect", new { setupToken = Token() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/imports/simplefin/sync", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/imports/simplefin/dry-run", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
