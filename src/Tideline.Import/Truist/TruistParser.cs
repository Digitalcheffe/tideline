using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tideline.Import.Truist;

public sealed class ImportFileException(string message) : Exception(message);

public sealed record TruistFile(string FileName, string BankNumber, TruistFormat Format, IReadOnlyList<TruistRow> Rows);

/// <summary>Reads Truist deposit and credit card CSV exports (docs/ARCHITECTURE.md).</summary>
public static partial class TruistParser
{
    [GeneratedRegex(@"acct_(\d{4})_", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy", "M/d/yy", "yyyy-MM-dd"];

    public static string? BankNumberFromFileName(string fileName)
    {
        var m = FileNamePattern().Match(Path.GetFileName(fileName));
        return m.Success ? m.Groups[1].Value : null;
    }

    public static TruistFile Parse(string fileName, string csvText)
    {
        var name = Path.GetFileName(fileName);
        var bank = BankNumberFromFileName(name)
            ?? throw new ImportFileException($"{name}: expected a name like acct_1234_01_01_2026_to_02_01_2026.csv");

        var table = Csv.Read(csvText);
        if (table.Count == 0) throw new ImportFileException($"{name}: the file is empty.");

        var header = table[0].Select(h => h.Trim()).ToList();
        var index = header.Select((h, i) => (h, i)).GroupBy(x => x.h).ToDictionary(g => g.Key, g => g.First().i);

        var isDeposit = index.ContainsKey("Full description");
        var isCard = index.ContainsKey("Description") && !isDeposit;
        if (!isDeposit && !isCard)
            throw new ImportFileException($"{name}: not a Truist deposit or credit card export (no Description column).");
        foreach (var required in new[] { "Posted Date", "Amount" })
            if (!index.ContainsKey(required)) throw new ImportFileException($"{name}: missing the '{required}' column.");

        string Cell(List<string> rec, string column) =>
            index.TryGetValue(column, out var i) && i < rec.Count ? rec[i].Trim() : "";

        var rows = new List<TruistRow>();
        var seen = new Dictionary<(DateOnly, string, long), int>();

        for (var line = 1; line < table.Count; line++)
        {
            var rec = table[line];
            var postedText = Cell(rec, "Posted Date");
            if (postedText.Length == 0) continue;

            var lineNo = line + 1;
            var posted = ParseDate(postedText) ?? throw new ImportFileException($"{name} line {lineNo}: unrecognised date '{postedText}'.");
            var description = Cell(rec, isDeposit ? "Full description" : "Description");
            var amount = ParseAmountCents(Cell(rec, "Amount"))
                ?? throw new ImportFileException($"{name} line {lineNo}: unrecognised amount '{Cell(rec, "Amount")}'.");
            if (isCard) amount = -amount; // card exports show purchases positive and payments in parentheses

            long? balance = null;
            if (isDeposit)
            {
                var balanceText = Cell(rec, "Daily Posted Balance");
                if (balanceText.Length > 0)
                    balance = ParseAmountCents(balanceText)
                        ?? throw new ImportFileException($"{name} line {lineNo}: unrecognised balance '{balanceText}'.");
            }

            var key = (posted, description, amount);
            seen.TryGetValue(key, out var occurrence);
            seen[key] = occurrence + 1;

            var raw = header.Select((h, i) => (h, v: i < rec.Count ? rec[i] : ""))
                .Where(x => x.h.Length > 0)
                .DistinctBy(x => x.h)
                .ToDictionary(x => x.h, x => x.v);

            rows.Add(new TruistRow(
                bank, posted, description, amount,
                Cell(rec, "Merchant name"), Cell(rec, "Category name"), balance, Cell(rec, "Check/Serial #"),
                name, occurrence, JsonSerializer.Serialize(raw)));
        }

        return new TruistFile(name, bank, isDeposit ? TruistFormat.Deposit : TruistFormat.Card, rows);
    }

    /// <summary>Strip $ and commas; <c>(12.34)</c> or <c>-12.34</c> is negative. Rounds half away from zero.</summary>
    public static long? ParseAmountCents(string text)
    {
        var s = text.Trim().Replace("$", "").Replace(",", "");
        if (s.Length == 0) return 0;

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')')) { negative = true; s = s[1..^1].Trim(); }
        if (s.StartsWith('-')) { negative = true; s = s[1..].Trim(); }
        if (s.StartsWith('+')) s = s[1..].Trim();

        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        var cents = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        return negative ? -cents : cents;
    }

    public static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
