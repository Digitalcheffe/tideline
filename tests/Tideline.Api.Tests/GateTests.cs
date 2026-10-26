using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Dapper;
using Xunit.Abstractions;

namespace Tideline.Api.Tests;

/// <summary>The Phase 3 gate, as far as it can be automated (issue: GATE Phase 3).</summary>
public sealed partial class GateTests(ITestOutputHelper output) : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    public sealed record Route(string Method, string Pattern)
    {
        public string Url => "/" + ParamRegex().Replace(Pattern, "1");
        public override string ToString() => $"{Method} /{Pattern}";
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex ParamRegex();

    /// <summary>Every API route the app really serves, read from the running host, so a new endpoint cannot hide from these tests.</summary>
    private IReadOnlyList<Route> Routes() =>
        _api.Service<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api/v1", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => new Route(m, e.RoutePattern.RawText!.TrimStart('/'))))
            .Distinct().OrderBy(r => r.Pattern).ThenBy(r => r.Method).ToList();

    private static string Relative(Route r) => r.Pattern["api/v1".Length..];

    private static bool IsPublic(Route r) =>
        (r.Method == "POST" && Relative(r) == "/auth/login") || (Relative(r) == "/auth/setup" && r.Method is "GET" or "POST")
        || (Relative(r) == "/auth/first-run" && r.Method is "GET" or "POST"); // an empty app: the first person makes the owner account

    private static bool IsBeforeSecondStep(Route r)
    {
        var p = Relative(r);
        return p.StartsWith("/auth/mfa", StringComparison.Ordinal) || p.StartsWith("/auth/enroll", StringComparison.Ordinal) || p is "/auth/logout" or "/auth/me" or "/auth/csrf";
    }

    private static HttpRequestMessage Request(Route r, string? cookie = null, string? csrf = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(r.Method), r.Url);
        if (cookie is not null) request.Headers.Add("Cookie", $"tideline={cookie}");
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        if (r.Method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
        return request;
    }

    [Fact]
    public void The_endpoint_list_is_read_from_the_host_and_is_not_empty()
    {
        var routes = Routes();
        foreach (var r in routes) output.WriteLine(r.ToString());

        routes.Count.Should().BeGreaterThan(60);
        routes.Should().Contain(r => r.Method == "POST" && r.Pattern.EndsWith("/imports/upload"));
        routes.Should().Contain(r => r.Method == "DELETE" && r.Pattern.Contains("/tokens/"));
    }

    [Fact]
    public async Task No_endpoint_answers_a_stranger_except_signing_in()
    {
        var raw = _api.RawClient();
        var open = new List<string>();

        foreach (var route in Routes().Where(r => !IsPublic(r)))
        {
            var response = await raw.SendAsync(Request(route));
            if (response.StatusCode != HttpStatusCode.Unauthorized) open.Add($"{route} -> {(int)response.StatusCode}");
        }

        open.Should().BeEmpty("every route but sign-in and the setup link needs a person who is signed in (or is the first-run page of an empty app)");
    }

    [Fact]
    public async Task No_endpoint_answers_after_the_password_alone_except_the_second_step_itself()
    {
        var raw = _api.RawClient();
        var login = await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = ApiFixture.OwnerEmail, password = ApiFixture.Password });
        var pre = login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => c.StartsWith("tideline=", StringComparison.Ordinal))["tideline=".Length..];
        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        meRequest.Headers.Add("Cookie", $"tideline={pre}");
        var csrf = JsonDocument.Parse(await (await raw.SendAsync(meRequest)).Content.ReadAsStringAsync()).RootElement.GetProperty("csrf").GetString()!;

        var answered = new List<string>();
        foreach (var route in Routes().Where(r => !IsPublic(r) && !IsBeforeSecondStep(r)))
        {
            var response = await raw.SendAsync(Request(route, pre, csrf));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Forbidden || !body.Contains("mfa_required")) answered.Add($"{route} -> {(int)response.StatusCode}");
        }

        answered.Should().BeEmpty("a password alone opens nothing: the second step is not optional");
    }

    [Fact]
    public async Task A_member_is_refused_every_owner_only_route_and_a_viewer_every_change()
    {
        var (_, memberSecret) = _api.AddUser("member@example.com", "Member", Tideline.Core.Security.Role.Member);
        var (_, viewerSecret) = _api.AddUser("viewer@example.com", "Viewer", Tideline.Core.Security.Role.Viewer);
        var member = await _api.SignInAsync("member@example.com", ApiFixture.Password, memberSecret);
        var viewer = await _api.SignInAsync("viewer@example.com", ApiFixture.Password, viewerSecret);

        var wrong = new List<string>();
        foreach (var route in Routes().Where(r => !IsPublic(r) && !Relative(r).StartsWith("/auth", StringComparison.Ordinal)))
        {
            var ownerOnly = Auth.AccessPolicy.IsOwnerOnly(route.Method, Relative(route));
            var isChange = route.Method is not ("GET" or "HEAD" or "OPTIONS");

            var asMember = await member.SendAsync(Request(route));
            if (ownerOnly && asMember.StatusCode != HttpStatusCode.Forbidden) wrong.Add($"member {route} -> {(int)asMember.StatusCode}");

            var asViewer = await viewer.SendAsync(Request(route));
            if ((ownerOnly || isChange) && asViewer.StatusCode != HttpStatusCode.Forbidden) wrong.Add($"viewer {route} -> {(int)asViewer.StatusCode}");
        }

        wrong.Should().BeEmpty();
    }

    // ---- the bank's numbers never change -----------------------------------------------------------------------

    private string BankFields() => _api.Scalar<string>("""
        SELECT group_concat(account_id || '|' || posted_on || '|' || amount_cents || '|' || original_description || '|' || COALESCE(bank_balance_cents, '') || '|' || COALESCE(check_number, '') || '|' || external_id || '|' || source, ';')
        FROM (SELECT * FROM transactions ORDER BY id)
        """);

    [Fact]
    public async Task No_endpoint_can_change_a_bank_field_even_when_sent_one()
    {
        var before = BankFields();
        var importedBefore = _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE source <> 'manual'");
        var ids = _api.Open().Query<long>("SELECT id FROM transactions ORDER BY id").ToList();
        var groceries = Id("categories", "Groceries");
        var checking = Id("accounts", "Checking");
        var savings = Id("accounts", "Savings");

        foreach (var id in ids)
        {
            var detail = await _api.Send(HttpMethod.Get, $"/api/v1/transactions/{id}");
            var version = detail.Body.GetProperty("version").GetString();
            var amount = detail.Body.GetProperty("bank").GetProperty("amountCents").GetInt64();
            var hostile = new
            {
                version, amountCents = 1, postedOn = "2020-01-01", description = "HACKED", originalDescription = "HACKED", accountId = savings, externalId = "x",
                bankBalanceCents = 1, checkNumber = "9", source = "manual", notes = "n", categoryId = groceries, type = "extra",
            };
            await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{id}", hostile);
            await _api.Send(HttpMethod.Put, $"/api/v1/transactions/{id}/splits", new { version = (await _api.Send(HttpMethod.Get, $"/api/v1/transactions/{id}")).Body.GetProperty("version").GetString(), splits = new[] { new { amountCents = amount, categoryId = groceries, type = "extra", postedOn = "2020-01-01", accountId = savings } } });
            await _api.Send(HttpMethod.Post, $"/api/v1/transactions/{id}/reset-to-rules");
            (await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{id}")).Status.Should().NotBe(HttpStatusCode.OK, "only a line you added by hand can be deleted, and these came from the bank");
        }
        await _api.Send(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids, set = new { categoryId = groceries, type = "extra", amountCents = 1, postedOn = "2020-01-01" } });

        // rules, merges and account changes reach many lines at once
        var rule = (await _api.Send(HttpMethod.Post, "/api/v1/rules", new { name = "everything", setType = "extra", conditions = Array.Empty<object>() })).Body.GetProperty("id").GetInt64();
        await _api.Send(HttpMethod.Post, $"/api/v1/rules/{rule}/apply", new { from = "2000-01-01", to = "2100-01-01" });
        await _api.Send(HttpMethod.Post, $"/api/v1/categories/{groceries}/merge", new { intoId = Id("categories", "Utilities") });
        await _api.Send(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { name = "Renamed", openingBalanceCents = 1, openingOn = "2026-01-01", bankNumber = "0000" });

        BankFields().Should().Be(before, "amount, date, description, bank balance, check number, id and source are the bank's, whatever is sent");
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE source <> 'manual'").Should().Be(importedBefore);
    }

    // ---- every change leaves an audit row ----------------------------------------------------------------------

    private enum Kind { Event, Create, Update, Delete }

    private sealed record Case(string Key, Kind Kind, string Action, Func<Task> Act);

    /// <summary>The routes that change nothing, so they have nothing to audit.</summary>
    /// <summary>Only works on an app with no users, which this test's app is not. FirstRunTests does it and checks its audit rows (user.created, user.password_set).</summary>
    private static readonly string[] CoveredElsewhere = ["POST /auth/first-run"];

    private static readonly string[] ReadOnlyPosts = ["POST /rules/test", "POST /budgets/{year:int}/fill-from-averages", "POST /notify/test", "POST /imports/simplefin/dry-run"];

    private long Id(string table, string name) => _api.Scalar<long>($"SELECT id FROM {table} WHERE name = @name", new { name });

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    [Fact]
    public async Task Every_route_that_changes_something_writes_an_audit_row_with_what_it_changed()
    {
        var checking = Id("accounts", "Checking");
        var savings = Id("accounts", "Savings");
        var groceries = Id("categories", "Groceries");
        var utilities = Id("categories", "Utilities");
        var mystery = _api.IdOf("MYSTERY VENDOR 9");
        var coffee = _api.IdOf("COFFEE SHOP");
        var ruleBody = (string name, string text) => new
        {
            name, setType = "extra", conditions = new[] { new { field = "description", op = "contains_any", value = JsonSerializer.SerializeToElement(new[] { text }) } },
        };

        long channel = 0, snapAccount = 0, snapshot = 0, person = 0, plan = 0, account = 0, category = 0, tag = 0, pattern = 0, recurring = 0, rule = 0, manual = 0, token = 0, invited = 0, move = 0, suggestion = 0;

        var m1 = _api.AddUser("m1@example.com", "M One", Tideline.Core.Security.Role.Member);
        var m2 = _api.AddUser("m2@example.com", "M Two", Tideline.Core.Security.Role.Member);
        var m1Client = await _api.SignInAsync("m1@example.com", ApiFixture.Password, m1.Secret);
        await _api.SignInAsync("m2@example.com", ApiFixture.Password, m2.Secret);
        var m3 = _api.AddUser("m3@example.com", "M Three", Tideline.Core.Security.Role.Member);
        var m3Client = await _api.SignInAsync("m3@example.com", ApiFixture.Password, m3.Secret);
        var newcomer = _api.AddUser("new@example.com", "New", Tideline.Core.Security.Role.Member, enrolled: false, password: null);
        var raw = _api.RawClient();
        string pre = "", csrf = "", newSecret = "", acctNewSecret = "";

        // someone managing their own account, signed in fully
        var acct = _api.AddUser("acct@example.com", "Acct One", Tideline.Core.Security.Role.Member);
        var acctClient = await _api.SignInAsync("acct@example.com", ApiFixture.Password, acct.Secret);
        async Task<JsonElement> Mine(HttpMethod method, string url, object body)
        {
            var response = await acctClient.SendAsync(new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) });
            var text = await response.Content.ReadAsStringAsync();
            response.IsSuccessStatusCode.Should().BeTrue($"{method} {url}: {text}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        HttpRequestMessage Cookie(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Cookie", $"tideline={pre}");
            if (csrf.Length > 0) request.Headers.Add("X-CSRF-Token", csrf);
            if (body is not null) request.Content = JsonContent.Create(body);
            return request;
        }
        static string CookieOf(HttpResponseMessage r) => r.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).First(c => c.StartsWith("tideline=", StringComparison.Ordinal))["tideline=".Length..];

        var cases = new List<Case>
        {
            // setting the household up
            new("POST /accounts", Kind.Create, "account.created", async () => account = (await Ok(HttpMethod.Post, "/api/v1/accounts", new { name = "Gate account", kind = "checking" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /accounts/{id:long}", Kind.Update, "account.updated", () => Ok(HttpMethod.Patch, $"/api/v1/accounts/{account}", new { notes = "n" })),
            new("POST /categories", Kind.Create, "category.created", async () => category = (await Ok(HttpMethod.Post, "/api/v1/categories", new { name = "Gate category", defaultType = "extra" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /categories/{id:long}", Kind.Update, "category.updated", () => Ok(HttpMethod.Patch, $"/api/v1/categories/{category}", new { name = "Gate category 2" })),
            new("POST /categories/{id:long}/merge", Kind.Update, "category.merged", () => Ok(HttpMethod.Post, $"/api/v1/categories/{category}/merge", new { intoId = groceries })),
            new("POST /tags", Kind.Create, "tag.created", async () => tag = (await Ok(HttpMethod.Post, "/api/v1/tags", new { name = "gate-tag" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /tags/{id:long}", Kind.Update, "tag.updated", () => Ok(HttpMethod.Patch, $"/api/v1/tags/{tag}", new { name = "gate-tag-2" })),
            new("DELETE /tags/{id:long}", Kind.Delete, "tag.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/tags/{tag}")),
            new("POST /move-patterns", Kind.Create, "move_pattern.created", async () => pattern = (await Ok(HttpMethod.Post, "/api/v1/move-patterns", new { fromAccountId = checking, fromContains = "A", toAccountId = savings, toContains = "B" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /move-patterns/{id:long}", Kind.Update, "move_pattern.updated", () => Ok(HttpMethod.Patch, $"/api/v1/move-patterns/{pattern}", new { fromAccountId = checking, fromContains = "A2", toAccountId = savings, toContains = "B2" })),
            new("DELETE /move-patterns/{id:long}", Kind.Delete, "move_pattern.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/move-patterns/{pattern}")),
            new("POST /recurring", Kind.Create, "recurring.created", async () => recurring = (await Ok(HttpMethod.Post, "/api/v1/recurring", new { name = "Gate bill", categoryId = utilities, expectedMinCents = 1, expectedMaxCents = 2, dueDay = 5, firstDueOn = "2026-01-05" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /recurring/{id:long}", Kind.Update, "recurring.updated", () => Ok(HttpMethod.Patch, $"/api/v1/recurring/{recurring}", new { expectedMaxCents = 3 })),
            new("PUT /settings", Kind.Update, "settings.updated", () => Ok(HttpMethod.Put, "/api/v1/settings", new { oneTimeAttentionCents = 12345 })),
            new("PUT /settings/app", Kind.Update, "settings.app_updated", () => Ok(HttpMethod.Put, "/api/v1/settings/app", new { values = new Dictionary<string, string?> { ["idle_hours"] = "10" } })),

            // rules
            new("POST /rules", Kind.Create, "rule.created", async () => rule = (await Ok(HttpMethod.Post, "/api/v1/rules", ruleBody("Gate rule", "MYSTERY VENDOR"), HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /rules/{id:long}", Kind.Update, "rule.updated", () => Ok(HttpMethod.Patch, $"/api/v1/rules/{rule}", ruleBody("Gate rule 2", "MYSTERY VENDOR"))),
            new("POST /rules/reorder", Kind.Update, "rule.reordered", async () =>
                await Ok(HttpMethod.Post, "/api/v1/rules/reorder", new { ids = (await _api.Get("/api/v1/rules")).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Reverse().ToArray() })),
            new("POST /rules/{id:long}/apply", Kind.Update, "rule.apply", () => Ok(HttpMethod.Post, $"/api/v1/rules/{rule}/apply", new { from = "2026-03-01", to = "2026-03-31" })),
            new("DELETE /rules/{id:long}", Kind.Delete, "rule.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/rules/{rule}")),

            // suggestions on flags
            new("POST /recommendation-rules", Kind.Create, "recommendation_rule.created", async () => suggestion = (await Ok(HttpMethod.Post, "/api/v1/recommendation-rules", new { name = "Gate suggestion", flagKind = "review_needed", suggestion = "Look at them." }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /recommendation-rules/{id:long}", Kind.Update, "recommendation_rule.updated", () => Ok(HttpMethod.Patch, $"/api/v1/recommendation-rules/{suggestion}", new { suggestion = "Look at them soon." })),
            new("POST /recommendations/dismiss", Kind.Update, "recommendation.dismissed", async () =>
            {
                _api.Execute("INSERT INTO flags (kind, subject_type, subject_id, period, severity, message, created_at) VALUES ('review_needed', 'category', 1, '2026-03', 'info', 'Lines to review', '2026-04-01T00:00:00Z')");
                await Ok(HttpMethod.Post, "/api/v1/recommendations/dismiss", new { ruleId = suggestion, flagId = _api.Scalar<long>("SELECT MAX(id) FROM flags") });
            }),
            new("DELETE /recommendation-rules/{id:long}", Kind.Delete, "recommendation_rule.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/recommendation-rules/{suggestion}")),

            // lines
            new("POST /transactions", Kind.Create, "transaction.create", async () => manual = (await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = checking, postedOn = "2026-03-20", amountCents = -1234, description = "GATE CASH", categoryId = groceries, type = "extra" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /transactions/{id:long}", Kind.Update, "transaction.update", async () => await Ok(HttpMethod.Patch, $"/api/v1/transactions/{manual}", new { version = await _api.VersionOf(manual), notes = "n" })),
            new("POST /transactions/{id:long}/reset-to-rules", Kind.Update, "transaction.reset", () => Ok(HttpMethod.Post, $"/api/v1/transactions/{manual}/reset-to-rules")),
            new("PUT /transactions/{id:long}/splits", Kind.Update, "transaction.splits", async () =>
                await Ok(HttpMethod.Put, $"/api/v1/transactions/{coffee}/splits", new { version = await _api.VersionOf(coffee), splits = new[] { new { amountCents = (await _api.Get($"/api/v1/transactions/{coffee}")).GetProperty("bank").GetProperty("amountCents").GetInt64(), categoryId = groceries, type = "extra" } } })),
            new("POST /transactions/bulk", Kind.Update, "transaction.update", () => Ok(HttpMethod.Post, "/api/v1/transactions/bulk", new { ids = new[] { mystery }, set = new { type = "one_time" } })),
            new("POST /transactions/leave-out", Kind.Update, "transaction.leave_out", () => Ok(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { mystery }, reason = "gate" })),
            new("POST /transactions/bring-back", Kind.Update, "transaction.bring_back", () => Ok(HttpMethod.Post, "/api/v1/transactions/bring-back", new { ids = new[] { mystery } })),
            new("POST /moves", Kind.Create, "move.pair", async () =>
            {
                var partner = (await Ok(HttpMethod.Post, "/api/v1/transactions", new { accountId = savings, postedOn = "2026-03-21", amountCents = 1234, description = "GATE PARTNER", categoryId = groceries, type = "extra" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
                move = (await Ok(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { manual, partner } }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
            }),
            new("DELETE /moves/{id:long}", Kind.Delete, "move.unpair", () => Ok(HttpMethod.Delete, $"/api/v1/moves/{move}")),
            new("DELETE /transactions/{id:long}", Kind.Delete, "transaction.delete", () => Ok(HttpMethod.Delete, $"/api/v1/transactions/{manual}")),

            // people, tokens, sessions
            new("POST /users", Kind.Create, "user.created", async () => invited = (await Ok(HttpMethod.Post, "/api/v1/users", new { email = "invited@example.com", displayName = "Invited", role = "viewer" }, HttpStatusCode.Created)).GetProperty("user").GetProperty("id").GetInt64()),
            new("PATCH /users/{id:long}", Kind.Update, "user.updated", () => Ok(HttpMethod.Patch, $"/api/v1/users/{invited}", new { displayName = "Invited 2" })),
            new("POST /users/{id:long}/setup-link", Kind.Create, "user.setup_link_created", () => Ok(HttpMethod.Post, $"/api/v1/users/{invited}/setup-link")),
            new("POST /users/{id:long}/reset-mfa", Kind.Update, "mfa.reset", () => Ok(HttpMethod.Post, $"/api/v1/users/{invited}/reset-mfa")),
            new("DELETE /sessions/{id:long}", Kind.Delete, "session.revoked", async () =>
                await Ok(HttpMethod.Delete, $"/api/v1/sessions/{(await _api.Get("/api/v1/sessions")).EnumerateArray().First(s => s.GetProperty("userEmail").GetString() == "m2@example.com").GetProperty("id").GetInt64()}")),
            new("POST /users/{id:long}/sessions/revoke-all", Kind.Event, "session.revoked_all", () => Ok(HttpMethod.Post, $"/api/v1/users/{m1.Id}/sessions/revoke-all")),
            new("POST /tokens", Kind.Create, "token.created", async () => token = (await Ok(HttpMethod.Post, "/api/v1/tokens", new { name = "Gate token", scope = "read" }, HttpStatusCode.Created)).GetProperty("record").GetProperty("id").GetInt64()),
            new("DELETE /tokens/{id:long}", Kind.Delete, "token.revoked", () => Ok(HttpMethod.Delete, $"/api/v1/tokens/{token}")),

            // net worth, retirement, plans and budget
            new("POST /accounts/{id:long}/snapshots", Kind.Create, "snapshot.saved", async () =>
            {
                snapAccount = (await Ok(HttpMethod.Post, "/api/v1/accounts", new { name = "Gate house", kind = "asset" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
                snapshot = (await Ok(HttpMethod.Post, $"/api/v1/accounts/{snapAccount}/snapshots", new { asOn = "2026-03-01", balanceCents = 100 }, HttpStatusCode.Created)).GetProperty("id").GetInt64();
            }),
            new("DELETE /accounts/{id:long}/snapshots/{snapshotId:long}", Kind.Delete, "snapshot.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/accounts/{snapAccount}/snapshots/{snapshot}")),
            new("POST /people", Kind.Create, "person.created", async () => person = (await Ok(HttpMethod.Post, "/api/v1/people", new { name = "Gate person", retireAge = 65 }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /people/{id:long}", Kind.Update, "person.updated", () => Ok(HttpMethod.Patch, $"/api/v1/people/{person}", new { retireAge = 67 })),
            new("PUT /retirement/goal", Kind.Create, "retirement_goal.saved", () => Ok(HttpMethod.Put, "/api/v1/retirement/goal", new { goalAnnualIncomeTodayCents = 8_000_000 })),
            new("POST /plans", Kind.Create, "plan.created", async () => plan = (await Ok(HttpMethod.Post, "/api/v1/plans", new { name = "Gate plan", targetCents = 1_000, targetOn = "2027-01-01" }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /plans/{id:long}", Kind.Update, "plan.updated", () => Ok(HttpMethod.Patch, $"/api/v1/plans/{plan}", new { targetCents = 2_000 })),
            new("PUT /budgets/{year:int}", Kind.Create, "budget.saved", () => Ok(HttpMethod.Put, "/api/v1/budgets/2026", new { expectedIncomeCents = 1 })),

            // flags
            new("POST /notify/evaluate", Kind.Event, "evaluator.run", () => Ok(HttpMethod.Post, "/api/v1/notify/evaluate")),
            new("POST /flags/{id:long}/acknowledge", Kind.Update, "flag.acknowledged", async () =>
                await Ok(HttpMethod.Post, $"/api/v1/flags/{(await Ok(HttpMethod.Get, "/api/v1/flags"))[0].GetProperty("id").GetInt64()}/acknowledge", new { note = "seen" })),

            // notifications
            new("POST /notify/channels", Kind.Create, "notify_channel.created", async () => channel = (await Ok(HttpMethod.Post, "/api/v1/notify/channels", new { kind = "webhook", name = "Gate", config = new { url = "https://hooks.example.test/x" } }, HttpStatusCode.Created)).GetProperty("id").GetInt64()),
            new("PATCH /notify/channels/{id:long}", Kind.Update, "notify_channel.updated", () => Ok(HttpMethod.Patch, $"/api/v1/notify/channels/{channel}", new { name = "Gate 2" })),
            new("PUT /notify/prefs", Kind.Update, "notify_prefs.updated", () => Ok(HttpMethod.Put, "/api/v1/notify/prefs", new { modes = new Dictionary<string, string> { ["new_debt"] = "off" } })),
            new("POST /notify/summary/send", Kind.Create, "summary.sent", () => Ok(HttpMethod.Post, "/api/v1/notify/summary/send", new { month = "2026-03" })),
            new("DELETE /notify/channels/{id:long}", Kind.Delete, "notify_channel.deleted", () => Ok(HttpMethod.Delete, $"/api/v1/notify/channels/{channel}")),

            // SimpleFIN
            new("POST /imports/simplefin/connect", Kind.Create, "simplefin.connected", () =>
                Ok(HttpMethod.Post, "/api/v1/imports/simplefin/connect", new { setupToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://bridge.example.test/simplefin/claim/gate")) }, HttpStatusCode.Created)),
            new("POST /imports/simplefin/sync", Kind.Create, "simplefin.sync", async () =>
            {
                await Ok(HttpMethod.Patch, $"/api/v1/accounts/{account}", new { simplefinAccountId = "ACT-9", simplefinFromOn = "2026-04-01" });
                await Ok(HttpMethod.Post, "/api/v1/imports/simplefin/sync", new { });
            }),
            new("DELETE /imports/simplefin", Kind.Update, "simplefin.disconnected", () => Ok(HttpMethod.Delete, "/api/v1/imports/simplefin")),

            // data in and out
            new("POST /backups", Kind.Create, "backup.created", () => Ok(HttpMethod.Post, "/api/v1/backups")),
            new("POST /export", Kind.Create, "export.created", async () => (await _api.Client.PostAsync("/api/v1/export", null)).EnsureSuccessStatusCode()),
            new("POST /imports/upload", Kind.Create, "import.upload", async () =>
            {
                var form = new MultipartFormDataContent { { new ByteArrayContent("x,y\n1,2\n"u8.ToArray()), "file", "gate.csv" } };
                (await _api.Client.PostAsync("/api/v1/imports/upload", form)).EnsureSuccessStatusCode();
            }),

            // signing in and out
            new("POST /auth/login", Kind.Create, "login.success", async () => (await raw.PostAsJsonAsync("/api/v1/auth/login", new { email = "m1@example.com", password = ApiFixture.Password })).EnsureSuccessStatusCode()),
            new("POST /auth/mfa/totp", Kind.Create, "mfa.success", async () => await _api.SignInAsync("m1@example.com", ApiFixture.Password, m1.Secret)),
            new("POST /auth/logout", Kind.Event, "logout", async () => (await m3Client.PostAsync("/api/v1/auth/logout", null)).EnsureSuccessStatusCode()),
            new("POST /auth/recovery-codes", Kind.Event, "mfa.recovery_codes_regenerated", async () => (await _api.Client.PostAsJsonAsync("/api/v1/auth/recovery-codes", new { code = _api.NextCode(_api.OwnerSecret) })).EnsureSuccessStatusCode()),
            new("DELETE /auth/sessions/{id:long}", Kind.Delete, "session.revoked", async () =>
            {
                await _api.SignInAsync(ApiFixture.OwnerEmail, ApiFixture.Password, _api.OwnerSecret);
                var other = (await _api.Get("/api/v1/auth/sessions")).EnumerateArray().First(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetInt64();
                await Ok(HttpMethod.Delete, $"/api/v1/auth/sessions/{other}");
            }),
            new("POST /auth/sessions/revoke-others", Kind.Create, "session.revoked_others", () => Ok(HttpMethod.Post, "/api/v1/auth/sessions/revoke-others")),
            new("POST /auth/setup", Kind.Event, "user.password_set", async () =>
            {
                string link;
                using (var conn = _api.Open()) link = new Tideline.Data.Security.SetupLinks(conn, _api.Clock).Create(newcomer.Id).Token;
                var response = await raw.PostAsJsonAsync("/api/v1/auth/setup", new { token = link, password = "Correct-horse-battery-9" });
                response.EnsureSuccessStatusCode();
                pre = CookieOf(response);
                csrf = JsonDocument.Parse(await (await raw.SendAsync(Cookie(HttpMethod.Get, "/api/v1/auth/me"))).Content.ReadAsStringAsync()).RootElement.GetProperty("csrf").GetString()!;
            }),
            new("POST /auth/enroll/totp/start", Kind.Event, "mfa.enroll_started", async () =>
            {
                var response = await raw.SendAsync(Cookie(HttpMethod.Post, "/api/v1/auth/enroll/totp/start"));
                response.EnsureSuccessStatusCode();
                newSecret = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
            }),
            new("POST /auth/enroll/totp/confirm", Kind.Event, "mfa.enrolled", async () =>
                (await raw.SendAsync(Cookie(HttpMethod.Post, "/api/v1/auth/enroll/totp/confirm", new { code = _api.NextCode(newSecret) }))).EnsureSuccessStatusCode()),

            // a person managing their own sign-in
            new("PATCH /auth/profile", Kind.Update, "user.updated", () => Mine(HttpMethod.Patch, "/api/v1/auth/profile", new { displayName = "Acct Two" })),
            new("POST /auth/password", Kind.Event, "user.password_changed", () => Mine(HttpMethod.Post, "/api/v1/auth/password", new { currentPassword = ApiFixture.Password, newPassword = "a different long passphrase" })),
            new("POST /auth/authenticator/start", Kind.Event, "mfa.replace_started", async () =>
                acctNewSecret = (await Mine(HttpMethod.Post, "/api/v1/auth/authenticator/start", new { code = _api.NextCode(acct.Secret) })).GetProperty("secret").GetString()!),
            new("POST /auth/authenticator/confirm", Kind.Event, "mfa.authenticator_replaced", () => Mine(HttpMethod.Post, "/api/v1/auth/authenticator/confirm", new { code = _api.NextCode(acctNewSecret) })),

            // clearing a failed import from the list
            new("POST /imports/{id:long}/dismiss", Kind.Update, "import.dismissed", async () =>
            {
                _api.Execute("INSERT INTO import_batches (source, file_name, started_at, status, error) VALUES ('csv', 'gate-failed.csv', '2026-04-01T00:00:00Z', 'failed', 'a test failure')");
                await Ok(HttpMethod.Post, $"/api/v1/imports/{_api.Scalar<long>("SELECT MAX(id) FROM import_batches")}/dismiss");
            }),

            // taking back an import that should not have happened
            new("POST /imports/{id:long}/undo", Kind.Event, "import.undone", async () =>
            {
                _api.Execute("INSERT INTO import_batches (source, file_name, started_at, status) VALUES ('csv', 'gate-undo.csv', '2026-04-01T00:00:00Z', 'done')");
                _api.Execute("UPDATE transactions SET import_batch_id = (SELECT MAX(id) FROM import_batches) WHERE id = @mystery", new { mystery });
                await Ok(HttpMethod.Post, $"/api/v1/imports/{_api.Scalar<long>("SELECT MAX(id) FROM import_batches")}/undo");
            }),
            new("POST /imports/{id:long}/restore", Kind.Event, "import.restored", () => Ok(HttpMethod.Post, $"/api/v1/imports/{_api.Scalar<long>("SELECT MAX(id) FROM import_batches")}/restore")),
        };

        var missing = new List<string>();
        foreach (var c in cases)
        {
            var since = _api.Scalar<long>("SELECT COALESCE(MAX(id), 0) FROM audit_log");
            await c.Act();
            using var conn = _api.Open();
            var rows = conn.Query<(string Action, string? Before, string? After)>(
                "SELECT action, before_json, after_json FROM audit_log WHERE id > @since AND action = @action", new { since, action = c.Action }).ToList();

            if (rows.Count == 0) { missing.Add($"{c.Key} wrote no '{c.Action}' row"); continue; }
            var ok = c.Kind switch
            {
                Kind.Create => rows.Any(r => r.After is not null),
                Kind.Update => rows.Any(r => r.Before is not null && r.After is not null),
                Kind.Delete => rows.Any(r => r.Before is not null),
                _ => true,
            };
            if (!ok) missing.Add($"{c.Key} wrote '{c.Action}' without the {c.Kind} detail (before/after)");
        }
        missing.Should().BeEmpty();

        // and no route was left out of the list above
        var changing = Routes().Where(r => r.Method is not ("GET" or "HEAD" or "OPTIONS")).Select(r => $"{r.Method} {Relative(r).TrimEnd('/')}").ToHashSet();
        changing.ExceptWith(ReadOnlyPosts);
        changing.ExceptWith(CoveredElsewhere);
        changing.Should().BeSubsetOf(cases.Select(c => c.Key), "a new route that changes data has to be added to this test, with its audit action");
    }
}
