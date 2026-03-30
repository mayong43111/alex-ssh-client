# SSH Client 重构计划

## 目标
- 让代码职责边界更清晰，降低单文件复杂度。
- 减少重复实现，避免同类逻辑多处修改导致回归。
- 在不改变现有用户行为的前提下，逐步改进结构。

## 重构原则
- 先补测试保护网，再拆分代码。
- 每次提交保持可编译、可运行、可回退。
- 优先抽取高重复、高耦合的公共逻辑。

## 分阶段计划

### 阶段 0：基线与保护网
- [x] 补充 RuleEngine 行为测试（多域名、通配、CIDR、回退规则）。
- [x] 补充配置持久化测试（ActiveProfile 与 MinimizeToTray）。
- [x] 清理无效测试样例（UnitTest1 空测试）。

### 阶段 1：ViewModel 拆分
- [x] 从 ProfilesViewModel 抽离规则归一化服务。
- [x] 从 ProfilesViewModel 抽离配置文件导入导出服务。
- [x] 从 ProfilesViewModel 抽离最小化偏好持久化服务。

### 阶段 2：代理链路去重
- [x] 提取 HTTP/SOCKS 共用的上游路由与连接逻辑。
- [x] 统一“规则命中/路由/上游连接”日志模板。
- [x] 减少热路径中的重复配置加载。

### 阶段 3：UI 层收敛
- [x] 托盘行为从 MainWindow 迁移到独立服务。
- [x] 文件对话框调用从 code-behind 迁移到可测试抽象。
- [x] MainWindow.xaml.cs 仅保留窗口级事件桥接。

### 阶段 4：文档与启动流程对齐
- [x] 修订 ARCHITECTURE 文档与当前实现一致。
- [x] 明确后台服务启动/停止时序，减少隐式逻辑。

## 当前执行记录
- 2026-03-30：创建重构计划文档并开始执行。
- 2026-03-30：完成阶段 2 第 1 项，新增 `UpstreamRouteConnector` 并接入 HTTP/SOCKS 代理。
- 2026-03-30：新增 `UpstreamRouteConnectorTests` 两个用例，测试总数从 7 提升到 9。
- 2026-03-30：补充配置状态字段持久化测试，并删除空壳测试 `UnitTest1`。
- 2026-03-30：完成阶段 2 第 3 项，`ProxyManager` 引入配置缓存加载，代理热路径不再每请求重复 Reload。
- 2026-03-30：完成阶段 0，新增 `RuleEngineTests` 并补齐多域名/通配/CIDR/无匹配等行为用例。
- 2026-03-30：完成阶段 1 第 3 项，新增 `MinimizePreferenceService` 并从 `ProfilesViewModel` 移除最小化偏好持久化逻辑。
- 2026-03-30：完成阶段 1 第 2 项，新增 `ProfileFileService` 并从 `ProfilesViewModel` 移除配置文件导入导出细节。
- 2026-03-30：完成阶段 1 第 1 项，新增 `RuleNormalizationService` 并从 `ProfilesViewModel` 移除规则归一化算法。
- 2026-03-30：完成阶段 3 前两项，新增 `TrayBehaviorService` 与 `ProfileFileDialogService`，MainWindow 改为委托服务实现。
- 2026-03-30：完成阶段 3 第 3 项，新增 `MainWindowActionService`，MainWindow 仅保留窗口事件桥接与最小状态更新。
- 2026-03-30：完成阶段 4 第 1 项，`ARCHITECTURE.md` 对齐当前实现（启动时序、配置字段、测试基线、服务拆分）。
- 2026-03-30：完成阶段 4 第 2 项，移除 `AppRuntime.StartBackgroundServicesAsync` 未使用入口，后台服务时序收敛为“登录后启动、退出时停止”。
