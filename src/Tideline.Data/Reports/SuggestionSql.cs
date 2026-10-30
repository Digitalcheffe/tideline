namespace Tideline.Data.Reports;

/// <summary>The effective-lines SQL the screens count from, for the suggestion checks in Tideline.Notify, so a card and a screen always agree.</summary>
public static class SuggestionSql
{
    public static string Ctes => EffectiveSql.Ctes;
}
