using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;

using SSHClient.App.ViewModels;

namespace SSHClient.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _closingHandled;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += MainWindow_Closing;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(this, "请先选择配置，再新增规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RuleEditorWindow(
            vm.ProfilesVM.GetSuggestedRuleName(),
            vm.ProfilesVM.GetSuggestedRulePriority())
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.CreatedRule is null)
        {
            return;
        }

        await vm.ProfilesVM.AddRuleFromDialogAsync(dialog.CreatedRule);
    }

    private async void EditRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(this, "请先选择配置，再编辑规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedRule = vm.ProfilesVM.SelectedRule;
        if (selectedRule is null)
        {
            MessageBox.Show(this, "请先在列表中选择一条规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RuleEditorWindow(
            selectedRule,
            actionOnlyMode: vm.ProfilesVM.IsDefaultRuleItem(selectedRule))
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true || dialog.CreatedRule is null)
        {
            return;
        }

        await vm.ProfilesVM.UpdateRuleFromDialogAsync(selectedRule, dialog.CreatedRule);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingHandled)
        {
            return;
        }

        e.Cancel = true;
        _closingHandled = true;

        try
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.ProfilesVM.PrepareForAppExitAsync();
            }
        }
        finally
        {
            Close();
        }
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        textBox.CaretIndex = textBox.Text.Length;
        textBox.ScrollToEnd();
    }
}