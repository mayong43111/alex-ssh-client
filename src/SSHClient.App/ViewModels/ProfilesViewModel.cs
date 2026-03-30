using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SSHClient.Core.Models;
using SSHClient.Core.Services;

namespace SSHClient.App.ViewModels;

using System.Collections.Generic;

public partial class ProfilesViewModel : ObservableObject
{
    private const string DefaultRuleName = "默认";
    private const int DefaultRulePriority = 9999;
    private const string DefaultProfileName = "Default";
    private const string DefaultProfileFileName = "default.profile.json";

    private static readonly JsonSerializerOptions ProfileFileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IProxyManager _proxyManager;
    private readonly IConfigService _configService;
    private readonly SSHClient.App.Services.ProxyHost _proxyHost;
    private string? _activeProfileFilePath;
    private bool? _minimizeToTray;
    private bool _isMinimizeBehaviorLoaded;

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

    public ProfilesViewModel(IProxyManager proxyManager, IConfigService configService, SSHClient.App.Services.ProxyHost proxyHost)
    {
        _proxyManager = proxyManager;
        _configService = configService;
        _proxyHost = proxyHost;
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
        _minimizeToTray = settings.MinimizeToTray;
        _isMinimizeBehaviorLoaded = true;

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

        _activeProfileFilePath = NormalizePathOrNull(settings.ActiveProfileFilePath);
        if (!string.IsNullOrWhiteSpace(_activeProfileFilePath))
        {
            var loadedFromFile = await TryReadProfileFileAsync(_activeProfileFilePath);
            if (loadedFromFile is not null)
            {
                var normalizedLoaded = loadedFromFile with
                {
                    JumpHosts = (loadedFromFile.JumpHosts ?? new List<string>()).ToList(),
                    Rules = NormalizeRules(loadedFromFile.Rules ?? Array.Empty<ProxyRule>()),
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
                await WriteProfileToFileAsync(_activeProfileFilePath, SelectedProfile with
                {
                    Name = DefaultProfileName,
                    JumpHosts = (SelectedProfile.JumpHosts ?? new List<string>()).ToList(),
                    Rules = NormalizeRules(SelectedProfile.Rules ?? Array.Empty<ProxyRule>()),
                });
            }

            await SaveAsync();
        }
    }

    public async Task<bool?> GetMinimizeToTrayPreferenceAsync()
    {
        if (_isMinimizeBehaviorLoaded)
        {
            return _minimizeToTray;
        }

        var settings = await _configService.LoadAsync();
        _minimizeToTray = settings.MinimizeToTray;
        _isMinimizeBehaviorLoaded = true;
        return _minimizeToTray;
    }

    public async Task SetMinimizeToTrayPreferenceAsync(bool? minimizeToTray)
    {
        _minimizeToTray = minimizeToTray;
        _isMinimizeBehaviorLoaded = true;

        var settings = await _configService.LoadAsync();
        settings.MinimizeToTray = minimizeToTray;
        await _configService.SaveAsync(settings);
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

    public int GetSuggestedRulePriority() => NextRulePriority();

    public string GetDefaultProfileFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, DefaultProfileFileName);
    }

    public string GetDefaultProfileFileName() => DefaultProfileFileName;

