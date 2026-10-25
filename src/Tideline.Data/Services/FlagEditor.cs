using System.Data;
using Tideline.Data.Repositories;

namespace Tideline.Data.Services;

/// <summary>A person saying "yes, I know about it". The flag stays, quiet, with who said so and why.</summary>
public sealed class FlagEditor(IDbConnection conn, TimeProvider clock, Actor? actor = null) : EditorBase(conn, clock, actor)
{
    public void Acknowledge(long id, string? note) => InTransaction((tx, audit) =>
    {
        var flags = new FlagRepository(Conn, Clock, tx);
        var before = flags.Get(id) ?? throw Missing("flag");
        if (before.AcknowledgedAt is not null) throw Bad("That one has already been acknowledged.");
        var text = Blank(note);
        if (text is { Length: > 400 }) throw Bad("The note is too long (400 characters at most).");

        flags.Acknowledge(id, UserId, text);
        audit.Write("flag.acknowledged", "flag", id, new { before.Kind, before.Period, acknowledged = false }, new { before.Kind, before.Period, acknowledged = true, note = text });
    });
}
