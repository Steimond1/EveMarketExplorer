using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EveMarketExplorer.Converters;

public sealed class CompactNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return "";
        }

        var number = value switch
        {
            decimal decimalValue => (double)decimalValue,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
        };

        return Format(number);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string Format(double value)
    {
        var sign = value < 0 ? "-" : "";
        var absolute = Math.Abs(value);

        return absolute switch
        {
            >= 1_000_000_000 => $"{sign}{FormatScaled(absolute / 1_000_000_000)}b",
            >= 1_000_000 => $"{sign}{FormatScaled(absolute / 1_000_000)}m",
            >= 1_000 => $"{sign}{FormatScaled(absolute / 1_000)}k",
            _ => $"{sign}{FormatScaled(absolute)}"
        };
    }

    private static string FormatScaled(double value)
    {
        var rounded = Math.Round(value, value >= 100 ? 0 : 1);
        return rounded.ToString(rounded >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
    }
}
