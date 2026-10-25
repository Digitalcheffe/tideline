namespace Tideline.Import.Truist;

/// <param name="StartCents">Balance before the first day, implied by the first day's bank balance.</param>
/// <param name="EndCents">Rebuilt balance after the last day.</param>
/// <param name="BadDays">Days where the rebuilt balance differs from the bank's by more than one cent.</param>
public sealed record FileCheckResult(long StartCents, long EndCents, int BadDays)
{
    public bool Passed => BadDays == 0;

    public string FailureMessage =>
        $"Running balance does not match Truist's on {BadDays} day(s); rows are missing or doubled. Export the file again.";
}

public static class FileCheck
{
    /// <summary>
    /// Deposit files only (docs/05-import.md, "The file check"): rebuild the running balance day by day and
    /// compare with Truist's balance, which sits on the day's last row in file order. Returns null for files with
    /// no balance column (credit cards), which are checked after load instead.
    /// </summary>
    public static FileCheckResult? Run(IReadOnlyList<TruistRow> rows)
    {
        var days = rows.GroupBy(r => r.PostedOn).OrderBy(g => g.Key).ToList();
        if (days.Count == 0 || days[0].Last().BankBalanceCents is null) return null;

        var first = days[0];
        var start = first.Last().BankBalanceCents!.Value - first.Sum(r => r.AmountCents);

        var running = start;
        var bad = 0;
        foreach (var day in days)
        {
            running += day.Sum(r => r.AmountCents);
            var bank = day.Last().BankBalanceCents;
            if (bank is not null && Math.Abs(running - bank.Value) > 1) bad++;
        }
        return new FileCheckResult(start, running, bad);
    }
}
