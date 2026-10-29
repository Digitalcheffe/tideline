using System.Reflection;
using FluentMigrator;

namespace Tideline.Data.Migrations;

/// <summary>
/// A migration whose DDL lives in an embedded .sql file. The files are slices of the schema contract
/// (docs/db/schema.sqlite.sql); SchemaContractTests proves the migrated database matches it.
/// </summary>
public abstract class ScriptMigration : Migration
{
    protected abstract string ScriptName { get; }

    public override void Up() => Execute.Sql(Read($"{ScriptName}.sqlite.sql"));

    public override void Down() => throw new NotSupportedException("Migrations are forward-only.");

    private static string Read(string file)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

[Migration(1, "Initial schema")]
public class M0001_Initial : ScriptMigration
{
    protected override string ScriptName => "M0001_Initial";
}

[Migration(2, "Guards: append-only audit log, read-only bank fields")]
public class M0002_Guards : ScriptMigration
{
    protected override string ScriptName => "M0002_Guards";
}

[Migration(3, "Savings accounts: the interest rate they pay")]
public class M0003_AccountRate : ScriptMigration
{
    protected override string ScriptName => "M0003_AccountRate";
}

[Migration(4, "Account numbers: a keyed hash and a sealed last 4 instead of digits in plain text")]
public class M0004_AccountNumbers : ScriptMigration
{
    protected override string ScriptName => "M0004_AccountNumbers";
}

[Migration(5, "Recommendations: suggestion rules on flags, and the ones a person has dismissed")]
public class M0005_Recommendations : ScriptMigration
{
    protected override string ScriptName => "M0005_Recommendations";
}

[Migration(6, "Imports: a failed one can be dismissed from the list")]
public class M0006_ImportDismissed : ScriptMigration
{
    protected override string ScriptName => "M0006_ImportDismissed";
}

[Migration(7, "Lines a person left out of the totals, and the import they were left out with")]
public class M0007_LeftOut : ScriptMigration
{
    protected override string ScriptName => "M0007_LeftOut";
}

[Migration(8, "A starter suggestion for the check_unknown alert: a check that needs a name")]
public class M0008_CheckSuggestion : ScriptMigration
{
    protected override string ScriptName => "M0008_CheckSuggestion";
}
