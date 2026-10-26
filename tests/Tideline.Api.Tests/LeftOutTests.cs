using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Tideline.Api.Tests;

/// <summary>Leaving lines out of the totals, deleting a hand-added line, and undoing an import (docs/07-api.md, "Ledger").</summary>
public sealed class LeftOutTests : IDisposable
{
    private readonly ApiFixture _api = new();

    public void Dispose() => _api.Dispose();

    private long Account(string name) => _api.Scalar<long>("SELECT id FROM accounts WHERE name = @name", new { name });

    private long Category(string name) => _api.Scalar<long>("SELECT id FROM categories WHERE name = @name", new { name });

    private async Task<long> Line(string account, long cents, string description, string date = "2026-03-20")
    {
        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/transactions",
            new { accountId = Account(account), postedOn = date, amountCents = cents, description, categoryId = Category("Groceries"), type = "extra" });
        status.Should().Be(HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt64();
    }

    private async Task<long> Extra() => (await _api.Get("/api/v1/overview?from=2026-03-01&to=2026-03-31")).GetProperty("spread").GetProperty("extra").Cents();

    private async Task<JsonElement> LeftOutList() => await _api.Get("/api/v1/lines?from=2026-01-01&to=2026-12-31&leftOut=true");

    /// <summary>A line as the bank would give it: a csv row, which can never be deleted.</summary>
    private long BankLine(string account, long cents, string description, long? batch = null)
    {
        _api.Execute("""
            INSERT INTO transactions (account_id, posted_on, amount_cents, original_description, external_id, source, import_batch_id, category_id, type, created_at, updated_at)
            VALUES (@account, '2026-03-21', @cents, @description, @external, 'csv', @batch, @category, 'extra', '2026-03-21T00:00:00Z', '2026-03-21T00:00:00Z')
            """, new { account = Account(account), cents, description, external = "csv:" + Guid.NewGuid().ToString("N"), batch, category = Category("Groceries") });
        return _api.IdOf(description);
    }

    private long Batch()
    {
        _api.Execute("INSERT INTO import_batches (source, file_name, started_at, status) VALUES ('csv', 'wrong-account.csv', '2026-04-01T00:00:00Z', 'done')");
        return _api.Scalar<long>("SELECT MAX(id) FROM import_batches");
    }

