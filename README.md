# KeyMouseCounter —— 键鼠按键统计

Windows 10/11 桌面小工具：**只统计次数，不记录任何内容**。

- 全局低层钩子统计键盘按键、鼠标左右中键点击、滚轮格数；
- 按「天 / 小时 / 分钟」落库（SQLite），同时写按天 JSONL 增量日志（日志为权威数据源）；
- 桌面挂件实时显示今日数字，主窗口提供曲线 / 热力图 / 表格三视图；
- 数据全在本机 `%LOCALAPPDATA%\KeyMouseCounter\`，**不联网、不写任何按键内容或窗口标题**。

版本 **2.3.0**（与 `docs/DESIGN.md`、`schema_meta.app_version` 一致）。

---

## 1. 三分钟跑起来

前置：Windows 10/11 x64 + **.NET 8 SDK**（本项目用 SDK **8.0.426** 验证；只有运行时不够）。

```powershell
# 0) 确认 SDK（若 command not found，见下方"没有 SDK 时"）
dotnet --version          # 期望 8.0.x

# 1) 构建 + 跑单元测试（应 31/31 全绿）
dotnet build -c Release
dotnet test  -c Release

# 2) 发布单文件 exe 到 dist\（含 3 个辅助脚本与 README）
.\scripts\publish.ps1

# 3) 运行
.\dist\KeyMouseCounter.exe
```

发布产物：`dist\KeyMouseCounter.exe`，**单文件 self-contained**，约 85–110 MB，目标机无需装 .NET。
首次运行会在托盘出现图标，并弹出桌面挂件；主窗口默认最小化启动（设置 → 常规 可改）。

**没有 SDK 时**（本机 `C:\Program Files\dotnet` 可能只有运行时）：

```powershell
# 用官方脚本装到自定义目录，避免动系统目录（需联网一次）
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $env:TEMP\dotnet-install.ps1
powershell -ExecutionPolicy Bypass -File $env:TEMP\dotnet-install.ps1 -Channel 8.0 -InstallDir D:\dotnet8
$env:DOTNET_ROOT = 'D:\dotnet8'; $env:PATH = "D:\dotnet8;$env:PATH"
# 之后 dotnet 命令即可用；scripts\publish.ps1 会自动探测 DOTNET_ROOT 与 D:\dotnet8
```

> 本项目验证环境：Windows 10/11 x64，SDK **8.0.426**（装于 `D:\dotnet8`），xUnit 31 项单测全绿。

---

## 2. 数据放在哪

| 内容 | 路径 |
|---|---|
| 数据根目录 | `%LOCALAPPDATA%\KeyMouseCounter\` |
| 配置 | `%LOCALAPPDATA%\KeyMouseCounter\config.json`（损坏时自动备份为 `config.bad.json` 并回落默认值） |
| 数据库 | `%LOCALAPPDATA%\KeyMouseCounter\data.db`（SQLite，WAL） |
| 按天日志（权威源） | `%LOCALAPPDATA%\KeyMouseCounter\data\YYYY-MM-DD.jsonl`（UTF-8 无 BOM，追加写，全部是**增量行**） |
| 汇总快照（仅参考） | `%LOCALAPPDATA%\KeyMouseCounter\data\YYYY-MM-DD.summary.json`（不参与计算） |
| 运行日志 | `%LOCALAPPDATA%\KeyMouseCounter\logs\app-*.log`（滚动，单个 ≤2MB，保留 3 个；**不含任何输入内容**） |
| CSV 导出 | `%LOCALAPPDATA%\KeyMouseCounter\exports\`（默认目录，导出时可另选） |

### 清空数据（三档，全部要求二次确认）

| 入口 | 作用 |
|---|---|
| 主窗口「导出」旁的设置 → **数据 → 清空今日** | 只清今天（内存 + 库 + 今日 jsonl） |
| 设置 → 数据 → **清空区间** | 按起止日期逐日清 |
| 设置 → 数据 → **清空全部** | 停钩子 → 刷盘 → 删表 → 删全部 jsonl → 重启钩子；需逐字输入 `DELETE` |
| 托盘 → 清空今日数据 | 同上一档的今日 |
| 命令行 `KeyMouseCounter.exe --purge` | 注销自启 + 删除整个 `%LOCALAPPDATA%\KeyMouseCounter\` 后退出 |
| 命令行 `KeyMouseCounter.exe --reset-data` | 只删统计（`data\` + `data.db`），保留配置 |

设置 → 数据 还有 **从日志重建数据库**：删掉 `data.db` 后启动会自动全量重建，也可手工触发对账。

---

## 3. 日常操作

- **挂件**：拖动移动（位置记忆）；双击打开主窗口；右键菜单（打开主界面 / 暂停 / 隐藏 / 穿透 / 透明度 / 设置 / 退出）；`Ctrl+滚轮` 调透明度 0.4–1.0；暂停态变灰并在右上角显示 `Ⅱ`。
- **全局热键**：默认 `Ctrl+Alt+K` 显示/隐藏主窗口（设置 → 挂件 可改，带占用检测）。
- **主窗口**：三个标签「今日·按小时 / 今日·按分钟 / 多日·按天」；三视图「曲线 / 热力图 / 表格」；指标「键盘按键 / 鼠标点击 / 活跃时长」；多日支持 1/7/14/30/90/自定义（最多 366 天）。
- **刷新**：打开窗口、切标签、点刷新、按 `F5`/`Ctrl+R` 都会先 `FlushNow()` 再查库；今日视图每 5 秒自动刷新。
- **退出**：托盘菜单 → 退出（关闭主窗口只是隐藏到托盘）。
- **开机自启**：设置 → 常规 开关，或 `.\scripts\install-autostart.ps1`（计划任务 `KeyMouseCounter`）；取消用 `.\scripts\uninstall-autostart.ps1` 或 `KeyMouseCounter.exe --unregister-autostart`。
  **注册/注销计划任务需要管理员权限**：`install-autostart.ps1` 已声明 `#Requires -RunAsAdministrator`，
  用 exe 命令行时请先「以管理员身份运行」。非提权调用会直接给出提示，不会静默失败。

