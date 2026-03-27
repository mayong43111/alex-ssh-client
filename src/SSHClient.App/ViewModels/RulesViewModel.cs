using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

public partial class RulesViewModel : ObservableObject
{
    private readonly IConfigService _configService;

    public ObservableCollection<ProxyRule> Rules { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    private ProxyRule? _selectedRule;

    public RulesViewModel(IConfigService configService)
    {
        _configService = configService;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var settings = await _configService.LoadAsync();
        Rules.Clear();
        foreach (var rule in settings.Rules)
        {
            Rules.Add(rule);
        }
    }

    [RelayCommand]
    public async Task AddRuleAsync()
    {
        var rule = new ProxyRule { Name = $"Rule-{Rules.Count + 1}", Pattern = "*", Action = RuleAction.Proxy, Type = "All" };
        Rules.Add(rule);
        await SaveAsync();
    }

    [RelayCommand]
    public async Task ExportRulesAsync()
    {
        var settings = await _configService.LoadAsync();
        var dialogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "rules-export.json");
        var json = JsonSerializer.Serialize(settings.Rules, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(dialogPath, json);
    }

    [RelayCommand]
    public async Task ImportRulesAsync()
    {
        var dialogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "rules-export.json");
        if (!File.Exists(dialogPath)) return;
        var json = await File.ReadAllTextAsync(dialogPath);
        var imported = JsonSerializer.Deserialize<List<ProxyRule>>(json) ?? new();
        Rules.Clear();
        foreach (var r in imported) Rules.Add(r);
        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteRuleAsync()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        await SaveAsync();
    }

    private bool CanDelete() => SelectedRule is not null;

    private async Task SaveAsync()
    {
        var settings = await _configService.LoadAsync();
        settings.Rules.Clear();
        foreach (var r in Rules)
        {
            settings.Rules.Add(r);
        }
        await _configService.SaveAsync(settings);
    }
}
