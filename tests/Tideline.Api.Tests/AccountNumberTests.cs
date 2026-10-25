using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Tideline.Data.Security;

namespace Tideline.Api.Tests;

public sealed class AccountNumberTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Id(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private async Task<JsonElement> Ok(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var (status, json) = await _api.Send(method, url, body);
        status.Should().Be(expected, $"{method} {url}: {json}");
        return json;
    }

    [Fact]
    public void The_database_holds_no_digits_only_a_keyed_hash_and_a_sealed_copy()
    {
        using var conn = _api.Open();

        var rows = conn.Query<(string Name, string? Plain, string? Hash, string? Sealed)>("SELECT name, bank_number, bank_number_hash, bank_number_enc FROM accounts WHERE name IN ('Checking', 'Savings', 'Card')").ToList();

        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(r => r.Plain == null, "the old column is never written");
        rows.Should().OnlyContain(r => r.Hash != null && r.Hash.Length == 64 && r.Sealed != null);
        rows.Should().OnlyContain(r => r.Sealed != "1111" && !r.Hash!.Contains("1111"));
        rows.Select(r => r.Hash).Should().OnlyHaveUniqueItems();
        AccountNumbers.Keys.Open(rows.Single(r => r.Name == "Checking").Sealed).Should().Be("1111");
        rows.Single(r => r.Name == "Checking").Hash.Should().Be(AccountNumbers.Keys.Hash("1111"));
    }

    [Fact]
    public async Task The_app_shows_only_the_last_four_as_a_mask_and_never_the_digits_or_the_hash()
    {
        var list = await _api.Get("/api/v1/accounts");

        var checking = list.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Checking");
        checking.GetProperty("bankNumberMask").GetString().Should().Be("••••1111");
        checking.TryGetProperty("bankNumber", out _).Should().BeFalse();
        list.ToString().Should().NotContain(AccountNumbers.Keys.Hash("1111"));
    }

    [Fact]
    public async Task A_full_number_typed_in_is_cut_to_its_last_four_and_the_rest_is_not_kept_anywhere()
    {
        var id = (await Ok(HttpMethod.Post, "/api/v1/accounts", new { name = "Long number", kind = "checking", institution = "Truist", bankNumber = "9876 5432 1098 7654" }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == id).GetProperty("bankNumberMask").GetString().Should().Be("••••7654");
        using var conn = _api.Open();
        var everything = string.Join(" ", conn.Query<string>("SELECT COALESCE(bank_number,'') || COALESCE(bank_number_hash,'') || COALESCE(bank_number_enc,'') FROM accounts"))
            + string.Join(" ", conn.Query<string>("SELECT COALESCE(before_json,'') || COALESCE(after_json,'') FROM audit_log"));
        everything.Should().NotContain("9876").And.NotContain("5432").And.NotContain("1098");
        everything.Should().NotContain("7654", "the audit log says only whether it changed");
    }

    [Fact]
    public async Task Changing_the_number_is_audited_without_digits_and_leaving_it_out_keeps_it_and_empty_clears_it()
    {
        var checking = Id("Checking");

        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { notes = "x" });
        (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == checking).GetProperty("bankNumberMask").GetString().Should().Be("••••1111");

        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { bankNumber = "4242" });
        var entry = (await _api.Get("/api/v1/audit?action=account.updated")).GetProperty("items")[0];
        var before = entry.GetProperty("beforeJson").GetString()!;
        var after = entry.GetProperty("afterJson").GetString()!;
        before.Should().Contain("accountNumberKey").And.NotContain("1111");
        after.Should().Contain("accountNumberKey").And.NotContain("4242");

        await Ok(HttpMethod.Patch, $"/api/v1/accounts/{checking}", new { bankNumber = "" });
        (await _api.Get("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetInt64() == checking).GetProperty("bankNumberMask").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_file_is_matched_to_its_account_by_the_digits_in_its_name_and_one_nobody_has_is_refused_in_words()
    {
        var result = await _api.Pipeline.ImportTruistAsync("acct_7777_03_01_2026_to_03_31_2026.csv", "Posted Date,Transaction Date,Transaction Type,Check/Serial #,Description,Amount\n03/01/2026,03/01/2026,DEBIT,,TEST,(1.00)\n");

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("No account has the bank number 7777");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_rule_names_an_account_by_its_last_four_and_it_is_kept_as_a_hash_and_shown_masked()
    {
        var rule = (await Ok(HttpMethod.Post, "/api/v1/rules", new
        {
            name = "Only on checking", setType = "extra",
            conditions = new[] { new { field = "account", op = "is", value = JsonSerializer.SerializeToElement("1111") } },
        }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

        _api.Scalar<string>("SELECT value FROM rule_conditions WHERE rule_id = @rule", new { rule }).Should().Contain(AccountNumbers.Keys.Hash("1111")).And.NotContain("\"1111\"");
        var shown = (await _api.Get("/api/v1/rules")).EnumerateArray().Single(r => r.GetProperty("id").GetInt64() == rule).GetProperty("conditions")[0];
        shown.GetProperty("value").GetString().Should().Be("••••1111");
        (await _api.Get("/api/v1/audit?action=rule.created")).ToString().Should().NotContain("1111");

        // the rule does what it says: only lines on that account would change
        var test = await Ok(HttpMethod.Post, "/api/v1/rules/test", new
        {
            rule = new { name = "t", priority = 1, setType = "income", conditions = new[] { new { field = "account", op = "is", value = JsonSerializer.SerializeToElement("••••1111") } } },
            from = "2026-03-01", to = "2026-03-31",
        });
        test.GetProperty("changes").EnumerateArray().Should().NotBeEmpty().And.OnlyContain(c => c.GetProperty("accountName").GetString() == "Checking");
    }

    [Fact]
    public async Task A_rule_with_something_that_is_not_an_account_number_is_refused()
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/rules", new
        {
            name = "Bad", setType = "extra", conditions = new[] { new { field = "account", op = "is", value = JsonSerializer.SerializeToElement("ab") } },
        });

        status.Should().Be(HttpStatusCode.BadRequest);
        json.GetProperty("detail").GetString().Should().Contain("last 4 digits");
    }

    [Fact]
    public void A_database_from_before_this_change_is_brought_in_line_and_the_digits_are_removed()
    {
        using var conn = _api.Open();
        conn.Execute("UPDATE accounts SET bank_number = '000012345678', bank_number_hash = NULL, bank_number_enc = NULL WHERE name = 'Savings'");
        conn.Execute("INSERT INTO rules (name, priority, is_active, set_type, created_at, updated_at) VALUES ('Old', 999, 1, 'extra', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')");
        var ruleId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()");
        conn.Execute("INSERT INTO rule_conditions (rule_id, field, op, value) VALUES (@ruleId, 'account', 'is', '\"1111\"')", new { ruleId });

        var changed = AccountNumbers.SecureExisting(conn, _api.Clock);

        changed.Should().Be(1);
        conn.ExecuteScalar<string?>("SELECT bank_number FROM accounts WHERE name = 'Savings'").Should().BeNull();
        conn.ExecuteScalar<string>("SELECT bank_number_hash FROM accounts WHERE name = 'Savings'").Should().Be(AccountNumbers.Keys.Hash("5678"), "only the last 4 survive");
        conn.ExecuteScalar<string>("SELECT value FROM rule_conditions WHERE rule_id = @ruleId", new { ruleId }).Should().Contain(AccountNumbers.Keys.Hash("1111"));
        AccountNumbers.SecureExisting(conn, _api.Clock).Should().Be(0, "running it again changes nothing");
    }

    [Fact]
    public void A_different_key_stops_the_app_instead_of_quietly_matching_nothing()
    {
        using var conn = _api.Open();
        AccountNumbers.Verify(conn, _api.Clock); // the same key as when the database was written: fine

        var other = new AccountNumberKeys(Enumerable.Range(100, 64).Select(i => (byte)i).ToArray());
        var wrong = () => AccountNumbers.Verify(conn, _api.Clock, other);

        wrong.Should().Throw<AccountKeyMismatchException>().WithMessage("*Restore the keys folder*");
    }

    [Fact]
    public void Only_the_last_four_digits_survive_any_input_and_fewer_than_four_is_nothing()
    {
        AccountNumbers.LastFour("1234 5678-9012").Should().Be("9012");
        AccountNumbers.LastFour("acct_4321_").Should().Be("4321");
        AccountNumbers.LastFour("123").Should().BeNull();
        AccountNumbers.LastFour("").Should().BeNull();
        AccountNumbers.LastFour(null).Should().BeNull();
        AccountNumbers.Mask("0042").Should().Be("••••0042");
        AccountNumbers.RuleValue("••••1111").Should().Be(AccountNumbers.Keys.Hash("1111"));
        AccountNumbers.RuleValue(AccountNumbers.Keys.Hash("1111")).Should().Be(AccountNumbers.Keys.Hash("1111"));
        AccountNumbers.RuleValue("nope").Should().BeNull();
    }

    [Fact]
    public void A_key_is_created_once_and_read_back_the_same_and_a_sealed_copy_is_different_each_time()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tideline-key-{Guid.NewGuid():N}", "account-numbers.key");
        try
        {
            var first = AccountNumberKeys.LoadOrCreate(path);
            File.ReadAllBytes(path).Should().HaveCount(64);
            var again = AccountNumberKeys.LoadOrCreate(path);

            again.Hash("1111").Should().Be(first.Hash("1111"));
            first.Seal("1111").Should().NotBe(first.Seal("1111"), "a fresh nonce each time");
            again.Open(first.Seal("1111")).Should().Be("1111");
            new AccountNumberKeys(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()).Open(first.Seal("1111")).Should().BeNull("another key cannot open it");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
