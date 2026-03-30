using System.Collections.ObjectModel;
using System.IO;
using SSHClient.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

using System.Collections.Generic;

public partial class ProfilesViewModel : ObservableObject
{
    private const string DefaultProfileName = "Default";

    private readonly IProxyManager _proxyManager;
    private readonly IConfigService _configService;
    private readonly ProxyHost _proxyHost;
    private readonly IMinimizePreferenceService _minimizePreferenceService;
    private readonly IProfileFileService _profileFileService;
    private readonly IRuleNormalizationService _ruleNormalizationService;
    private string? _activeProfileFilePath;

    public ObservableCollection<ProxyProfile> Profiles { get; } = new();
    public ObservableCollection<ProxyRule> Rules { get; } = new();

    public IReadOnlyList<RuleAction> RuleActions { get; } = Enum.GetValues<RuleAction>();
    public IReadOnlyList<string> RuleTypes { get; } = new[]
    {
        "All",
        "DomainSuffix",
        "IpCidr",
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private ProxyProfile? _selectedProfile;

    partial void OnSelectedProfileChanged(ProxyProfile? value)
    {
        LoadRulesForSelectedProfile(value);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    private ProxyRule? _selectedRule;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    private bool _isLoggedIn;

    [ObservableProperty]
    private string? _connectedProfileName;

    public string ConnectButtonText => IsConnecting
        ? (IsLoggedIn ? "登出中..." : "登录中...")
        : (IsLoggedIn ? "登出" : "登录");

    public ProfilesViewModel(
        IProxyManager proxyManager,
        IConfigService configService,
        ProxyHost proxyHost,
        IMinimizePreferenceService minimizePreferenceService,
        IProfileFileService profileFileService,
        IRuleNormalizationService ruleNormalizationService)
    {
        _proxyManager = proxyManager;
        _configService = configService;
        _proxyHost = proxyHost;
        _minimizePreferenceService = minimizePreferenceService;
        _profileFileService = profileFileService;
        _ruleNormalizationService = ruleNormalizationService;
        _ = RefreshAsync();
    }

    private static ProxyProfile CreateDefaultProfile()
    {
        return new ProxyProfile
        {
            Name = "Default",
            Host = "127.0.0.1",
            Username = "user",
            Port = 22,
            LocalSocksPort = 1080,
            AuthMethod = SshAuthMethod.Password,
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var settings = await _configService.LoadAsync();

        var profiles = settings.Profiles.ToList();
        var createdDefault = false;
        if (profiles.Count == 0)
        {
            profiles.Add(CreateDefaultProfile());
            createdDefault = true;
        }

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        _activeProfileFilePath = _profileFileService.NormalizePathOrNull(settings.ActiveProfileFilePath);
        if (!string.IsNullOrWhiteSpace(_activeProfileFilePath))
        {
            var loadedFromFile = await _profileFileService.ReadProfileAsync(_activeProfileFilePath);
            if (loadedFromFile is not null)
            {
                var normalizedLoaded = loadedFromFile with
                {
                    JumpHosts = (loadedFromFile.JumpHosts ?? new List<string>()).ToList(),
                    Rules = _ruleNormalizationService.NormalizeRules(loadedFromFile.Rules ?? Array.Empty<ProxyRule>()),
                };

                UpsertProfile(normalizedLoaded);
                settings.ActiveProfileName = normalizedLoaded.Name;
            }
            else
            {
                _activeProfileFilePath = null;
            }
        }

        var preferredName = settings.ActiveProfileName;
        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();

        if (createdDefault)
        {
            _activeProfileFilePath = GetDefaultProfileFilePath();
            if (SelectedProfile is not null)
            {
                await _profileFileService.WriteProfileAsync(_activeProfileFilePath, SelectedProfile with
                {
                    Name = DefaultProfileName,
                    JumpHosts = (SelectedProfile.JumpHosts ?? new List<string>()).ToList(),
                    Rules = _ruleNormalizationService.NormalizeRules(SelectedProfile.Rules ?? Array.Empty<ProxyRule>()),
                });
            }

            await SaveAsync();
        }
    }

    public async Task<bool?> GetMinimizeToTrayPreferenceAsync()
    {
        return await _minimizePreferenceService.GetMinimizeToTrayPreferenceAsync();
    }

    public async Task SetMinimizeToTrayPreferenceAsync(bool? minimizeToTray)
    {
        await _minimizePreferenceService.SetMinimizeToTrayPreferenceAsync(minimizeToTray);
    }

    [RelayCommand]
    public async Task AddProfileAsync()
    {
        var newProfile = new ProxyProfile
        {
            Name = $"Profile-{Profiles.Count + 1}",
            Host = "host",
            Username = "user",
            Port = 22,
            LocalSocksPort = 1080,
            AuthMethod = SshAuthMethod.Password,
        };
        Profiles.Add(newProfile);
        await SaveAsync();
    }

    [RelayCommand]
    public async Task SaveProfileAsync()
    {
        await SaveAsync();
        await ApplyRulesImmediatelyIfLoggedInAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnSelection))]
    public async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null) return;
        Profiles.Remove(SelectedProfile);
        await SaveAsync();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    public async Task AddRuleAsync()
    {
        var rule = new ProxyRule
        {
            Name = GetSuggestedRuleName(),
            Priority = GetSuggestedRulePriority(),
            Pattern = "*",
            Type = "DomainSuffix",
            Action = RuleAction.Proxy,
        };

        await AddRuleFromDialogAsync(rule);
    }

