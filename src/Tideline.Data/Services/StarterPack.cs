namespace Tideline.Data.Services;

/// <summary>
/// The starter categories and rules that ship inside the program (Starter/starter-pack.json). It is a household file with only categories and
/// rules, so <see cref="HouseholdSetup.Apply(HouseholdSetup.Household, System.Data.IDbConnection, System.Data.IDbTransaction, TimeProvider)"/> loads it and
/// adds only what is missing. It holds no household data: national brands and plain bank wording.
/// </summary>
public static class StarterPack
{
    public const string RuleNamePrefix = "Starter: ";

    public static HouseholdSetup.Household Load()
    {
        using var stream = typeof(StarterPack).Assembly.GetManifestResourceStream("starter-pack.json")
            ?? throw new InvalidDataException("The starter pack is missing from this build.");
        using var reader = new StreamReader(stream);
        return HouseholdSetup.Parse(reader.ReadToEnd());
    }
}
