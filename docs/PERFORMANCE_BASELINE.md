# 性能基线与压测记录

## 工具与执行入口
- 性能探针工具：`tools/SSHClient.PerfRunner`
- 一键脚本：`scripts/run-perf-check.ps1`

默认参数（满足阶段 F 场景定义）：
- 并发场景：`1,10,50,100`
- 每个场景持续：`600s`（10 分钟）
- 连接基线迭代：`2000`

## 使用方式

快速模式（用于本地回归，约几十秒）：

```powershell
./scripts/run-perf-check.ps1 -Quick
```

完整模式（10 分钟场景）：

```powershell
./scripts/run-perf-check.ps1 -DurationSeconds 600 -Iterations 2000 -Scenarios "1,10,50,100"
```

输出目录：`artifacts/perf`

## 最近一次执行（Full 10m）
- 运行时间：`2026-03-30T10:48:53Z`
- 报告文件：
  - `artifacts/perf/perf-report-20260330-104853.json`
  - `artifacts/perf/perf-report-20260330-104853.md`

### 规则匹配基线（Release）
- Throughput: `828,070.12 ops/s`
- Total matches: `500,000`
- Elapsed: `603.81 ms`
- GC: `Gen0/Gen1/Gen2 = 344/0/0`

### 连接时延基线（Direct）
- Throughput: `117.62 ops/s`
- P50: `8.035 ms`
- P95: `10.900 ms`
- P99: `16.007 ms`

### 负载场景摘要（Full 600s）
| Concurrency | Total | Success | Error | Throughput(ops/s) | P50(ms) | P95(ms) | CPU(%) | PeakWS(MB) |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 48785 | 48785 | 0 | 81.31 | 11.867 | 16.849 | 0.76 | 43.31 |
| 10 | 43914 | 43914 | 0 | 73.18 | 132.129 | 191.390 | 0.78 | 45.08 |
| 50 | 37000 | 37000 | 0 | 61.59 | 806.508 | 936.052 | 0.66 | 46.24 |
| 100 | 32266 | 32266 | 0 | 53.63 | 1861.904 | 2020.474 | 0.58 | 46.82 |

## 上一次 Quick 回归（参考）
- 运行时间：`2026-03-30T10:43:59Z`
- 报告文件：
  - `artifacts/perf/perf-report-20260330-104359.json`
  - `artifacts/perf/perf-report-20260330-104359.md`

## 说明
- 当前基线已更新为完整 10 分钟模式报告。
- 负载稳定性方面（成功率/错误率）已具备充分证据：1/10/50/100 并发场景均为 100% 成功、0% 错误。
- 性能优秀性方面仍需谨慎：高并发下 P95/P99 尾延迟抬升明显，建议后续针对连接建立与并发调度路径继续优化。