---

## 4. 计数口径（不可擅自改）

| 项 | 规则 |
|---|---|
| 键盘按键 | 每次**物理按下**计 1；长按自动重复默认**不计**（`countAutoRepeat:false`） |
| 修饰键 | Shift/Ctrl/Alt/Win 默认**计入**（`countModifierKeys:true`） |
| 抬起 | `keyup` 不计 |
| 鼠标点击 | 左/右/中每次 DOWN 计 1；双击=2；拖拽=1 |
| 滚轮 | 每 120 delta 计 1 格，不足 120 的余量跨事件累积不丢弃；横滚单独计 |
| 光标移动 | **完全不统计**（v2.1 起） |
| 注入/合成输入 | 默认**不计**（`ignoreInjected:true`，防 IME 合成键翻倍） |
| 活跃时长 | 相邻两次被统计输入的间隔 ≤ `idleThresholdSec`（默认 60s）才累加该间隔，归属到间隔**结束时刻所在分钟**；光标移动不算 |
| 首次/最后输入 | 当天最早/最晚**有计数的分钟**，格式 `HH:mm` |
| 自身窗口 | 默认排除（`excludeSelf:true`） |

口径可在「设置 → 计数口径」即时修改，**只影响之后的数据**（历史不重算）。

命令行 `--count-injected` 可临时统计注入输入，**仅用于自动化验证**，不会写回配置。

---

## 5. 技术栈与降级说明

- .NET 8 + WinForms（x64，`asInvoker` 最小权限）；
- 输入捕获：`WH_KEYBOARD_LL` + `WH_MOUSE_LL` 低层钩子（专用线程，回调只做内存自增）；
- 存储：`Microsoft.Data.Sqlite` 8.x（WAL）；
- 图表：**ScottPlot.WinForms 5.0.56 —— 可用，未走降级方案**；
  发布时必须 `IncludeNativeLibrariesForSelfExtract=true`（ScottPlot 带原生依赖，否则 `DllNotFound`）；
  WinForms 不支持 Trim / NativeAOT，勿开。
