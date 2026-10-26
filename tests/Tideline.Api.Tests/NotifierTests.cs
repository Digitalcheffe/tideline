using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Tideline.Core.Security;
using Tideline.Notify;

namespace Tideline.Api.Tests;

public sealed class NotifierTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Id(string table, string name) => _api.Scalar<long>($"SELECT id FROM {table} WHERE name = @name", new { name });

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    private async Task<long> Webhook(string url = "https://hooks.example.test/hook/abc123secret") =>
        (await Ok(HttpMethod.Post, "/api/v1/notify/channels", new { kind = "webhook", name = "Chat", config = new { url } }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

    private Task Tick() => _api.Service<NotifyHost>().TickAsync();

    private async Task OwesMoney(string description = "AFFIRM PAYMENT 1")
    {
        await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = Id("accounts", "Checking"), postedOn = "2026-03-28", amountCents = -4_000, description, type = "required" }, HttpStatusCode.Created);
    }

    // ---- channels ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_channels_settings_are_sealed_never_shown_and_never_in_the_audit_log()
    {
        var id = await Webhook();

        var list = await Ok(HttpMethod.Get, "/api/v1/notify/channels");
        list[0].GetProperty("summary").GetString().Should().Be("posts to hooks.example.test");
        list[0].ToString().Should().NotContain("abc123secret");
        _api.Scalar<string>("SELECT config_enc FROM notification_channels WHERE id = @id", new { id }).Should().NotContain("hooks.example.test").And.NotContain("abc123secret");

        await Ok(HttpMethod.Patch, $"/api/v1/notify/channels/{id}", new { name = "Chat room", config = new { url = "https://hooks.example.test/hook/newsecret999" } });
        var audit = (await _api.Get("/api/v1/audit?entity=notify_channel")).ToString();
        audit.Should().NotContain("abc123secret").And.NotContain("newsecret999").And.Contain("settingsReplaced");
        (await Ok(HttpMethod.Get, "/api/v1/notify/channels"))[0].GetProperty("name").GetString().Should().Be("Chat room");
    }

    [Fact]
    public async Task Bad_channels_are_refused_with_a_reason()
    {
        async Task Refused(object body, string mention)
        {
            var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/notify/channels", body);
            status.Should().Be(HttpStatusCode.BadRequest, json.ToString());
            json.GetProperty("detail").GetString().Should().Contain(mention);
        }

        await Refused(new { kind = "carrier pigeon", name = "x", config = new { } }, "webhook or email");
        await Refused(new { kind = "webhook", name = "", config = new { url = "https://x.test" } }, "name");
        await Refused(new { kind = "webhook", name = "x" }, "settings");
        await Refused(new { kind = "webhook", name = "x", config = new { url = "ftp://x.test" } }, "http or https");
        await Refused(new { kind = "email", name = "x", config = new { host = "smtp.test", port = 587, useSsl = true, from = "nobody", to = "me@x.test" } }, "addresses");
    }

    [Fact]
    public async Task A_channel_can_be_switched_off_and_removed_and_each_change_is_audited()
    {
        var id = await Webhook();

        await Ok(HttpMethod.Patch, $"/api/v1/notify/channels/{id}", new { isActive = false });
        await OwesMoney();
        await Tick();
        _api.Webhook.Sent.Should().BeEmpty("a channel that is off hears nothing");

        await Ok(HttpMethod.Delete, $"/api/v1/notify/channels/{id}");
        (await Ok(HttpMethod.Get, "/api/v1/notify/channels")).GetArrayLength().Should().Be(0);
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action LIKE 'notify_channel.%'").Should().Be(3);
    }

    // ---- sending -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Urgent_flags_go_out_once_with_the_agreed_payload_and_the_others_wait_for_the_summary()
    {
        await Webhook();
        await OwesMoney();

        await Tick();

        var sent = _api.Webhook.Sent;
        sent.Should().NotBeEmpty();
        sent.Select(m => m.FlagId).Should().OnlyHaveUniqueItems();
        sent.Should().OnlyContain(m => m.Kind != "review_needed" && m.Kind != "one_time_review" && m.Kind != "recurring_out_of_range", "those are for the summary");
        var debt = sent.Single(m => m.Kind == "new_debt");
        debt.Severity.Should().Be("alert");
        debt.Title.Should().Be("A payment that looks like a new loan");
        debt.Detail.Should().Contain("$40.00").And.Contain("AFFIRM PAYMENT 1");
        debt.Link.Should().StartWith("http://localhost:5173/lines?from=2026-03-01&to=2026-03-31");
        debt.FlagId.Should().BeGreaterThan(0);
        debt.At.Should().BeCloseTo(_api.Clock.GetUtcNow(), TimeSpan.FromMinutes(2));

        var count = sent.Count;
        await Tick();
        _api.Webhook.Sent.Count.Should().Be(count, "nothing is sent twice");

        // acknowledged ones are not sent either
        await OwesMoney("AFFIRM PAYMENT 2");
        var fresh = (await Ok(HttpMethod.Post, "/api/v1/notify/evaluate")).GetProperty("created").GetInt32();
        fresh.Should().BeGreaterThan(0);
        var newFlag = (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().First(f => f.GetProperty("detail").GetString()!.Contains("PAYMENT 2")).GetProperty("id").GetInt64();
        await Ok(HttpMethod.Post, $"/api/v1/flags/{newFlag}/acknowledge", new { });
        await Tick();
        _api.Webhook.Sent.Should().NotContain(m => m.FlagId == newFlag);
    }

    [Fact]
    public async Task Urgent_messages_wait_out_quiet_hours_and_go_when_they_end()
    {
        await Webhook();
        await Tick(); // everything that is open now has been sent
        var before = _api.Webhook.Sent.Count;

        _api.Clock.Advance(TimeSpan.FromHours(8.5)); // 22:30
        await OwesMoney();
        await Tick();
        _api.Webhook.Sent.Count.Should().Be(before, "21:00 to 07:00 is quiet");

        _api.Clock.Advance(TimeSpan.FromHours(8.5)); // 07:00 next day... and a bit
        await Tick();
        _api.Webhook.Sent.Skip(before).Should().Contain(m => m.Kind == "new_debt");
    }

    [Fact]
    public async Task A_message_that_cannot_be_sent_is_recorded_and_tried_again_without_repeats_once_it_works()
    {
        await Webhook();
        _api.Webhook.FailWith = "The webhook answered 502.";
        await OwesMoney();

        await Tick();

        _api.Webhook.Sent.Should().BeEmpty();
        _api.Scalar<long>("SELECT COUNT(*) FROM notifications_sent WHERE status = 'failed'").Should().BeGreaterThan(0);
        _api.Scalar<string>("SELECT error FROM notifications_sent WHERE status = 'failed' LIMIT 1").Should().Be("The webhook answered 502.");
        _api.Scalar<long>("SELECT COUNT(*) FROM system_events WHERE source = 'notify' AND level = 'warn'").Should().BeGreaterThan(0);

        _api.Webhook.FailWith = null;
        await Tick();
        _api.Webhook.Sent.Should().Contain(m => m.Kind == "new_debt");
        var sentAfterFix = _api.Webhook.Sent.Count;
        await Tick();
        _api.Webhook.Sent.Count.Should().Be(sentAfterFix);
    }

    [Fact]
    public async Task A_message_is_given_up_on_after_three_tries()
    {
        await Webhook();
        _api.Webhook.FailWith = "down";
        await OwesMoney();

        for (var i = 0; i < 6; i++) await Tick();

        _api.Scalar<long>("SELECT MAX(n) FROM (SELECT COUNT(*) AS n FROM notifications_sent WHERE flag_id IS NOT NULL GROUP BY flag_id, channel_id)").Should().Be(Notifier.MaxTries);
        _api.Scalar<long>("SELECT COUNT(*) FROM notifications_sent WHERE flag_id IS NULL").Should().Be(Notifier.MaxTries, "the summary is given up on too, not tried every minute");
    }

    [Fact]
    public async Task Many_new_alerts_in_one_run_are_one_message_not_a_flood()
    {
        await Webhook();
        for (var i = 1; i <= 7; i++) await OwesMoney($"AFFIRM PAYMENT {i}");

        await Tick();

        _api.Webhook.Sent.Where(m => m.Kind != "monthly_summary").Should().HaveCount(1);
        var digest = _api.Webhook.Sent.Single(m => m.Kind != "monthly_summary");
        digest.Kind.Should().Be("digest");
        digest.Title.Should().MatchRegex(@"^\d+ things need you$");
        digest.Detail.Should().Contain("A payment that looks like a new loan");
        _api.Scalar<long>("SELECT COUNT(DISTINCT flag_id) FROM notifications_sent WHERE status = 'sent'").Should().BeGreaterThan(5, "each flag is marked as told");
    }

    [Fact]
    public async Task What_each_kind_does_can_be_changed_and_the_change_is_audited_with_before_and_after()
    {
        await Webhook();
        var prefs = (await Ok(HttpMethod.Get, "/api/v1/notify/prefs")).EnumerateArray().ToDictionary(p => p.GetProperty("kind").GetString()!);
        prefs.Should().HaveCount(16);
        prefs["new_debt"].GetProperty("mode").GetString().Should().Be("immediate");
        prefs["budget_pop"].GetProperty("mode").GetString().Should().Be("summary");

        await Ok(HttpMethod.Put, "/api/v1/notify/prefs", new { modes = new Dictionary<string, string> { ["new_debt"] = "off", ["review_needed"] = "immediate" } });
        await OwesMoney();
        await Tick();

        _api.Webhook.Sent.Should().NotContain(m => m.Kind == "new_debt", "switched off");
        _api.Webhook.Sent.Should().Contain(m => m.Kind == "review_needed", "switched to immediate");
        var entry = (await _api.Get("/api/v1/audit?action=notify_prefs.updated")).GetProperty("items")[0];
        entry.GetProperty("beforeJson").GetString().Should().Contain("\"new_debt\":\"immediate\"");
        entry.GetProperty("afterJson").GetString().Should().Contain("\"new_debt\":\"off\"");

        var (status, json) = await _api.Send(HttpMethod.Put, "/api/v1/notify/prefs", new { modes = new Dictionary<string, string> { ["new_debt"] = "sometimes" } });
        status.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("detail").GetString().Should().Contain("not at all");
        (await _api.Send(HttpMethod.Put, "/api/v1/notify/prefs", new { modes = new Dictionary<string, string> { ["no_such_kind"] = "off" } })).Status.Should().Be(HttpStatusCode.BadRequest);

        // putting a kind back to its default stores nothing
        await Ok(HttpMethod.Put, "/api/v1/notify/prefs", new { modes = new Dictionary<string, string> { ["new_debt"] = "immediate" } });
        _api.Scalar<long>("SELECT COUNT(*) FROM notification_prefs WHERE flag_kind = 'new_debt'").Should().Be(0);
    }

    [Fact]
    public async Task A_test_message_shows_whether_a_channel_works_without_saying_why_in_secrets()
    {
        var id = await Webhook();

        var ok = await Ok(HttpMethod.Post, "/api/v1/notify/test", new { channelId = id });

        ok.GetProperty("ok").GetBoolean().Should().BeTrue();
        _api.Webhook.Sent.Single().Kind.Should().Be("test");

        _api.Webhook.FailWith = "The webhook answered 404.";
        var bad = await Ok(HttpMethod.Post, "/api/v1/notify/test", new { channelId = id });
        bad.GetProperty("ok").GetBoolean().Should().BeFalse();
        bad.GetProperty("error").GetString().Should().Be("The webhook answered 404.");
        (await _api.Send(HttpMethod.Post, "/api/v1/notify/test", new { channelId = 9999 })).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mail_goes_the_same_way_as_a_webhook()
    {
        await Ok(HttpMethod.Post, "/api/v1/notify/channels", new { kind = "email", name = "Mail", config = new { host = "smtp.example.test", port = 587, useSsl = true, from = "tideline@example.test", to = "alex@example.test", username = "u", password = "hunter2" } }, HttpStatusCode.Created);
        await OwesMoney();

        await Tick();

        _api.Email.Sent.Should().Contain(m => m.Kind == "new_debt");
        _api.Email.Configs.Should().OnlyContain(c => c.Contains("smtp.example.test"));
        (await Ok(HttpMethod.Get, "/api/v1/notify/channels"))[0].ToString().Should().NotContain("hunter2");
        (await _api.Get("/api/v1/audit")).ToString().Should().NotContain("hunter2");
    }

    // ---- the monthly summary ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_summary_for_a_month_has_the_numbers_of_that_month()
    {
        var spread = (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread");
        string D(string key) => new Tideline.Core.Money(spread.GetProperty(key).Cents()).ToString();

        var s = await Ok(HttpMethod.Get, "/api/v1/notify/summary?month=2026-03");

        s.GetProperty("title").GetString().Should().StartWith("March 2026: ");
        var body = s.GetProperty("body").GetString()!;
        body.Should().Contain($"In {D("in")} · Bills {D("bills")} · Required {D("required")} · Extra {D("extra")} · One-time {D("oneTime")} · Savings {D("savings")}");
        body.Should().MatchRegex(@"\d+ of \d+ bills paid|no bills expected");
        body.Should().NotContain("—").And.NotContain("–");
        s.GetProperty("link").GetString().Should().Be("/?from=2026-03-01&to=2026-03-31");
    }

    [Fact]
    public async Task With_nothing_open_the_summary_says_all_clear_and_with_things_open_it_lists_them_first()
    {
        var withOpen = await Ok(HttpMethod.Get, "/api/v1/notify/summary?month=2026-03");
        // clear everything pressing: acknowledge every alert and warning
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        foreach (var f in (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().Where(f => f.GetProperty("severity").GetString() is "alert" or "warn"))
            await Ok(HttpMethod.Post, $"/api/v1/flags/{f.GetProperty("id").GetInt64()}/acknowledge", new { });

        var clear = await Ok(HttpMethod.Get, "/api/v1/notify/summary?month=2026-03");
        clear.GetProperty("title").GetString().Should().Be("March 2026: all clear.");
        clear.GetProperty("needsYou").GetInt32().Should().Be(0);

        await OwesMoney();
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        var busy = await Ok(HttpMethod.Get, "/api/v1/notify/summary?month=2026-03");
        busy.GetProperty("title").GetString().Should().Be("March 2026: 1 thing needs you");
        busy.GetProperty("body").GetString()!.Split('\n')[0].Should().Be("- A payment that looks like a new loan");
        _ = withOpen;
    }

    [Fact]
    public async Task The_summary_is_sent_once_from_the_set_day_and_time_for_the_month_that_just_ended()
    {
        await Webhook();
        await Tick(); // 14:00 on 1 April, past the 08:00 default
        _api.Webhook.Sent.Where(m => m.Kind == "monthly_summary").Should().HaveCount(1);
        var summary = _api.Webhook.Sent.Single(m => m.Kind == "monthly_summary");
        summary.Title.Should().StartWith("March 2026: ");
        summary.Link.Should().Be("http://localhost:5173/?from=2026-03-01&to=2026-03-31");

        await Tick();
        _api.Webhook.Sent.Count(m => m.Kind == "monthly_summary").Should().Be(1, "once a month");
        _api.Scalar<long>("SELECT COUNT(*) FROM settings WHERE key = 'summary_sent:2026-03'").Should().Be(1);
    }

    [Fact]
    public async Task The_summary_can_be_sent_by_hand_and_that_counts_for_the_month()
    {
        await Webhook();

        var sent = await Ok(HttpMethod.Post, "/api/v1/notify/summary/send", new { month = "2026-03" });

        sent.GetProperty("delivered").GetInt32().Should().Be(1);
        _api.Webhook.Sent.Single().Kind.Should().Be("monthly_summary");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'summary.sent'").Should().Be(1);
        await Tick();
        _api.Webhook.Sent.Count(m => m.Kind == "monthly_summary").Should().Be(1, "the scheduler does not send it again");
    }

    // ---- settings the evaluators use ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_debt_words_and_the_card_interest_category_are_settings_that_change_what_is_flagged()
    {
        await OwesMoney();
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().Should().Contain(f => f.GetProperty("kind").GetString() == "new_debt");

        await Ok(HttpMethod.Put, "/api/v1/settings", new { debtWords = "ZZZ LENDING CO" });
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().Should().NotContain(f => f.GetProperty("kind").GetString() == "new_debt", "AFFIRM is no longer a debt word");
        (await Ok(HttpMethod.Get, "/api/v1/settings")).GetProperty("debtWords").GetString().Should().Be("ZZZ LENDING CO");

        var interest = Id("categories", "Utilities");
        await Ok(HttpMethod.Put, "/api/v1/settings", new { cardInterestCategoryId = interest });
        await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = Id("accounts", "Card"), postedOn = "2026-03-25", amountCents = -2_350, description = "PURCHASE INTEREST CHARGE", type = "required", categoryId = interest }, HttpStatusCode.Created);
        await Ok(HttpMethod.Post, "/api/v1/notify/evaluate");
        var flag = (await Ok(HttpMethod.Get, "/api/v1/flags")).EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "card_interest");
        flag.GetProperty("severity").GetString().Should().Be("alert");
        flag.GetProperty("detail").GetString().Should().Contain("$23.50");

        var (status, json) = await _api.Send(HttpMethod.Put, "/api/v1/settings", new { cardInterestCategoryId = 99999 });
        status.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("detail").GetString().Should().Contain("does not exist");
    }

    // ---- who may -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_an_owner_can_see_or_change_any_of_it()
    {
        var (_, secret) = _api.AddUser("member@example.com", "Member", Role.Member);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, secret);

        foreach (var url in new[] { "/api/v1/notify/channels", "/api/v1/notify/prefs", "/api/v1/notify/status", "/api/v1/notify/summary" })
            (await member.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);
        (await member.PostAsJsonAsync("/api/v1/notify/channels", new { kind = "webhook", name = "x", config = new { url = "https://x.test" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PostAsJsonAsync("/api/v1/notify/test", new { channelId = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_status_page_lists_the_schedule_and_what_was_sent()
    {
        await Webhook();
        await OwesMoney();
        await Tick();

        var status = await Ok(HttpMethod.Get, "/api/v1/notify/status");

        status.GetProperty("quietStart").GetString().Should().Be("21:00");
        status.GetProperty("summaryDay").GetInt32().Should().Be(1);
        status.GetProperty("lastEvaluationDay").GetString().Should().Be("2026-04-01");
        status.GetProperty("recent").EnumerateArray().Should().Contain(r => r.GetProperty("title").GetString() == "A payment that looks like a new loan" && r.GetProperty("status").GetString() == "sent");
    }
}
