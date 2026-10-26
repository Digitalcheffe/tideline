namespace Tideline.Core.Moves;

/// <summary>How to recognise the two sides of a move between our own accounts (<c>move_patterns</c>).</summary>
public sealed record MovePattern(
    long Id,
    long FromAccountId,
    string FromContains,
    long ToAccountId,
    string ToContains,
    int MaxDaysApart = 5);

/// <summary>A ledger row that is not yet part of a complete move.</summary>
public sealed record MoveCandidate(long TransactionId, long AccountId, DateOnly PostedOn, long AmountCents, string Description);

public sealed record MovePair(long OutTransactionId, long InTransactionId, long PatternId);

/// <param name="Pairs">Complete moves to create.</param>
/// <param name="Pending">Rows that match a pattern but have no partner yet; retried on every import.</param>
public sealed record MovePairingResult(IReadOnlyList<MovePair> Pairs, IReadOnlyList<long> Pending);

public static class MovePairer
{
    /// <summary>After this many days without a partner the evaluator raises <c>move_unpaired</c>.</summary>
    public const int UnpairedFlagAfterDays = 10;

    /// <summary>
    /// docs/ARCHITECTURE.md. Per pattern, walk OUT rows by date ascending; each takes the unpaired IN
    /// row with the same absolute amount and the smallest day gap within the window (tie: earliest IN row).
    /// </summary>
    public static MovePairingResult Pair(IEnumerable<MoveCandidate> candidates, IEnumerable<MovePattern> patterns)
    {
        var rows = candidates.OrderBy(r => r.PostedOn).ThenBy(r => r.TransactionId).ToList();
        var used = new HashSet<long>();
        var matchedSomePattern = new HashSet<long>();
        var pairs = new List<MovePair>();

        foreach (var pattern in patterns.OrderBy(p => p.Id))
        {
            var outs = rows.Where(r => r.AccountId == pattern.FromAccountId && r.AmountCents < 0 && Contains(r, pattern.FromContains)).ToList();
            var ins = rows.Where(r => r.AccountId == pattern.ToAccountId && r.AmountCents > 0 && Contains(r, pattern.ToContains)).ToList();
            foreach (var r in outs.Concat(ins)) matchedSomePattern.Add(r.TransactionId);

            foreach (var o in outs)
            {
                if (used.Contains(o.TransactionId)) continue;

                var partner = ins
                    .Where(i => !used.Contains(i.TransactionId) && i.AmountCents == -o.AmountCents)
                    .Select(i => (Row: i, Gap: Math.Abs(i.PostedOn.DayNumber - o.PostedOn.DayNumber)))
                    .Where(x => x.Gap <= pattern.MaxDaysApart)
                    .OrderBy(x => x.Gap).ThenBy(x => x.Row.PostedOn).ThenBy(x => x.Row.TransactionId)
                    .Select(x => x.Row)
                    .FirstOrDefault();
                if (partner is null) continue;

                used.Add(o.TransactionId);
                used.Add(partner.TransactionId);
                pairs.Add(new MovePair(o.TransactionId, partner.TransactionId, pattern.Id));
            }
        }

        var pending = rows.Where(r => matchedSomePattern.Contains(r.TransactionId) && !used.Contains(r.TransactionId))
            .Select(r => r.TransactionId).ToList();
        return new MovePairingResult(pairs, pending);
    }

    private static bool Contains(MoveCandidate row, string text) =>
        row.Description.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>moves.counts_as</c>: 'savings' when exactly one leg is on a savings account, otherwise 'none'.</summary>
    public static MoveCountsAs CountsAs(AccountKind fromKind, AccountKind toKind) =>
        (fromKind == AccountKind.Savings) ^ (toKind == AccountKind.Savings) ? MoveCountsAs.Savings : MoveCountsAs.None;
}

public enum MoveCountsAs { None, Savings }
