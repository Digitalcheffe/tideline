using System.Data;
using System.IO.Compression;
using System.Text;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>
/// Every table as a CSV in one zip, for the owner to keep or move elsewhere. Nothing that signs people in or reaches outside is
/// in it: no password hashes, authenticator secrets, recovery codes, sessions, API tokens, channel settings or bank feed addresses.
/// </summary>
public static class ExportService
{
    /// <summary>Table name to the columns exported. A star means all of them; users are listed so the hashes stay out.</summary>
    public static readonly IReadOnlyDictionary<string, string> Tables = new Dictionary<string, string>
    {
        ["users"] = "id, email, display_name, role, is_active, created_at, last_login_at",
        ["people"] = "*",
        ["accounts"] = "*",
        ["card_terms"] = "*",
        ["loan_terms"] = "*",
        ["retirement_terms"] = "*",
        ["balance_snapshots"] = "*",
        ["categories"] = "*",
        ["tags"] = "*",
        ["import_batches"] = "*",
        ["recurring"] = "*",
        ["plans"] = "*",
        ["plan_checkpoints"] = "*",
        ["moves"] = "*",
        ["transactions"] = "*",
        ["transaction_tags"] = "*",
        ["split_lines"] = "*",
        ["split_line_tags"] = "*",
        ["rules"] = "*",
        ["rule_conditions"] = "*",
        ["rule_tags"] = "*",
        ["move_patterns"] = "*",
        ["budgets"] = "*",
        ["budget_lines"] = "*",
        ["retirement_goal"] = "*",
        ["flags"] = "*",
        ["audit_log"] = "*",
        ["system_events"] = "*",
    };

    public static void Write(IDbConnection conn, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (table, columns) in Tables)
        {
            var entry = zip.CreateEntry($"{table}.csv", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            using var reader = conn.ExecuteReader($"SELECT {columns} FROM {table} ORDER BY 1");

            writer.Write(string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader.GetName(i)))));
            writer.Write("\r\n");
            while (reader.Read())
            {
                writer.Write(string.Join(',', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Cell(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? ""))));
                writer.Write("\r\n");
            }
        }
    }

    /// <summary>Quoted when it holds a comma, quote or line break. A cell that starts like a formula gets a leading quote so a spreadsheet shows it as text.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '@' || (value.Length > 1 && value[0] == '-' && !char.IsDigit(value[1]) && value[1] != '.'))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
