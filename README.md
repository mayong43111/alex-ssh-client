# SSH Client (Shadowrocket for Windows-inspired)

A minimal .NET 8 WPF desktop client scaffold inspired by **Shadowrocket for Windows** but focused on **SSH-based SOCKS/HTTP proxying**. Includes MVVM structure, DI, Serilog logging, configuration via `appsettings.json`, and stub SSH tunneling logic using `Renci.SshNet`.

## ✨ Features (MVP scaffolding)
- WPF UI shell with tabs: **Dashboard**, **Profiles**, **Rules**, **Connections**.
- MVVM via `CommunityToolkit.Mvvm` (source generators for observables & commands).
- Configuration service (`FileConfigService`) loading/saving `appsettings.json`.
- Stub SSH tunnel service (`SshTunnelService`) using `Renci.SshNet` with dynamic (SOCKS) forwarding.
- Proxy manager orchestrating multiple tunnels.
- Logging via **Serilog** (console + rolling file).
- xUnit tests with **FluentAssertions**.
- GitHub Actions CI workflow (restore/build/test).

## 🛠️ Prerequisites
- .NET SDK 8.0+
- Windows 10/11 (WPF); core library is cross-platform.

## 🚀 Build & Run
```bash
# Restore & build
 dotnet restore
 dotnet build

# Run WPF app
 dotnet run --project src/SSHClient.App

# Run tests
 dotnet test
```

> 🧪 **Verification-before-completion:** Always run `dotnet test` and check exit codes before claiming success.

## 🧩 Configuration
`src/SSHClient.App/appsettings.json` sample:
```json
{
  "SSHClient": {
    "Logging": {
      "MinimumLevel": "Information",
      "LogPath": "logs/sshclient-.log"
    },
    "Profiles": [
      {
        "Name": "SampleSSH",
        "Host": "your-ssh-host.example.com",
        "Port": 22,
        "Username": "alice",
        "PrivateKeyPath": "C:/Users/alice/.ssh/id_rsa",
        "AuthMethod": "PrivateKey",
        "LocalSocksPort": 1080,
        "StrictHostKeyChecking": true
      }
    ],
    "Rules": [
      {
        "Name": "Default",
        "Pattern": "*",
        "Action": "Proxy",
        "Profile": "SampleSSH"
      }
    ]
  }
}
```

### Notes
- **真实 SSH 转发**：默认使用 stub（无依赖）。要启用 SSH.NET：
  1) `dotnet add src/SSHClient.Core package Renci.SshNet`
  2) 在 `Directory.Build.props` 中把 `<DefineConstants>SSHNET_STUB</DefineConstants>` 改为 `<DefineConstants>SSHNET;HAS_RENCI</DefineConstants>`
  3) `dotnet build`
  - 启用后：`SshTunnelService` 会启动 `ForwardedPortDynamic`（本地 SOCKS）并可创建针对目标的 `ForwardedPortLocal`；`SshProxyConnector` 会经 SSH 连接目标。
- 想要 **特定软件使用代理**（非全局）：
  - HTTP 代理：`HTTP_PROXY=http://127.0.0.1:8888`，`HTTPS_PROXY=http://127.0.0.1:8888`
  - SOCKS5：`ALL_PROXY=socks5://127.0.0.1:1080`
  - 示例：`curl -x http://127.0.0.1:8888 https://example.com` 或 `curl --socks5 127.0.0.1:1080 https://example.com`
  - 浏览器/IDE/包管理器通常有独立代理设置，可填写上面的地址；无需启用系统全局代理。
- `ProxyManager` 使用 `Func<ISshTunnelService>` 工厂 + `IProxyConnector`，可替换为 `gost`、`sing-box` 等其他引擎。

## 📁 Project Structure
```
SSHClient.sln
├─ src
│  ├─ SSHClient.App          # WPF app
│  └─ SSHClient.Core         # Core services & models
└─ tests
   └─ SSHClient.Tests        # xUnit tests
```

## 🔄 Next Steps / Ideas
- Add host key verification UI and known_hosts management.
- Implement rule engine (domain/CIDR/process match) and transparent proxy helpers.
- Add secret storage (Windows Credential Manager) for passwords/passphrases.
- Introduce background service for auto-reconnect, health checks.
- Add UI for adding/editing profiles, import/export, and quick connect.
- Optional: switch to Avalonia for cross-platform GUI.

## 📦 CI/CD
See `.github/workflows/ci.yml` — runs restore, build, test on Windows.

## 🛡️ Security
- Prefer **public key authentication**.
- Avoid committing real `appsettings.json` secrets; use `appsettings.Development.json` locally and user secrets.

## 🧾 License
TBD — choose MIT/Apache-2.0 as needed.