- 若某环境无法还原 ScottPlot，代码里保留了 `IChartView { Control Host; void Render(ChartData); }` 抽象，
  按文档 §7.5 可用 GDI+ 自绘替换而不动主窗口逻辑（当前未内置 GDI+ 实现）。

---

## 6. 隐私红线

1. **只计数，不记内容**：钩子回调里的 `vkCode` 仅用于自动重复判断，绝不落盘；
   日志/数据库字段是白名单（日期、分钟、计数、活跃秒数、用户 SID、主机名、时区偏移）；
2. **不联网**：项目不引用任何 HTTP 客户端，无遥测；
3. **数据可删**：三档清空 + `--purge`；
4. `data\*.jsonl` 全文检索不会出现按键内容、输入文本、窗口标题。

---

## 7. 已知限制（文档 §13）

- UAC 安全桌面、锁屏、Ctrl+Alt+Del 期间**无法统计**（系统限制），该时段为 0；
- **开机自启（计划任务）的注册/注销需要管理员权限**（`Register-ScheduledTask` 在普通用户令牌下返回
  `0x80070005 拒绝访问`）；程序会在未提权时给出明确提示，功能本身不影响统计；
  任务注册完成后以 `RunLevel=Limited` 的普通权限运行，不需要常驻提权；
- 杀软可能误报/拦截钩子；提权程序前台时可能漏计（需要时把计划任务改成 `Highest`）；
- 钩子可能被系统静默摘除 → 程序每 60s 自愈检测，系统有输入而计数为 0 时自动重装；
- 分钟归属受 200ms 时钟缓存影响，分钟边界附近极少数事件可能归到相邻分钟（≤200ms 误差）；
- 日志被手工删除时重建会丢该日数据（数据库仍完整时不受影响）；日志为权威源，`log_gap` 标记的日期重建时跳过。

---

## 8. 目录结构

```
KeyMouseCounter.sln
键鼠按键统计-开发文档.md          # 需求与契约（v2.3）
README.md
scripts/
  publish.ps1                     # 构建 + 测试 + 单文件发布
  install-autostart.ps1           # 注册计划任务（开机自启）
  uninstall-autostart.ps1         # 注销计划任务（可选 -PurgeData）
src/KeyMouseCounter.App/          # 主程序（WinForms，单文件）
  Core/       计数引擎、分钟桶、时钟、统计模型
  Input/      低层钩子输入源
  Native/     P/Invoke、热键窗口
  Storage/    SQLite、JSONL 日志、重建、清空
  Services/   刷盘、保留期、查询、自启动、单实例
  Charts/     曲线 / 热力图 / 表格（ScottPlot）
  UI/         主窗口、挂件、设置、托盘、对话框
src/KeyMouseCounter.Tests/        # xUnit 单测（T01–T26，当前 31 个 Fact 全绿）
docs/DESIGN.md                    # 设计说明（v2.3）
docs/CONTRACTS.md                 # 模块接口契约
```

---

## 9. 开发与验证

```powershell
dotnet build -c Release
dotnet test  -c Release            # 31 passed
.\scripts\publish.ps1              # 出 dist\KeyMouseCounter.exe
Start-Process .\dist\KeyMouseCounter.exe

# 看运行日志
Get-Content "$env:LOCALAPPDATA\KeyMouseCounter\logs\app-*.log" -Tail 30
# 看当天日志行数（应只有计数，无按键内容）
(Get-Content "$env:LOCALAPPDATA\KeyMouseCounter\data\$(Get-Date -f yyyy-MM-dd).jsonl").Count
# 资源占用
Get-Process KeyMouseCounter | Select-Object CPU,@{n='MB';e={[int]($_.WorkingSet64/1MB)}}
```

> 钩子只捕获真实输入。自动化验证请用 `KeyMouseCounter.exe --count-injected`，
> 或直接对 `CounterEngine` 做单测（推荐，`dotnet test` 不依赖真实钩子）。
