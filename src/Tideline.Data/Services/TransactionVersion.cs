using System.Data;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>
/// The version of a line: when it last changed plus how many audited changes it has had. The count keeps two edits in the
/// same second apart, so a stale write is always noticed (docs/ARCHITECTURE.md).
/// </summary>
public static class TransactionVersion
{
    public static string Of(IDbConnection conn, long id, IDbTransaction? tx = null)
    {
        var updated = conn.ExecuteScalar<string?>("SELECT updated_at FROM transactions WHERE id = @id", new { id }, tx) ?? "";
        var edits = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE entity = 'transaction' AND entity_id = @key", new { key = id.ToString() }, tx);
        return $"{updated}/{edits}";
    }
}
