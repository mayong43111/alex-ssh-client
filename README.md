# SSH Client

一个独立实现的 Windows 桌面 SSH 客户端（.NET 8 + WPF）。

本项目聚焦于“通过 SSH 建立上游链路，并在本机提供 HTTP/SOCKS 代理入口 + 规则路由能力”。

## 主要能力
- WPF 客户端 + MVVM（CommunityToolkit.Mvvm）
- Profile 管理（保存、另存为、读取）
- 登录后启动本地代理监听（单端口 Mixed）：
  - HTTP: `127.0.0.1:1080`
  - SOCKS5: `127.0.0.1:1080`
- 规则路由（Profile 级规则，支持 DomainSuffix/IpCidr/All）
- 规则编辑弹窗与列表双击编辑（默认项不允许在弹窗中编辑，且不可删除）
- 托盘最小化行为（首次询问 + 持久化偏好）
- 结构化日志（Serilog，UI 实时日志 + 文件日志）
- 测试与 CI（xUnit + GitHub Actions）

## 运行环境
- Windows 10/11
- .NET SDK 8.0+

## 快速开始
```bash
dotnet restore
dotnet build
dotnet run --project src/SSHClient.App
dotnet test
```

诊断运行（建议排查时使用）：
```bash
dotnet run --project src/SSHClient.App -- --diag
```

## 配置说明
配置文件：`src/SSHClient.App/appsettings.json`

当前结构要点：
- 规则是 **Profile 内部字段**（`Profiles[i].Rules`）
- 应用状态会持久化：
  - `ActiveProfileName`
  - `ActiveProfileFilePath`
  - `MinimizeToTray`

示例：
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
    "Profiles": [
      {
        "Name": "SampleSSH",
        "Host": "your-ssh-host.example.com",
        "Port": 22,
        "Username": "alice",
        "Password": null,
        "PrivateKeyPath": "C:/Users/alice/.ssh/id_rsa",
        "PrivateKeyPassphrase": null,
        "AuthMethod": "PrivateKey",
        "LocalListenAddress": "127.0.0.1",
        "LocalSocksPort": 1080,
        "JumpHosts": [],
        "StrictHostKeyChecking": true,
        "Rules": [
          {
            "Name": "默认",
            "Priority": 9999,
            "Pattern": "*",
            "Type": "All",
            "Action": "Direct"
          }
        ]
      }
    ],
    "ActiveProfileName": "SampleSSH",
    "ActiveProfileFilePath": null,
    "MinimizeToTray": null
  }
}
```

## 代理使用说明
- HTTP 代理地址：`http://127.0.0.1:1080`
- SOCKS5 代理地址：`socks5://127.0.0.1:1080`

说明：
- 当前版本使用单端口 Mixed 模式，HTTP 与 SOCKS5 共用 `SocksPort`（默认 1080）。
- `HttpPort` 配置项暂保留用于兼容旧配置，不再用于单独监听。

命令行示例：
```bash
curl -x http://127.0.0.1:1080 https://example.com
curl --socks5 127.0.0.1:1080 https://example.com
```

Git 通过本地 SOCKS5 代理：
```bash
git config --local http.proxy socks5h://127.0.0.1:1080
git config --local https.proxy socks5h://127.0.0.1:1080
```

## 规则行为说明
- 域名规则支持多条输入（`;` 或换行分隔）
- 通配规则 `*.example.com` 可匹配 `example.com` 和其子域
- 运行时总会追加最终兜底规则 `All/* -> Direct`，避免无命中时隐式走代理

## 项目结构
```text
SSHClient.sln
├─ src
│  ├─ SSHClient.App
│  └─ SSHClient.Core
└─ tests
   └─ SSHClient.Tests
```

## 文档
- 架构文档：`docs/ARCHITECTURE.md`
- 重构记录：`docs/REFACTOR_PLAN.md`

## 安全建议
- 优先使用公钥认证
- 避免提交真实凭据到仓库

## 许可证
MIT，见 `LICENSE`。
