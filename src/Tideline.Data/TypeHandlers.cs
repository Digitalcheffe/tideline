using System.Data;
using System.Globalization;
using System.Text;
using Dapper;

namespace Tideline.Data;

/// <summary>
/// Dapper mappings: dates as 'YYYY-MM-DD', instants as ISO-8601 UTC. Enums are stored as snake_case text, but
/// Dapper never consults type handlers for enums (it writes the integer and reads with Enum.Parse), so
/// repositories convert explicitly with <see cref="ToDb{T}"/> and <see cref="FromDb{T}"/>.
/// The CHECK constraints on enum columns reject integers if someone forgets.
/// </summary>
public static class TypeHandlers
{
    private static readonly object Lock = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Lock)
        {
            if (_registered) return;
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset)); // Dapper maps it natively and would skip our handler
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
            SqlMapper.AddTypeHandler(new DateOnlyHandler());
            SqlMapper.AddTypeHandler(new InstantHandler());
            _registered = true;
        }
    }

    public static string ToDb<T>(this T value) where T : struct, Enum => ToSnakeCase(value.ToString());

    public static T FromDb<T>(string text) where T : struct, Enum =>
        EnumNames<T>.ByText.TryGetValue(text, out var v) ? v : throw new InvalidDataException($"'{text}' is not a valid {typeof(T).Name}.");

    private static class EnumNames<T> where T : struct, Enum
    {
        public static readonly Dictionary<string, T> ByText = Enum.GetValues<T>().ToDictionary(v => ToSnakeCase(v.ToString()));
    }

    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

internal sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public override DateOnly Parse(object value) =>
        DateOnly.ParseExact((string)value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

internal sealed class InstantHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
    }

    public override DateTimeOffset Parse(object value) =>
        DateTimeOffset.ParseExact((string)value, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
