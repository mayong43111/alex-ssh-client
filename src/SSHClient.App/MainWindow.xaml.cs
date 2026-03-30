using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using SSHClient.App.Services;

using SSHClient.App.ViewModels;

namespace SSHClient.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _closingHandled;
    private readonly ITrayBehaviorService _trayBehaviorService;
    private readonly IMainWindowActionService _mainWindowActionService;

    public MainWindow(MainViewModel vm, ITrayBehaviorService trayBehaviorService, IMainWindowActionService mainWindowActionService)
    {
        InitializeComponent();
        DataContext = vm;
        _trayBehaviorService = trayBehaviorService;
        _mainWindowActionService = mainWindowActionService;
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _trayBehaviorService.AttachAsync(this);
        UpdateMinimizeMenuState();
    }

    private async void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        await _trayBehaviorService.HandleWindowStateChangedAsync();
        UpdateMinimizeMenuState();
    }

    private void UpdateMinimizeMenuState()
    {
        TaskbarMinimizeMenuItem.IsChecked = _trayBehaviorService.IsTaskbarModeSelected;
        TrayMinimizeMenuItem.IsChecked = _trayBehaviorService.IsTrayModeSelected;
    }

    private void MinimizeBehaviorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async void TaskbarMinimizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await _trayBehaviorService.SetTaskbarModeAsync();
        UpdateMinimizeMenuState();
    }

    private async void TrayMinimizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await _trayBehaviorService.SetTrayModeAsync();
        UpdateMinimizeMenuState();
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        await _mainWindowActionService.HandleSaveAsAsync(this, vm);
    }

    private async void LoadFromFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        await _mainWindowActionService.HandleLoadFromFileAsync(this, vm);
    }

    private async void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        await _mainWindowActionService.HandleAddRuleAsync(this, vm);
    }

    private async void EditRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }
        await _mainWindowActionService.HandleEditRuleAsync(this, vm);
    }

    private async void RulesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        if (IsFromComboBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        await _mainWindowActionService.HandleEditRuleAsync(this, vm);
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
                await _mainWindowActionService.HandlePrepareExitAsync(vm);
            }
        }
        finally
        {
            _trayBehaviorService.Dispose();
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

    private void MonitorCopyAddressMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorConnectionsDataGrid.SelectedItem is not ConnectionRowViewModel row)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.MonitorVM.CopyAddressCommand.Execute(row.HostPort);
    }

    private void MonitorClearDataMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.MonitorVM.ClearMonitorDataCommand.Execute(null);
    }

    private static bool IsFromComboBox(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ComboBox || current is ComboBoxItem)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}