    public string GetSuggestedRuleName() => $"规则-{Rules.Count + 1}";

    public int GetSuggestedRulePriority() => _ruleNormalizationService.GetNextRulePriority(Rules);

    public string GetDefaultProfileFilePath()
    {
        return _profileFileService.GetDefaultProfileFilePath();
    }

    public string GetDefaultProfileFileName() => Path.GetFileName(GetDefaultProfileFilePath());

    public string GetCurrentProfileExportDirectory()
    {
        return _profileFileService.GetCurrentExportDirectory(_activeProfileFilePath);
    }

    public string GetCurrentProfileExportFileName()
    {
        return _profileFileService.GetCurrentExportFileName(_activeProfileFilePath, SelectedProfile?.Name);
    }

    public async Task<string?> ExportSelectedProfileAsync(string? targetPath)
    {
        if (SelectedProfile is null)
        {
            return "当前没有可导出的配置。";
        }

        var filePath = (targetPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "请选择保存路径。";
        }

        var fullPath = _profileFileService.NormalizePathOrNull(filePath);

        var snapshotRules = _ruleNormalizationService.NormalizeRules(Rules)
            .Select(r => new ProxyRule
            {
                Name = r.Name,
                Priority = r.Priority,
                Pattern = r.Pattern,
                Type = r.Type,
                Action = r.Action,
            })
            .ToList();

        var snapshotJumpHosts = (SelectedProfile.JumpHosts ?? new List<string>()).ToList();
        var profileToSave = SelectedProfile with
        {
            JumpHosts = snapshotJumpHosts,
            Rules = snapshotRules,
        };

        await _profileFileService.WriteProfileAsync(fullPath, profileToSave);
        _activeProfileFilePath = fullPath;
        await SaveAsync();
        Log.Information("配置文件已导出：{Profile} -> {Path}", profileToSave.Name, fullPath);
        return null;
    }

    public async Task<string?> ImportProfileFromFileAsync(string? sourcePath)
    {
        var filePath = (sourcePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "请选择要加载的文件。";
        }

        var fullPath = _profileFileService.NormalizePathOrNull(filePath);
        var loaded = await _profileFileService.ReadProfileAsync(fullPath);

        if (loaded is null || string.IsNullOrWhiteSpace(loaded.Name))
        {
            return "文件内容无效，未找到配置名称。";
        }

        var normalizedRules = _ruleNormalizationService.NormalizeRules(loaded.Rules ?? Array.Empty<ProxyRule>());
        loaded = loaded with
        {
            JumpHosts = (loaded.JumpHosts ?? new List<string>()).ToList(),
            Rules = normalizedRules,
        };

        UpsertProfile(loaded);

        SelectedProfile = loaded;
        _activeProfileFilePath = fullPath;
        await SaveAsync();
        await ApplyRulesImmediatelyIfLoggedInAsync();
        Log.Information("配置文件已加载：{Profile} <- {Path}", loaded.Name, fullPath);
        return null;
    }

    public bool IsDefaultRuleItem(ProxyRule? rule) => _ruleNormalizationService.IsDefaultRule(rule);

    public async Task AddRuleFromDialogAsync(ProxyRule rule)
    {
        var normalizedRule = new ProxyRule
        {
            Name = string.IsNullOrWhiteSpace(rule.Name) ? GetSuggestedRuleName() : rule.Name.Trim(),
            Priority = Math.Clamp(rule.Priority <= 0 ? GetSuggestedRulePriority() : rule.Priority, 1, _ruleNormalizationService.DefaultRulePriority - 1),
            Pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? "*" : rule.Pattern.Trim(),
            Type = _ruleNormalizationService.NormalizeRuleType(rule.Type, rule.Pattern),
            Action = rule.Action,
        };

        var defaultIndex = Rules.ToList().FindIndex(_ruleNormalizationService.IsDefaultRule);
        if (defaultIndex >= 0)
        {
            Rules.Insert(defaultIndex, normalizedRule);
        }
        else
        {
            Rules.Add(normalizedRule);
        }

        SelectedRule = normalizedRule;
        await SaveAsync();
    }

