using System;
using System.Globalization;
using System.Windows.Data;

namespace SSHClient.App.Converters;

public sealed class RuleTypeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var type = value as string;
        return type switch
        {
            "All" => "全部",
            "IpCidr" => "IP 段",
            _ => "域名",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}