# SSH Client

一个面向 Windows 的 SSH 桌面客户端，使用 .NET 8 与 WPF 实现。

本项目聚焦 SSH 连接管理、配置管理与桌面交互体验，适合本地开发与日常运维场景。

## 主要能力
- SSH 配置管理（新增、保存、读取、另存为）
- 登录 / 登出流程与状态反馈
- 支持密码与公钥两种认证方式
- 实时日志面板与诊断启动模式
- 托盘最小化行为与偏好持久化
- 基于 MVVM 的可维护结构（CommunityToolkit.Mvvm）
- 单元测试与持续集成（xUnit + GitHub Actions）

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

诊断启动：
```bash
dotnet run --project src/SSHClient.App -- --diag
```

## 配置文件
应用配置文件位于：src/SSHClient.App/appsettings.json

常用字段：
- SSHClient.Profiles：连接配置列表
- SSHClient.ActiveProfileName：当前配置名称
- SSHClient.ActiveProfileFilePath：当前配置来源文件路径
- SSHClient.MinimizeToTray：最小化行为偏好
- SSHClient.Logging：日志等级与日志文件路径

示例：
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
        "Password": null,
        "PrivateKeyPath": "C:/Users/alice/.ssh/id_rsa",
        "PrivateKeyPassphrase": null,
        "AuthMethod": "PrivateKey",
        "LocalListenAddress": "127.0.0.1",
        "LocalSocksPort": 1080,
        "JumpHosts": [],
        "StrictHostKeyChecking": true,
        "Rules": []
      }
    ],
    "ActiveProfileName": "SampleSSH",
    "ActiveProfileFilePath": null,
    "MinimizeToTray": null
  }
}
```

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
- docs/ARCHITECTURE.md：架构说明
- docs/REFACTOR_PLAN.md：重构记录

## 安全建议
- 优先使用公钥认证
- 不要将真实凭据提交到仓库

## 许可证
MIT，见 LICENSE。