    public async Task UpdateRuleFromDialogAsync(ProxyRule originalRule, ProxyRule editedRule)
    {
        var index = Rules.IndexOf(originalRule);
        if (index < 0)
        {
            return;
        }

        ProxyRule normalizedRule;
        if (_ruleNormalizationService.IsDefaultRule(originalRule))
        {
            normalizedRule = _ruleNormalizationService.CreateDefaultRule(editedRule.Action);
        }
        else
        {
            normalizedRule = new ProxyRule
            {
                Name = string.IsNullOrWhiteSpace(editedRule.Name) ? originalRule.Name : editedRule.Name.Trim(),
                Priority = Math.Clamp(editedRule.Priority <= 0 ? originalRule.Priority : editedRule.Priority, 1, _ruleNormalizationService.DefaultRulePriority - 1),
                Pattern = string.IsNullOrWhiteSpace(editedRule.Pattern) ? "*" : editedRule.Pattern.Trim(),
                Type = _ruleNormalizationService.NormalizeRuleType(editedRule.Type, editedRule.Pattern),
                Action = editedRule.Action,
            };
        }

        Rules[index] = normalizedRule;
        SelectedRule = normalizedRule;
        await SaveAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOperateOnRuleSelection))]
    public async Task DeleteRuleAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        if (_ruleNormalizationService.IsDefaultRule(SelectedRule))
        {
            Log.Warning("默认规则不可删除，仅可编辑动作");
            return;
        }

        Rules.Remove(SelectedRule);
        SelectedRule = Rules.FirstOrDefault();
        await SaveAsync();
    }

    [RelayCommand]
    public async Task SaveRulesAsync()
    {
        await SaveAsync();
        await ApplyRulesImmediatelyIfLoggedInAsync();
        Log.Information("规则已保存：配置 {Profile}，共 {Count} 条", SelectedProfile?.Name ?? "(无)", Rules.Count);
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        if (IsConnecting)
        {
            return;
        }

        if (IsLoggedIn)
        {
            await LogoutAsync();
            return;
        }

        if (SelectedProfile is null)
        {
            return;
        }

        try
        {
            if (!CanConnectWithCurrentAuth())
            {
                return;
            }

            Log.Information(
                "开始登录：配置 {Profile} -> {Host}:{Port}，用户名 {Username}",
                SelectedProfile.Name,
                SelectedProfile.Host,
                SelectedProfile.Port,
                SelectedProfile.Username);

            IsConnecting = true;
            await Task.Yield();

            Log.Information("正在执行登录：配置 {Profile}", SelectedProfile.Name);

            var connected = await _proxyManager.ConnectAsync(SelectedProfile);
            if (!connected)
            {
                Log.Warning("配置 {Profile} 连接失败", SelectedProfile.Name);
                IsLoggedIn = false;
                ConnectedProfileName = null;
                return;
            }

            try
            {
                await _proxyHost.StartAsync(forceStart: true, activeProfileName: SelectedProfile.Name);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "配置 {Profile} 代理监听启动失败，正在回滚 SSH 连接", SelectedProfile.Name);
                await _proxyManager.DisconnectAsync(SelectedProfile.Name);
                IsLoggedIn = false;
                ConnectedProfileName = null;
                return;
            }

            IsLoggedIn = true;
            ConnectedProfileName = SelectedProfile.Name;
            Log.Information("登录成功：配置 {Profile}，代理监听已开启", SelectedProfile.Name);
        }
        catch (Exception ex)
        {
            IsLoggedIn = false;
            ConnectedProfileName = null;
            Log.Error(ex, "配置 {Profile} 执行连接命令失败", SelectedProfile.Name);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task PrepareForAppExitAsync()
    {
        Log.Information("退出应用：准备停止代理并退出登录");

        if (IsConnecting)
        {
            Log.Information("退出应用：等待当前连接操作完成");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (IsConnecting && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }

        if (IsLoggedIn)
        {
            await LogoutAsync();
            return;
        }

        try
        {
            var profileName = ConnectedProfileName ?? SelectedProfile?.Name;
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                await _proxyManager.DisconnectAsync(profileName);
            }

            await _proxyHost.StopAsync();
            IsLoggedIn = false;
            ConnectedProfileName = null;
            Log.Information("退出应用：代理已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出应用清理失败");
        }
    }

    public string? JumpHostsDisplay => SelectedProfile is null ? null : string.Join(",", SelectedProfile.JumpHosts ?? new List<string>());


    private bool CanOperateOnSelection() => SelectedProfile is not null;

    private bool CanOperateOnRuleSelection() => SelectedRule is not null && !_ruleNormalizationService.IsDefaultRule(SelectedRule);

    private bool CanConnect()
    {
        if (IsConnecting)
        {
            return false;
        }

        return IsLoggedIn || SelectedProfile is not null;
    }

    private async Task SaveAsync()
    {
        if (Profiles.Count == 0)
        {
            Profiles.Add(CreateDefaultProfile());
        }

        if (SelectedProfile is not null)
        {
            var normalizedRules = _ruleNormalizationService.NormalizeRules(Rules);
            var selectedIndex = Profiles.IndexOf(SelectedProfile);
            if (selectedIndex >= 0)
            {
                var updated = SelectedProfile with { Rules = normalizedRules };
                Profiles[selectedIndex] = updated;
                SelectedProfile = updated;
            }
        }

        var settings = await _configService.LoadAsync();
        settings.Profiles.Clear();
        foreach (var p in Profiles)
        {
            settings.Profiles.Add(p);
        }

        settings.ActiveProfileName = SelectedProfile?.Name;
        settings.ActiveProfileFilePath = _activeProfileFilePath;

        await _configService.SaveAsync(settings);
        await PersistActiveProfileFileIfNeededAsync();

        if (SelectedProfile is null)
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }
    }

    private async Task PersistActiveProfileFileIfNeededAsync()
    {
        if (string.IsNullOrWhiteSpace(_activeProfileFilePath) || SelectedProfile is null)
        {
            return;
        }

        var snapshot = SelectedProfile with
        {
            JumpHosts = (SelectedProfile.JumpHosts ?? new List<string>()).ToList(),
            Rules = _ruleNormalizationService.NormalizeRules(SelectedProfile.Rules ?? Array.Empty<ProxyRule>()),
        };

        await _profileFileService.WriteProfileAsync(_activeProfileFilePath, snapshot);
    }

    private void UpsertProfile(ProxyProfile profile)
    {
        var existingIndex = Profiles
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => string.Equals(x.item.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
            ?.index;

        if (existingIndex is int index)
        {
            Profiles[index] = profile;
            return;
        }

        Profiles.Add(profile);
    }

    private async Task ApplyRulesImmediatelyIfLoggedInAsync()
    {
        if (!IsLoggedIn || IsConnecting)
        {
            return;
        }

        var activeProfileName = ConnectedProfileName ?? SelectedProfile?.Name;
        if (string.IsNullOrWhiteSpace(activeProfileName))
        {
            return;
        }

        try
        {
            await _proxyHost.StopAsync();
            await _proxyHost.StartAsync(forceStart: true, activeProfileName: activeProfileName);
            Log.Information("规则已立即生效：配置 {Profile}", activeProfileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "规则热更新失败：配置 {Profile}", activeProfileName);
        }
    }

    private bool CanConnectWithCurrentAuth()
    {
        if (SelectedProfile is null)
        {
            return false;
        }

        if (SelectedProfile.AuthMethod == SshAuthMethod.Password
            && string.IsNullOrWhiteSpace(SelectedProfile.Password))
        {
            Log.Warning("已取消连接：配置 {Profile} 使用密码认证但密码为空", SelectedProfile.Name);
            return false;
        }

        return true;
    }

    private async Task LogoutAsync()
    {
        var profileName = ConnectedProfileName ?? SelectedProfile?.Name;
        if (string.IsNullOrWhiteSpace(profileName))
        {
            IsLoggedIn = false;
            ConnectedProfileName = null;
            return;
        }

        IsConnecting = true;
        Log.Information("开始登出：配置 {Profile}", profileName);
        await Task.Yield();

        try
        {
            await _proxyManager.DisconnectAsync(profileName);
            await _proxyHost.StopAsync();
            IsLoggedIn = false;
            ConnectedProfileName = null;
            Log.Information("已登出：配置 {Profile}，代理监听已关闭", profileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "配置 {Profile} 执行登出失败", profileName);
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void LoadRulesForSelectedProfile(ProxyProfile? profile)
    {
        Rules.Clear();
        var normalizedRules = _ruleNormalizationService.NormalizeRules(profile?.Rules ?? Array.Empty<ProxyRule>());
        foreach (var rule in normalizedRules)
        {
            Rules.Add(rule);
        }

        SelectedRule = Rules.FirstOrDefault();
    }
}
