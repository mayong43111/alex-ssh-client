using Microsoft.Win32;
using System.Windows;

namespace SSHClient.App.Services;

public interface IProfileFileDialogService
{
    string? ChooseExportPath(Window owner, string initialDirectory, string fileName);
    string? ChooseImportPath(Window owner);
}

public sealed class ProfileFileDialogService : IProfileFileDialogService
{
    public string? ChooseExportPath(Window owner, string initialDirectory, string fileName)
    {
        var saveDialog = new SaveFileDialog
        {
            Title = "另存为配置文件",
            Filter = "配置文件 (*.profile.json)|*.profile.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckPathExists = true,
            AddExtension = true,
            DefaultExt = ".profile.json",
            OverwritePrompt = true,
            InitialDirectory = initialDirectory,
            FileName = fileName,
        };

        return saveDialog.ShowDialog(owner) == true ? saveDialog.FileName : null;
    }

    public string? ChooseImportPath(Window owner)
    {
        var openDialog = new OpenFileDialog
        {
            Title = "加载配置文件",
            Filter = "配置文件 (*.profile.json)|*.profile.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        return openDialog.ShowDialog(owner) == true ? openDialog.FileName : null;
    }
}
