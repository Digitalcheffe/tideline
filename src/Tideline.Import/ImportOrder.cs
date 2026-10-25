using Tideline.Import.Truist;

namespace Tideline.Import;

/// <summary>
/// Files are imported oldest first: an account's opening balance comes from its first file, so a newer file must not
/// go in before an older one. Files that cannot be read keep their place at the end and fail with their own reason.
/// </summary>
public static class ImportOrder
{
    public static IReadOnlyList<string> OldestFirst(IEnumerable<string> paths)
    {
        var keyed = paths.Select(p => (Path: p, First: FirstDate(p))).ToList();
        return keyed
            .OrderBy(k => k.First ?? DateOnly.MaxValue)
            .ThenBy(k => k.Path, StringComparer.OrdinalIgnoreCase)
            .Select(k => k.Path)
            .ToList();
    }

    private static DateOnly? FirstDate(string path)
    {
        try
        {
            var file = TruistParser.Parse(Path.GetFileName(path), File.ReadAllText(path));
            return file.Rows.Count == 0 ? null : file.Rows.Min(r => r.PostedOn);
        }
        catch (Exception e) when (e is ImportFileException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
