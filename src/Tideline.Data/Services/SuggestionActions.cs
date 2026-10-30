using System.Data;
using Dapper;
using Tideline.Core;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

/// <summary>
/// The fixed steps a suggestion card can offer (<see cref="SuggestionCatalog.MarkOneTime"/> and so on). Each is named in code, only a person asking for it
/// runs it, and it goes through the same editor as doing it by hand, so it is checked and audited the same way. Nothing is ever created silently.
/// </summary>
public static class SuggestionActions
{
    public static object Run(IDbConnection conn, TimeProvider clock, Actor? actor, long flagId)
    {
        TypeHandlers.EnsureRegistered();
        var flag = new FlagRepository(conn, clock).Get(flagId) ?? throw new EditException(EditError.NotFound, "There is no such card.");
        var action = SuggestionCatalog.Find(flag.Kind)?.Action ?? throw new EditException(EditError.Invalid, "This suggestion has no button.");

        switch (action)
        {
            case SuggestionCatalog.MarkOneTime:
                if (flag.SubjectType != "transaction") throw new EditException(EditError.Invalid, "This card is not about a single line.");
                var line = flag.SubjectId;
                new TransactionEditor(conn, clock, actor).Update(line, new EditRequest(TransactionVersion.Of(conn, line), Type: "one_time"));
                conn.Execute("UPDATE flags SET resolved_at = @now WHERE id = @flagId AND resolved_at IS NULL", new { flagId, now = clock.GetUtcNow() });
                return new { done = true, line };
            case SuggestionCatalog.AddRecurring:
                if (flag.SubjectType != "candidate") throw new EditException(EditError.Invalid, "This card is not about a bill.");
                var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
                var added = BillAdder.Add(conn, clock, actor, [(flag.SubjectId, null)], today);
                conn.Execute("UPDATE flags SET resolved_at = @now WHERE id = @flagId AND resolved_at IS NULL", new { flagId, now = clock.GetUtcNow() });
                return new { done = true, added = added[0].Name, linked = added[0].LinesLinked };
            default:
                throw new EditException(EditError.Invalid, "That step is not available.");
        }
    }
}
