using System.Windows;
using Serilog;
using SSHClient.App;
using SSHClient.App.ViewModels;

namespace SSHClient.App.Services;

public interface IMainWindowActionService
{
    Task HandleSaveAsAsync(Window owner, MainViewModel vm);
    Task HandleLoadFromFileAsync(Window owner, MainViewModel vm);
    Task HandleAddRuleAsync(Window owner, MainViewModel vm);
    Task HandleEditRuleAsync(Window owner, MainViewModel vm);
    Task HandleSetSystemProxyAsync(Window owner, MainViewModel vm);
    Task HandleRestoreSystemProxyAsync(Window owner, MainViewModel vm);
    Task HandlePreviewPacAsync(Window owner, MainViewModel vm);
    Task HandlePrepareExitAsync(MainViewModel vm);
}

public sealed class MainWindowActionService : IMainWindowActionService
{
    private readonly IProfileFileDialogService _profileFileDialogService;
    private readonly ISystemProxyApplicationService _systemProxyApplicationService;
    private readonly IPacPreviewService _pacPreviewService;

    public MainWindowActionService(
        IProfileFileDialogService profileFileDialogService,
        ISystemProxyApplicationService systemProxyApplicationService,
        IPacPreviewService pacPreviewService)
    {
        _profileFileDialogService = profileFileDialogService;
        _systemProxyApplicationService = systemProxyApplicationService;
        _pacPreviewService = pacPreviewService;
    }

    public async Task HandleSaveAsAsync(Window owner, MainViewModel vm)
    {
        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(owner, "请先选择配置，再执行另存为。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetFilePath = _profileFileDialogService.ChooseExportPath(
            owner,
            vm.ProfilesVM.GetCurrentProfileExportDirectory(),
            vm.ProfilesVM.GetCurrentProfileExportFileName());
        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            return;
        }

        var error = await vm.ProfilesVM.ExportSelectedProfileAsync(targetFilePath);
        if (!string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(owner, error, "另存为失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(owner, $"已保存到：{targetFilePath}", "另存为完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public async Task HandleLoadFromFileAsync(Window owner, MainViewModel vm)
    {
        try
        {
            var sourcePath = _profileFileDialogService.ChooseImportPath(owner);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            var error = await vm.ProfilesVM.ImportProfileFromFileAsync(sourcePath);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(owner, error, "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(owner, $"已加载：{sourcePath}", "加载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载配置文件异常");
            MessageBox.Show(owner, $"加载失败：{ex.Message}", "加载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task HandleAddRuleAsync(Window owner, MainViewModel vm)
    {
        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(owner, "请先选择配置，再新增规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RuleEditorWindow(
            vm.ProfilesVM.GetSuggestedRuleName(),
            vm.ProfilesVM.GetSuggestedRulePriority())
        {
            Owner = owner,
        };

        if (dialog.ShowDialog() != true || dialog.CreatedRule is null)
        {
            return;
        }

        await vm.ProfilesVM.AddRuleFromDialogAsync(dialog.CreatedRule);
    }

    public async Task HandleEditRuleAsync(Window owner, MainViewModel vm)
    {
        if (vm.ProfilesVM.SelectedProfile is null)
        {
            MessageBox.Show(owner, "请先选择配置，再编辑规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedRule = vm.ProfilesVM.SelectedRule;
        if (selectedRule is null)
        {
            MessageBox.Show(owner, "请先在列表中选择一条规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (vm.ProfilesVM.IsDefaultRuleItem(selectedRule))
        {
            MessageBox.Show(owner, "默认规则不允许编辑。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RuleEditorWindow(
            selectedRule,
            actionOnlyMode: false)
        {
            Owner = owner,
        };

        if (dialog.ShowDialog() != true || dialog.CreatedRule is null)
        {
            return;
        }

        await vm.ProfilesVM.UpdateRuleFromDialogAsync(selectedRule, dialog.CreatedRule);
    }

    public async Task HandleSetSystemProxyAsync(Window owner, MainViewModel vm)
    {
        if (!vm.ProfilesVM.IsLoggedIn)
        {
            var continueWithoutLogin = MessageBox.Show(
                owner,
                "当前未登录，设置系统代理后可能无法访问外网。是否继续？",
                "系统代理设置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (continueWithoutLogin != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            var result = await _systemProxyApplicationService.ApplyPacAsync(vm.ProfilesVM.Rules);
            if (result.Status == SystemProxyApplyStatus.Cancelled)
            {
                MessageBox.Show(owner, "已取消管理员授权，系统代理未更新。", "系统代理设置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (result.Status == SystemProxyApplyStatus.InvalidListenPort)
            {
                MessageBox.Show(owner, "监听端口无效，无法设置系统代理。", "系统代理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.Status == SystemProxyApplyStatus.InvalidPacScriptPort)
            {
                MessageBox.Show(owner, "PAC 脚本端口无效，无法设置系统代理。", "系统代理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.Status != SystemProxyApplyStatus.Success || string.IsNullOrWhiteSpace(result.ScriptUrl))
            {
                throw new InvalidOperationException("系统代理设置结果无效。");
            }

            MessageBox.Show(owner, $"系统自动代理脚本已启用：{result.ScriptUrl}\n代理规则数：{result.ProxyRuleCount}（仅这些规则走软件）。", "系统代理设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设置系统代理失败");
            MessageBox.Show(owner, $"设置系统代理失败：{ex.Message}", "系统代理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task HandleRestoreSystemProxyAsync(Window owner, MainViewModel vm)
    {
        try
        {
            var result = await _systemProxyApplicationService.RestorePacAsync();
            if (result.Status == SystemProxyRestoreStatus.Cancelled)
            {
                MessageBox.Show(owner, "已取消管理员授权，系统代理未恢复。", "恢复系统代理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBox.Show(owner, "系统代理已恢复默认设置。", "恢复系统代理", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "恢复系统代理设置失败");
            MessageBox.Show(owner, $"恢复系统代理失败：{ex.Message}", "恢复系统代理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task HandlePreviewPacAsync(Window owner, MainViewModel vm)
    {
        try
        {
            var script = await _pacPreviewService.BuildPreviewScriptAsync(vm.ProfilesVM.Rules);
            var previewWindow = new PacPreviewWindow(script)
            {
                Owner = owner,
            };

            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "生成 PAC 预览失败");
            MessageBox.Show(owner, $"生成 PAC 预览失败：{ex.Message}", "PAC 预览", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task HandlePrepareExitAsync(MainViewModel vm)
    {
        await vm.ProfilesVM.PrepareForAppExitAsync();
    }
}
