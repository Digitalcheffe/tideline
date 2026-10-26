using System.Data;
using Dapper;

namespace Tideline.Data.Services;

/// <summary>
/// Clearing a failed import from the Imports list. The batch row stays (it is a record of what was tried) and the change is audited;
/// it just stops being listed and stops raising the "import failed" alert.
/// </summary>
public sealed class ImportBatchEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    public void Dismiss(long id) => InTransaction((tx, audit) =>
    {
        var batch = Conn.QuerySingleOrDefault<(string Status, string? File, string? Error, string? Dismissed)?>(
            "SELECT status, file_name, error, dismissed_at FROM import_batches WHERE id = @id", new { id }, tx) ?? throw Missing("import");
        if (batch.Status != "failed") throw new EditException(EditError.Unprocessable, "Only an import that failed can be dismissed.");
        if (batch.Dismissed is not null) throw new EditException(EditError.Conflict, "That one has already been dismissed.");

        Conn.Execute("UPDATE import_batches SET dismissed_at = @now WHERE id = @id", new { id, now = Clock.GetUtcNow() }, tx);
        audit.Write("import.dismissed", "import_batch", id, new { batch.Status, file = batch.File, batch.Error, dismissed = false }, new { batch.Status, file = batch.File, batch.Error, dismissed = true });
    });
}