    [Fact]
    public async Task A_bank_line_can_be_left_out_of_the_totals_and_brought_back_and_is_never_changed()
    {
        var before = await Extra();
        var line = BankLine("Checking", -7000, "DOUBLE UPLOAD");
        (await Extra()).Should().Be(before + 7000);

        var (status, json) = await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { line }, reason = "  uploaded twice " });

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("leftOut").GetInt32().Should().Be(1);
        (await Extra()).Should().Be(before, "a left-out line is in no total");
        var detail = await _api.Get($"/api/v1/transactions/{line}");
        detail.GetProperty("leftOut").GetProperty("reason").GetString().Should().Be("uploaded twice");
        detail.GetProperty("bank").GetProperty("amountCents").GetInt64().Should().Be(-7000, "the bank fields stay exactly as they were");
        (await _api.Get("/api/v1/lines?from=2026-03-01&to=2026-03-31")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("transactionId").GetInt64()).Should().NotContain(line, "the normal list does not show it");
        var listed = (await LeftOutList()).GetProperty("items").EnumerateArray().ToList();
        listed.Select(i => i.GetProperty("transactionId").GetInt64()).Should().Contain(line);
        listed.Should().OnlyContain(i => i.GetProperty("isLeftOut").GetBoolean());

        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bring-back", new { ids = new[] { line } })).Status.Should().Be(HttpStatusCode.OK);
        (await Extra()).Should().Be(before + 7000, "back in the totals");
        (await _api.Get($"/api/v1/transactions/{line}")).GetProperty("leftOut").ValueKind.Should().Be(JsonValueKind.Null);
        (await _api.Get("/api/v1/audit?action=transaction.leave_out")).GetProperty("items").GetArrayLength().Should().Be(1);
        (await _api.Get("/api/v1/audit?action=transaction.bring_back")).GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task A_left_out_line_leaves_the_account_flows_and_cannot_be_edited_until_it_is_brought_back()
    {
        var checking = Account("Checking");
        var flowBefore = _api.Scalar<long>("SELECT COALESCE(SUM(amount_cents), 0) FROM transactions WHERE account_id = @checking AND left_out_at IS NULL", new { checking });
        var line = BankLine("Checking", -1500, "SHOULD NOT COUNT");
        await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { line } });

        _api.Scalar<long>("SELECT COALESCE(SUM(amount_cents), 0) FROM transactions WHERE account_id = @checking AND left_out_at IS NULL", new { checking }).Should().Be(flowBefore);

        var edit = await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), notes = "x" });
        edit.Status.Should().Be((HttpStatusCode)422);
        edit.Body.GetProperty("detail").GetString().Should().Contain("Bring it back");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { line } })).Status.Should().Be(HttpStatusCode.Conflict, "it is already out");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/bring-back", new { ids = new[] { BankLine("Checking", -1, "NEVER LEFT OUT") } })).Status.Should().Be(HttpStatusCode.Conflict);

        await _api.Send(HttpMethod.Post, "/api/v1/transactions/bring-back", new { ids = new[] { line } });
        (await _api.Send(HttpMethod.Patch, $"/api/v1/transactions/{line}", new { version = await _api.VersionOf(line), notes = "x" })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_line_in_a_transfer_has_to_be_unpaired_first_and_many_at_once_are_counted()
    {
        var outLeg = await Line("Checking", -3000, "XFER OUT");
        var inLeg = await Line("Savings", 3000, "XFER IN");
        var move = (await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { outLeg, inLeg } })).Body.GetProperty("id").GetInt64();
        var plain = BankLine("Checking", -500, "PLAIN ONE");

        var one = await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { outLeg } });
        one.Status.Should().Be((HttpStatusCode)422);
        one.Body.GetProperty("detail").GetString().Should().Contain("Unpair");

        var many = await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { outLeg, plain, 999_999L } });
        many.Status.Should().Be(HttpStatusCode.OK);
        many.Body.GetProperty("leftOut").GetInt32().Should().Be(1);
        many.Body.GetProperty("skipped").GetInt32().Should().Be(1);
        many.Body.GetProperty("notFound").GetInt32().Should().Be(1);

        await _api.Send(HttpMethod.Delete, $"/api/v1/moves/{move}");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { outLeg } })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_line_added_by_hand_can_be_deleted_but_a_bank_line_and_a_transfer_leg_cannot()
    {
        var before = await Extra();
        var mine = await Line("Checking", -2000, "ADDED BY MISTAKE");
        (await Extra()).Should().Be(before + 2000);

        (await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{mine}")).Status.Should().Be(HttpStatusCode.OK);
        (await Extra()).Should().Be(before);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE id = @mine", new { mine }).Should().Be(0);
        (await _api.Get("/api/v1/audit?action=transaction.delete")).GetProperty("items").GetArrayLength().Should().Be(1);

        var bank = await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{BankLine("Checking", -100, "BANK ROW")}");
        bank.Status.Should().Be(HttpStatusCode.Conflict);
        bank.Body.GetProperty("detail").GetString().Should().Contain("Leave them out");

        var a = await Line("Checking", -900, "PAIRED A");
        var b = await Line("Savings", 900, "PAIRED B");
        await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { a, b } });
        (await _api.Send(HttpMethod.Delete, $"/api/v1/transactions/{a}")).Status.Should().Be((HttpStatusCode)422);
    }

    [Fact]
    public async Task Undoing_an_import_takes_every_line_out_in_one_step_unpairs_its_transfers_and_can_be_restored()
    {
        var before = await Extra();
        var batch = Batch();
        var one = BankLine("Checking", -1100, "WRONG FILE ONE", batch);
        var two = BankLine("Checking", -2200, "WRONG FILE TWO", batch);
        var separatelyOut = BankLine("Checking", -330, "ALREADY OUT", batch);
        await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { separatelyOut } });
        var other = await Line("Savings", 1100, "OTHER SIDE");   // not from the batch; pairs with the first line
        await _api.Send(HttpMethod.Post, "/api/v1/moves", new { transactionIds = new[] { one, other } });
        (await Extra()).Should().Be(before + 2200, "only the unpaired line of the file counts as spending");

        var (status, json) = await _api.Send(HttpMethod.Post, $"/api/v1/imports/{batch}/undo");

        status.Should().Be(HttpStatusCode.OK, json.ToString());
        json.GetProperty("leftOut").GetInt32().Should().Be(2, "the two that were still counted");
        json.GetProperty("unpaired").GetInt32().Should().Be(1);
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE import_batch_id = @batch AND left_out_at IS NULL", new { batch }).Should().Be(0);
        (await _api.Get($"/api/v1/transactions/{other}")).GetProperty("moveId").ValueKind.Should().Be(JsonValueKind.Null, "the partner on the other account is a plain line again");
        (await Extra()).Should().BeLessThanOrEqualTo(before, "the file's lines are out; the partner is a plain line of its own again, which cannot add spending");
        var imports = (await _api.Get("/api/v1/imports")).GetProperty("batches").EnumerateArray().Single(b => b.GetProperty("id").GetInt64() == batch);
        imports.GetProperty("countedLines").GetInt64().Should().Be(0);
        imports.GetProperty("undoneLines").GetInt64().Should().Be(2);
        (await _api.Send(HttpMethod.Post, $"/api/v1/imports/{batch}/undo")).Status.Should().Be(HttpStatusCode.Conflict, "nothing is left to take out");

        var restored = await _api.Send(HttpMethod.Post, $"/api/v1/imports/{batch}/restore");
        restored.Status.Should().Be(HttpStatusCode.OK, restored.Body.ToString());
        restored.Body.GetProperty("restored").GetInt32().Should().Be(2);
        _api.Scalar<string?>("SELECT left_out_at FROM transactions WHERE id = @separatelyOut", new { separatelyOut }).Should().NotBeNull("a line left out by hand stays out");
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE id IN (@one, @two) AND left_out_at IS NULL", new { one, two }).Should().Be(2);
        (await _api.Send(HttpMethod.Post, $"/api/v1/imports/{batch}/restore")).Status.Should().Be(HttpStatusCode.Conflict);
        (await _api.Send(HttpMethod.Post, "/api/v1/imports/999999/undo")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_re_import_does_not_bring_a_left_out_line_back()
    {
        var line = BankLine("Checking", -4400, "STAYS OUT");
        await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { line } });

        using var conn = _api.Open();
        var ids = new Tideline.Data.Repositories.LedgerRepository(conn, TimeProvider.System)
            .ExistingExternalIds([_api.Scalar<string>("SELECT external_id FROM transactions WHERE id = @line", new { line })]);

        ids.Should().HaveCount(1, "the row is still there, so a file with it again adds nothing");
    }
}
