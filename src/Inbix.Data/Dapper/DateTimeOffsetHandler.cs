using System.Data;
using System.Globalization;
using Dapper;

namespace Inbix.Data.Dapper;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as round-trippable ISO-8601 TEXT, which keeps SQLite
/// ordering correct and is portable across providers. Dapper registers this for both
/// <c>DateTimeOffset</c> and <c>DateTimeOffset?</c>.
/// </summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override DateTimeOffset Parse(object value)
        => DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