    public string GetCurrentProfileExportDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_activeProfileFilePath))
        {
            var dir = Path.GetDirectoryName(_activeProfileFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    public string GetCurrentProfileExportFileName()
    {
        if (!string.IsNullOrWhiteSpace(_activeProfileFilePath))
        {
            var currentName = Path.GetFileName(_activeProfileFilePath);
            if (!string.IsNullOrWhiteSpace(currentName))
            {
                return currentName;
            }
        }

        var profileName = string.IsNullOrWhiteSpace(SelectedProfile?.Name) ? "profile" : SelectedProfile.Name;
        return $"{profileName}.profile.json";
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

        var fullPath = Path.GetFullPath(filePath);

        var snapshotRules = NormalizeRules(Rules)
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

        await WriteProfileToFileAsync(fullPath, profileToSave);
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

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return "文件不存在。";
        }

        var loaded = await TryReadProfileFileAsync(fullPath);

        if (loaded is null || string.IsNullOrWhiteSpace(loaded.Name))
        {
            return "文件内容无效，未找到配置名称。";
        }

        var normalizedRules = NormalizeRules(loaded.Rules ?? Array.Empty<ProxyRule>());
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

    public bool IsDefaultRuleItem(ProxyRule? rule) => IsDefaultRule(rule);

    public async Task AddRuleFromDialogAsync(ProxyRule rule)
    {
        var normalizedRule = new ProxyRule
        {
            Name = string.IsNullOrWhiteSpace(rule.Name) ? GetSuggestedRuleName() : rule.Name.Trim(),
            Priority = Math.Clamp(rule.Priority <= 0 ? NextRulePriority() : rule.Priority, 1, DefaultRulePriority - 1),
            Pattern = string.IsNullOrWhiteSpace(rule.Pattern) ? "*" : rule.Pattern.Trim(),
            Type = NormalizeRuleType(rule.Type, rule.Pattern),
            Action = rule.Action,
        };

        var defaultIndex = Rules.ToList().FindIndex(IsDefaultRule);
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
        if (IsDefaultRule(originalRule))
        {
            normalizedRule = CreateDefaultRule(editedRule.Action);
        }
        else
        {
            normalizedRule = new ProxyRule
            {
                Name = string.IsNullOrWhiteSpace(editedRule.Name) ? originalRule.Name : editedRule.Name.Trim(),
                Priority = Math.Clamp(editedRule.Priority <= 0 ? originalRule.Priority : editedRule.Priority, 1, DefaultRulePriority - 1),
                Pattern = string.IsNullOrWhiteSpace(editedRule.Pattern) ? "*" : editedRule.Pattern.Trim(),
                Type = NormalizeRuleType(editedRule.Type, editedRule.Pattern),
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

        if (IsDefaultRule(SelectedRule))
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

    private bool CanOperateOnRuleSelection() => SelectedRule is not null && !IsDefaultRule(SelectedRule);

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
            var normalizedRules = NormalizeRules(Rules);
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
        if (_isMinimizeBehaviorLoaded)
        {
            settings.MinimizeToTray = _minimizeToTray;
        }

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
            Rules = NormalizeRules(SelectedProfile.Rules ?? Array.Empty<ProxyRule>()),
        };

        await WriteProfileToFileAsync(_activeProfileFilePath, snapshot);
    }

    private static async Task WriteProfileToFileAsync(string filePath, ProxyProfile profile)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, profile, ProfileFileJsonOptions);
    }

    private static async Task<ProxyProfile?> TryReadProfileFileAsync(string filePath)
    {
        var fullPath = NormalizePathOrNull(filePath);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<ProxyProfile>(stream, ProfileFileJsonOptions);
    }

    private static string? NormalizePathOrNull(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return Path.GetFullPath(filePath);
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

    private static bool IsDefaultRule(ProxyRule? rule)
        => rule is not null && string.Equals(rule.Name, DefaultRuleName, StringComparison.Ordinal);

    private static ProxyRule CreateDefaultRule(RuleAction action)
        => new()
        {
            Name = DefaultRuleName,
            Priority = DefaultRulePriority,
            Pattern = "*",
            Type = "All",
            Action = action,
        };

    private static List<ProxyRule> NormalizeRules(IEnumerable<ProxyRule> rules)
    {
        var input = rules.ToList();
        var defaultAction = input.FirstOrDefault(IsDefaultRule)?.Action ?? RuleAction.Direct;

        var normalized = input
            .Where(r => !IsDefaultRule(r))
            .Select(r => new ProxyRule
            {
                Name = r.Name,
                Priority = Math.Clamp(r.Priority <= 0 ? 100 : r.Priority, 1, DefaultRulePriority - 1),
                Pattern = r.Pattern,
                Type = NormalizeRuleType(r.Type, r.Pattern),
                Action = r.Action,
            })
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        normalized.Add(CreateDefaultRule(defaultAction));
        return normalized;
    }

    private static string NormalizeRuleType(string? type, string? pattern)
    {
        if (string.Equals(type, "All", StringComparison.OrdinalIgnoreCase))
        {
            return "All";
        }

        if (string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase))
        {
            return "IpCidr";
        }

        if (!string.IsNullOrWhiteSpace(type) && string.Equals(type, "DomainSuffix", StringComparison.OrdinalIgnoreCase))
        {
            return "DomainSuffix";
        }

        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return "All";
        }

        if (!string.IsNullOrWhiteSpace(pattern) && pattern.Contains('/'))
        {
            return "IpCidr";
        }

        return "DomainSuffix";
    }

    private int NextRulePriority()
    {
        var max = Rules
            .Where(r => !IsDefaultRule(r))
            .Select(r => r.Priority)
            .DefaultIfEmpty(0)
            .Max();

        if (max <= 0)
        {
            return 10;
        }

        var stepped = ((max / 10) + 1) * 10;
        return Math.Min(stepped, DefaultRulePriority - 1);
    }

    private void LoadRulesForSelectedProfile(ProxyProfile? profile)
    {
        Rules.Clear();
        var normalizedRules = NormalizeRules(profile?.Rules ?? Array.Empty<ProxyRule>());
        foreach (var rule in normalizedRules)
        {
            Rules.Add(rule);
        }

        SelectedRule = Rules.FirstOrDefault();
    }
}
