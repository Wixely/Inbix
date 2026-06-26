using System.Data;
using System.Globalization;
using Dapper;

namespace Inbix.Data.Dapper;

/// <summary>
/// Stores <see cref="DateOnly"/> as ISO <c>yyyy-MM-dd</c> TEXT (SQLite has no date type). Registered
/// for both <c>DateOnly</c> and <c>DateOnly?</c> — used for identity dates of birth.
/// </summary>
public sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Parse(object value)
        => DateOnly.ParseExact((string)value, Format, CultureInfo.InvariantCulture);

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString(Format, CultureInfo.InvariantCulture);
    }
}
