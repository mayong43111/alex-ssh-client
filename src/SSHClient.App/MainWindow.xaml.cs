using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

using SSHClient.App.ViewModels;

namespace SSHClient.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _closingHandled;
    private readonly Forms.NotifyIcon _trayIcon;
    private bool _trayHintShown;
    private bool? _minimizeToTray;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;

        _trayIcon = CreateTrayIcon();
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.ContextMenuStrip = BuildTrayContextMenu();
    }

    private static Forms.NotifyIcon CreateTrayIcon()
    {
        System.Drawing.Icon icon;
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        else
        {
            icon = SystemIcons.Application;
        }

        return new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "SSH 客户端",
            Visible = false,
        };
    }

    private Forms.ContextMenuStrip BuildTrayContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        return menu;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _minimizeToTray = await vm.ProfilesVM.GetMinimizeToTrayPreferenceAsync();
        UpdateMinimizeMenuState();
    }

    private async void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var behavior = _minimizeToTray;
        if (!behavior.HasValue)
        {
            var result = MessageBox.Show(
                this,
                "首次最小化请选择行为：\n是：最小化到托盘（后台运行）\n否：仅最小化到任务栏\n取消：本次仅最小化，稍后再选\n\n后续可通过右上角齿轮图标修改。",
                "最小化行为",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                behavior = true;
                await vm.ProfilesVM.SetMinimizeToTrayPreferenceAsync(true);
                _minimizeToTray = true;
            }
            else if (result == MessageBoxResult.No)
            {
                behavior = false;
                await vm.ProfilesVM.SetMinimizeToTrayPreferenceAsync(false);
                _minimizeToTray = false;
            }
            else
            {
                behavior = false;
            }

            UpdateMinimizeMenuState();
        }

        if (behavior == true)
        {
            MinimizeToTray();
        }
    }

    private void MinimizeToTray()
    {
        ShowInTaskbar = false;
        Hide();

        _trayIcon.Visible = true;
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.BalloonTipTitle = "SSH 客户端";
        _trayIcon.BalloonTipText = "程序已最小化到托盘，双击图标可恢复窗口。";
        _trayIcon.ShowBalloonTip(1500);
    }

    private void RestoreFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(RestoreFromTray);
            return;
        }

        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private void UpdateMinimizeMenuState()
    {
        TaskbarMinimizeMenuItem.IsChecked = _minimizeToTray == false;
        TrayMinimizeMenuItem.IsChecked = _minimizeToTray == true;
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
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _minimizeToTray = false;
        await vm.ProfilesVM.SetMinimizeToTrayPreferenceAsync(false);
        UpdateMinimizeMenuState();
        RestoreFromTray();
    }

    private async void TrayMinimizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        _minimizeToTray = true;
        await vm.ProfilesVM.SetMinimizeToTrayPreferenceAsync(true);
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

        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(this, "请先选择配置，再执行另存为。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "另存为配置文件",
            Filter = "配置文件 (*.profile.json)|*.profile.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckPathExists = true,
            AddExtension = true,
            DefaultExt = ".profile.json",
            OverwritePrompt = true,
            InitialDirectory = vm.ProfilesVM.GetCurrentProfileExportDirectory(),
            FileName = vm.ProfilesVM.GetCurrentProfileExportFileName(),
        };

        if (saveDialog.ShowDialog(this) != true)
        {
            return;
        }

        var targetFilePath = saveDialog.FileName;

        var error = await vm.ProfilesVM.ExportSelectedProfileAsync(targetFilePath);
        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(this, error, "另存为失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this, $"已保存到：{targetFilePath}", "另存为完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void LoadFromFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Title = "加载配置文件",
            Filter = "配置文件 (*.profile.json)|*.profile.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        var error = await vm.ProfilesVM.ImportProfileFromFileAsync(openDialog.FileName);
        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(this, error, "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this, $"已加载：{openDialog.FileName}", "加载完成", MessageBoxButton.OK, MessageBoxImage.Information);
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
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
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