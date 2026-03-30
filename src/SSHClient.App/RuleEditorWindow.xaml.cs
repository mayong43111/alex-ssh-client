using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using SSHClient.Core.Models;

namespace SSHClient.App;

public partial class RuleEditorWindow : Window
{
    private const int MaxEditablePriority = 9998;
    private readonly bool _actionOnlyMode;
    private readonly ProxyRule? _sourceRule;

    public ProxyRule? CreatedRule { get; private set; }

    public RuleEditorWindow(string suggestedName, int suggestedPriority)
    {
        InitializeComponent();
        _actionOnlyMode = false;

        RuleNameTextBox.Text = string.IsNullOrWhiteSpace(suggestedName) ? "规则-1" : suggestedName;
        RulePriorityTextBox.Text = Math.Clamp(suggestedPriority, 1, MaxEditablePriority).ToString(CultureInfo.InvariantCulture);
        SelectRuleType("DomainSuffix");
        SetPatternInputForType("DomainSuffix", "*");
        Loaded += (_, _) => RuleNameTextBox.Focus();
    }

    public RuleEditorWindow(ProxyRule sourceRule, bool actionOnlyMode)
    {
        ArgumentNullException.ThrowIfNull(sourceRule);

        InitializeComponent();
        _sourceRule = sourceRule;
        _actionOnlyMode = actionOnlyMode;

        Title = actionOnlyMode ? "编辑规则（仅动作）" : "编辑规则";
        RuleNameTextBox.Text = sourceRule.Name;
        RulePriorityTextBox.Text = sourceRule.Priority.ToString(CultureInfo.InvariantCulture);

        SelectRuleType(sourceRule.Type);
        SetPatternInputForType(sourceRule.Type, sourceRule.Pattern);
        SelectRuleAction(sourceRule.Action);

        if (actionOnlyMode)
        {
            RuleNameTextBox.IsReadOnly = true;
            RulePriorityTextBox.IsReadOnly = true;
            RuleTypeComboBox.IsEnabled = false;
            DomainPatternsTextBox.IsReadOnly = true;
            IpAddressTextBox.IsReadOnly = true;
            IpPrefixTextBox.IsReadOnly = true;
            Loaded += (_, _) => RuleActionComboBox.Focus();
            return;
        }

        Loaded += (_, _) => RuleNameTextBox.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var name = RuleNameTextBox.Text.Trim();
        int priority;
        var type = (RuleTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        string pattern;

        if (_actionOnlyMode)
        {
            priority = _sourceRule?.Priority ?? DefaultActionOnlyPriority();
            type = _sourceRule?.Type ?? "All";
            pattern = _sourceRule?.Pattern ?? "*";

            if (string.IsNullOrWhiteSpace(name))
            {
                name = _sourceRule?.Name ?? "默认";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(this, "规则名称不能为空。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                RuleNameTextBox.Focus();
                return;
            }

            if (!int.TryParse(RulePriorityTextBox.Text.Trim(), out priority) || priority < 1 || priority > MaxEditablePriority)
            {
                MessageBox.Show(this, "优先级必须为 1-9998 的整数。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                RulePriorityTextBox.Focus();
                return;
            }

            if (!TryBuildPatternByType(type, out pattern))
            {
                return;
            }
        }

        var action = (RuleAction?)((RuleActionComboBox.SelectedItem as ComboBoxItem)?.Tag) ?? RuleAction.Proxy;

        CreatedRule = new ProxyRule
        {
            Name = name,
            Priority = priority,
            Pattern = pattern,
            Type = type,
            Action = action,
        };

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SelectRuleType(string? type)
    {
        var target = string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase)
            ? "IpCidr"
            : string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)
                ? "All"
                : "DomainSuffix";

        foreach (var item in RuleTypeComboBox.Items)
        {
            if (item is ComboBoxItem comboItem
                && string.Equals(comboItem.Tag as string, target, StringComparison.OrdinalIgnoreCase))
            {
                RuleTypeComboBox.SelectedItem = comboItem;
                return;
            }
        }

        RuleTypeComboBox.SelectedIndex = 0;
    }

    private void SelectRuleAction(RuleAction action)
    {
        foreach (var item in RuleActionComboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is RuleAction tag && tag == action)
            {
                RuleActionComboBox.SelectedItem = comboItem;
                return;
            }
        }

        RuleActionComboBox.SelectedIndex = 0;
    }

    private int DefaultActionOnlyPriority()
    {
        return MaxEditablePriority + 1;
    }

    private void SetPatternInputForType(string? type, string? pattern)
    {
        var normalizedType = string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase)
            ? "IpCidr"
            : string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)
                ? "All"
                : "DomainSuffix";

        var rawPattern = pattern ?? string.Empty;

        if (normalizedType == "IpCidr")
        {
            var parts = rawPattern.Split('/');
            if (parts.Length == 2)
            {
                IpAddressTextBox.Text = parts[0].Trim();
                IpPrefixTextBox.Text = string.IsNullOrWhiteSpace(parts[1]) ? "32" : parts[1].Trim();
            }
            else
            {
                IpAddressTextBox.Text = rawPattern.Trim();
                IpPrefixTextBox.Text = "32";
            }

            DomainPatternsTextBox.Text = string.Empty;
            return;
        }

        if (normalizedType == "All")
        {
            DomainPatternsTextBox.Text = string.Empty;
            IpAddressTextBox.Text = string.Empty;
            IpPrefixTextBox.Text = "32";
            return;
        }

        var entries = SplitDomainEntries(rawPattern);
        DomainPatternsTextBox.Text = string.Join(Environment.NewLine, entries);
        IpAddressTextBox.Text = string.Empty;
        IpPrefixTextBox.Text = "32";
    }

    private bool TryBuildPatternByType(string type, out string pattern)
    {
        pattern = "*";

        if (string.Equals(type, "All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase))
        {
            var ipText = IpAddressTextBox.Text.Trim();
            if (!IPAddress.TryParse(ipText, out var ipAddress))
            {
                MessageBox.Show(this, "IP 地址格式无效。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                IpAddressTextBox.Focus();
                return false;
            }

            var prefixText = IpPrefixTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(prefixText))
            {
                prefixText = "32";
            }

            if (!int.TryParse(prefixText, out var prefix))
            {
                MessageBox.Show(this, "CIDR 前缀必须是数字。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                IpPrefixTextBox.Focus();
                return false;
            }

            var maxPrefix = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (prefix < 0 || prefix > maxPrefix)
            {
                MessageBox.Show(this, $"CIDR 前缀范围应为 0-{maxPrefix}。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
                IpPrefixTextBox.Focus();
                return false;
            }

            pattern = $"{ipText}/{prefix}";
            return true;
        }

        var domains = SplitDomainEntries(DomainPatternsTextBox.Text)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (domains.Count == 0)
        {
            MessageBox.Show(this, "请至少输入一个域名匹配。", "输入校验", MessageBoxButton.OK, MessageBoxImage.Warning);
            DomainPatternsTextBox.Focus();
            return false;
        }

        pattern = string.Join(';', domains);
        return true;
    }

    private static List<string> SplitDomainEntries(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new List<string>();
        }

        return input
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }
}
