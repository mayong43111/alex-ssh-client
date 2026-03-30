using System.Windows;
using Serilog;
using SSHClient.App.ViewModels;

namespace SSHClient.App.Services;

public interface IMainWindowActionService
{
    Task HandleSaveAsAsync(Window owner, MainViewModel vm);
    Task HandleLoadFromFileAsync(Window owner, MainViewModel vm);
    Task HandleAddRuleAsync(Window owner, MainViewModel vm);
    Task HandleEditRuleAsync(Window owner, MainViewModel vm);
    Task HandlePrepareExitAsync(MainViewModel vm);
}

public sealed class MainWindowActionService : IMainWindowActionService
{
    private readonly IProfileFileDialogService _profileFileDialogService;

    public MainWindowActionService(IProfileFileDialogService profileFileDialogService)
    {
        _profileFileDialogService = profileFileDialogService;
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

    public async Task HandlePrepareExitAsync(MainViewModel vm)
    {
        await vm.ProfilesVM.PrepareForAppExitAsync();
    }
}
