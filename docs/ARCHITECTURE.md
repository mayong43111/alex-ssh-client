# 🧭 SSH Client Architecture Overview

> 本文档描述当前代码状态下的整体设计、模块划分、依赖关系、启动流程、配置与日志策略，并给出扩展建议。适用于新成员上手与后续演进。

---

## 🏗️ 顶层结构

```
c:\repos\SSH Client
├─ src
│  ├─ SSHClient.Core        // 核心逻辑（配置、规则引擎、代理内核、SSH Tunnel 服务）
│  └─ SSHClient.App         // WPF 客户端（UI、DI、应用入口、系统代理控制）
└─ tests
   └─ SSHClient.Tests       // xUnit 测试，覆盖规则引擎、代理管理等
```

### 核心组件
- **SSHClient.Core**
  - `Configuration/`：`AppSettings` 等配置 POCO
  - `Services/`：`FileConfigService`, `ProxyManager`, `SshTunnelService`, `SshProxyConnector`, `ISshTunnelService`
  - `Proxy/`：`HttpProxyServer`, `SocksProxyServer`, `RuleEngine`, `RuleAction` 等
- **SSHClient.App**
  - `App.xaml` / `App.xaml.cs`：应用入口（薄壳），只负责启动流程编排与错误兜底
  - `Bootstrap/`：启动分层模块
    - `AppHostFactory`：Host 构建、DI 注册、Serilog 配置
    - `GlobalExceptionHooks`：全局异常事件挂钩
    - `AppRuntime`：主窗口显示、后台服务启停
  - `ViewModels/`：`MainViewModel`, `ProfilesViewModel`
  - `MainWindow.xaml`：紧凑登录界面（Login/About）+ 内嵌日志区
  - `MainWindow.xaml.cs`：日志框自动滚动到末尾（新日志持续向下滚动）
  - `Logging/`：`RollingUiLogService`, `UiLogSink`（内存滚动日志 + Serilog UI Sink）
  - `Services/ProxyHost`：在配置允许时启动/停止 HTTP+SOCKS 代理、系统代理切换
  - `StartupProbe`：诊断日志写入 `%TEMP%\sshclient-startup.log`

---

## 🚀 启动流程（WPF）
1. `App.OnStartup` 被触发
2. 解析命令行：支持 `--minimal`（纯烟测窗口）
3. 注册全局异常挂钩：`GlobalExceptionHooks.Register(this)`
4. 通过 `AppHostFactory.Build(args)` 构建 `Host`（`Host.CreateDefaultBuilder + appsettings.json`）
5. 注册服务（DI）：
  - Core：`IConfigService`, `ISshTunnelService`, `Func<ISshTunnelService>`, `IProxyConnector`, `IProxyManager`, `IRuleEngine`, `ISystemProxyService`
  - App：`ProxyHost`, `MainWindow`, `ProfilesViewModel`, `MainViewModel`, `IUiLogService`
6. `Host.Start()`
7. `AppRuntime.ShowMainWindow()` 显示主窗体
8. `AppRuntime.StartBackgroundServicesAsync()` 后台启动 `ProxyHost`
9. 退出时（`OnExit`）：
  - `_host.StopAsync(1s)`
  - `AppRuntime.StopBackgroundServicesAsync(2s)`
  - `_host.Dispose()`

> 🔁 `--minimal` 模式仍保留：用于快速确认 WPF 渲染/窗口显示是否正常。

---

## 🔩 核心服务与职责

### `FileConfigService` (`SSHClient.Core.Services`)
- 负责 `appsettings.json` 读写（路径默认 `AppContext.BaseDirectory`）
- JSON 序列化使用 `JsonSerializerDefaults.Web`

### `ProxyManager`
- 管理 SSH Profile 缓存与连接生命周期
- 关键接口：
  - `GetProfilesAsync()`：加载并缓存配置
  - `ConnectAsync(profileName)`：使用 `Func<ISshTunnelService>` 生成 Tunnel，调用 `ISshTunnelService.StartAsync`
  - `DisconnectAsync(profileName)`：停止并清理 Tunnel
- 注意：DI 中显式注册 `Func<ISshTunnelService>`（懒加载/独立生命周期）

### `SshTunnelService`
- 提供 SSH 隧道功能，支持 `SSHNET` 条件编译
- `#if SSHNET` 时使用 `Renci.SshNet` 提供的 `SshClient`, `ForwardedPortDynamic/Local`
- `#else` 提供 stub：便于离线/无 NuGet 时编译
- 实现 `ILocalForwardManager`（本地端口转发）

### `SshProxyConnector`
- 上层代理与规则引擎的连接器
- 在启用 SSHNET 时：确保 Tunnel up → 建立本地转发并连接到本地端口
- 在 stub 模式下回退到直接 `TcpClient.ConnectAsync`

### `RuleEngine`
- 根据目标 `host:port` 匹配 `Rule`，返回 `RuleAction`（Proxy/Direct/Block 等）以及指定 Profile 名称
- 被 HTTP/ SOCKS 代理调用

### `HttpProxyServer` / `SocksProxyServer`
- 基于 `TcpListener` 实现，支持取消（`CancellationTokenSource`）
- HTTP 支持 `CONNECT` 以及普通请求；根据 Rule 决定直连或通过 SSH Profile
- 停止逻辑安全（捕获 `OperationCanceledException` 和 socket 停止异常）

### `ProxyHost`（App 层）
- 聚合配置 → 启动 HTTP/SOCKS 代理 → 可选启用系统代理
- `StopAsync` 捕获异常，避免退出卡死

