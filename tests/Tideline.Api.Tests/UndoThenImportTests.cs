using System.Net;
using FluentAssertions;
using Tideline.Data.Repositories;

namespace Tideline.Api.Tests;

/// <summary>Undo an import, then send the file again: the lines come back and the balance agrees with the bank, never higher than one import.</summary>
public sealed class UndoThenImportTests : IDisposable
{
    private readonly ApiFixture _api = new(importFixtures: false);

    public void Dispose() => _api.Dispose();

    private const string File1 = "acct_1111_03_01_2026_to_03_31_2026.csv";

    private string Text(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "truist", name));

    private long Balance(long account)
    {
        using var conn = _api.Open();
        return new LedgerRepository(conn, TimeProvider.System).BalanceAsOf(account, new DateOnly(2026, 12, 31));
    }

    [Fact]
    public async Task Sending_a_file_again_after_undoing_its_import_brings_its_lines_back_and_the_balance_matches_the_bank()
    {
        var first = await _api.Pipeline.ImportTruistAsync(File1, Text(File1));
        first.Succeeded.Should().BeTrue(first.Error);
        var account = _api.Scalar<long>("SELECT account_id FROM transactions LIMIT 1");
        var lines = _api.Scalar<long>("SELECT COUNT(*) FROM transactions");
        var once = Balance(account);
        var opening = _api.Scalar<long?>("SELECT opening_balance_cents FROM accounts WHERE id = @account", new { account });

        (await _api.Send(HttpMethod.Post, $"/api/v1/imports/{first.BatchId}/undo")).Status.Should().Be(HttpStatusCode.OK);
        Balance(account).Should().Be(opening ?? 0, "every line is out, so only the opening balance is left");

        var again = await _api.Pipeline.ImportTruistAsync(File1, Text(File1));

        again.Succeeded.Should().BeTrue(again.Error);
        again.RowsInserted.Should().Be(0, "nothing is new");
        again.RowsRestored.Should().Be((int)lines, "every line of the undone import is back");
        again.Reconciliation!.Matches.Should().BeTrue("the ledger agrees with the bank again");
        Balance(account).Should().Be(once, "the same as one import, never higher");
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions").Should().Be(lines, "no line was added twice");
        _api.Scalar<long>("SELECT COUNT(*) FROM transactions WHERE left_out_at IS NOT NULL").Should().Be(0);
        _api.Scalar<long?>("SELECT opening_balance_cents FROM accounts WHERE id = @account", new { account }).Should().Be(opening, "the opening balance does not move");
        _api.Scalar<long>("SELECT COUNT(*) FROM audit_log WHERE action = 'transaction.bring_back'").Should().Be(lines);

        (await _api.Pipeline.ImportTruistAsync(File1, Text(File1))).RowsRestored.Should().Be(0, "a third time there is nothing to bring back");
    }

    [Fact]
    public async Task A_line_left_out_by_hand_stays_out_when_the_file_is_sent_again()
    {
        (await _api.Pipeline.ImportTruistAsync(File1, Text(File1))).Succeeded.Should().BeTrue();
        var line = _api.Scalar<long>("SELECT id FROM transactions WHERE move_id IS NULL ORDER BY id LIMIT 1");
        (await _api.Send(HttpMethod.Post, "/api/v1/transactions/leave-out", new { ids = new[] { line } })).Status.Should().Be(HttpStatusCode.OK);

        var again = await _api.Pipeline.ImportTruistAsync(File1, Text(File1));

        again.RowsRestored.Should().Be(0);
        _api.Scalar<string?>("SELECT left_out_at FROM transactions WHERE id = @line", new { line }).Should().NotBeNull();
    }
}
