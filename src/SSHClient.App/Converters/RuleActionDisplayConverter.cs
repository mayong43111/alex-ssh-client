using System;
using System.Globalization;
using System.Windows.Data;
using SSHClient.Core.Models;

namespace SSHClient.App.Converters;

public sealed class RuleActionDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not RuleAction action)
        {
            return string.Empty;
        }

        return action switch
        {
            RuleAction.Proxy => "代理",
            RuleAction.Direct => "直连",
            RuleAction.Reject => "拒绝",
            _ => action.ToString(),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}