### `RollingUiLogService` + `UiLogSink`（App 层）
- Serilog 通过 `UiLogSink` 进入 UI 内存日志
- `RollingUiLogService` 负责滚动窗口（最大 1000 条）
- 超过 1000 条时删除最早日志，`MainViewModel.LiveLog` 订阅快照更新

### `ProfilesViewModel`（App 层）
- 负责 Profile 的加载、保存、刷新与连接
- 无 Profile 时自动创建默认 `Default`
- Login 页关键绑定字段：`Host`, `Port`, `LocalListenAddress`, `LocalSocksPort`, `Username`, `AuthMethod`, `Password`

### `SystemProxyService`
- 封装系统代理开关（Windows）
- 受 `appsettings.json` 中 `ToggleSystemProxy` 控制

---

## 🖥️ WPF MVVM
- **View**：`MainWindow.xaml`
  - `TabControl` 仅保留 `Login` / `About`
  - `Login` 内包含：Server/Auth 表单 + 日志显示区
  - 底部操作按钮：`Log in`（主按钮）+ `Save` / `Load` / `Exit`（次按钮）
- **ViewModel**：
  - `MainViewModel`：页面组合根 + `LiveLog` 同步
  - `ProfilesViewModel`：加载/新增/删除/连接 Profile，保存回 `appsettings.json`
- 使用 **CommunityToolkit.Mvvm**：`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`

---

## ⚙️ 配置与日志

### `appsettings.json`
```json
{
  "SSHClient": {
    "Logging": {
      "MinimumLevel": "Information",
      "LogPath": "logs/sshclient-.log"
    },
    "Proxy": {
      "HttpPort": 8888,
      "SocksPort": 1080,
      "EnableOnStartup": true,
      "ToggleSystemProxy": false
    },
    "Profiles": [ /* ProxyProfile 列表 */ ],
    "Rules": [ /* Rule 列表 */ ]
  }
}
```

### Serilog
- 来源：`AppHostFactory -> Host.UseSerilog`
- 输出：Console + 可选 rolling file（配置 `LogPath` 时启用）
- 同步输出：`UiLogSink -> RollingUiLogService`（UI 实时日志）
- `StartupProbe`：专用于启动阶段诊断 → `%TEMP%\sshclient-startup.log`

---

## ⏹️ 退出与资源释放
- `App.OnExit`：
   - `_host.StopAsync(1s)`
   - `AppRuntime.StopBackgroundServicesAsync(2s)`
   - `_host.Dispose()`
- 代理监听器 (`HttpProxyServer`, `SocksProxyServer`) 的停止方法捕获取消/Socket 异常，防止 hang

> ✅ 已验证：`dotnet run --project src/SSHClient.App --no-build -- --diag` 后关闭窗口，进程正常退出（避免之前的卡死）。

> 注：`--diag` 不是专用业务参数；当前主要用于开发阶段配合控制台输出进行诊断。

---

## 🧪 测试
- `dotnet test` 当前 7/7 通过（规则、配置、管理器等）
- 建议后续添加：
  - 规则匹配覆盖更多 pattern（wildcard、CIDR、端口范围）
  - 代理端到端集成测试（可用 TestContainers 或本地 loopback mock server）
  - SSHNET 启用下的连接测试（可用本地 sshd mock / openssh docker）

---

## 🔮 扩展路线图（建议）
- **SSH**：加入 `known_hosts` 校验、keyboard-interactive、多跳（jump hosts）
- **配置**：增加加密存储（密码/密钥 passphrase）
- **UI**：
  - Profile 测试按钮（Ping/SSH handshake）
  - 规则测试工具（输入域名/URL → 显示匹配结果 & 决策）
  - 托盘图标 + 快速开关系统代理
- **网络**：
  - HTTPS 解密（可选，需自签 CA，慎用）
  - UDP 转发 & DNS 代理
  - PAC 文件支持
- **平台**：为 WinService / CLI 模式提供入口（可共享同一 Core）

---

## 🧷 关键词索引（文件 → 功能）
- `src/SSHClient.App/App.xaml.cs` → 启动编排、`--minimal`、异常兜底
- `src/SSHClient.App/Bootstrap/AppHostFactory.cs` → Host 构建、DI、Serilog
- `src/SSHClient.App/Bootstrap/GlobalExceptionHooks.cs` → 全局异常挂钩
- `src/SSHClient.App/Bootstrap/AppRuntime.cs` → 主窗口显示、后台服务启停
- `src/SSHClient.App/MainWindow.xaml` → Login/About UI 与内嵌日志面板
- `src/SSHClient.App/MainWindow.xaml.cs` → 日志框自动滚动行为
- `src/SSHClient.App/Logging/*.cs` → UI 滚动日志管线
- `src/SSHClient.App/ViewModels/*.cs` → MVVM 逻辑（当前主用 Main/Profiles）
- `src/SSHClient.App/Services/ProxyHost.cs` → 聚合代理启动/停止
- `src/SSHClient.Core/Services/ProxyManager.cs` → Profile & Tunnel 管理
- `src/SSHClient.Core/Services/SshTunnelService.cs` → SSH 隧道（SSHNET/stub）
- `src/SSHClient.Core/Proxy/*.cs` → HTTP/SOCKS proxy 实现 & RuleEngine
- `src/SSHClient.Core/Services/FileConfigService.cs` → appsettings JSON 持久化

---

📌 若设计有更新（例如启用 SSHNET、添加多平台/后台服务支持），请同步此文档并在 README 中链接。
