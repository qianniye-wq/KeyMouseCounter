# KeyMouseCounter 设计说明

- 版本：**v2.3**（与 `AppPaths.Version`、`schema_meta.app_version`、程序集版本一致）
- 对应需求：`键鼠按键统计-开发文档.md`（v2.3，仓库根目录）
- 目标平台：Windows 10/11 x64，.NET 8，WinForms，单文件 self-contained 发布

---

## 1. 目标与非目标

**做**：统计键盘按键次数、鼠标左右中键点击次数、滚轮格数、活跃时长；按分钟落桶，按天落库与落日志；桌面挂件实时显示；主窗口三视图（曲线/热力图/表格）；CSV 导出；三档清空；日志可 100% 重建数据库；开机自启。

**不做**：不记录任何按键内容、输入文本、窗口标题；不联网；不注入、不修改其他程序行为；不做多机同步。

---

## 2. 进程与线程模型

单进程、单实例（`Local\KeyMouseCounter.Mutex`），四类线程：

| 线程 | 职责 | 约束 |
|---|---|---|
| UI 线程（`KMC.UI`，**显式 8 MB 栈**的 STA 线程） | WinForms 消息循环，主窗口/挂件/设置/托盘 | 不做 I/O 密集操作；查询走 `Task.Run` 或短查询 |
| 钩子线程（`KMC.HookThread`，AboveNormal） | `WH_KEYBOARD_LL` + `WH_MOUSE_LL` 消息循环 | 回调内**只有**读结构体 → 过滤 → 一次极短锁 → `CallNextHookEx`；禁止 I/O / `DateTime.Now` / UI / 跨线程等待（§4.1 硬规则） |
| 刷盘线程（`System.Threading.Timer`） | 每 `flushIntervalSec`（默认 30s）取快照 → 日志 → 数据库 | 顺序不可颠倒（§6.5） |
| 看门狗线程（Timer） | 每 15s 检查刷盘是否卡住；每 60s 钩子自愈；每 6h 保留期清理 | 只做判断与重装，不做重活 |

**为什么 UI 线程要显式加大栈**：真机观察到偶发的 `0xc00000fd`（栈溢出），错误模块 `TextShaping.dll`——
SkiaSharp→DirectWrite 在字体回退路径上的调用链很深，默认 1 MB 的主线程栈余量不足时会直接杀进程，
托管层（`AppDomain.UnhandledException`）完全拦不到。把消息循环放到 8 MB 栈的 STA 线程上（`Program.UiThreadStackBytes`）
是成本最低的缓解；同时计划任务配置了 `RestartCount=3` 做兜底。

**为什么钩子单独线程**：低层钩子回调超时（默认 `LowLevelHooksTimeout`）会被系统**静默摘钩**——程序还在但不计数。UI 线程一卡（比如渲染 90 天热力图）就会触发，所以必须隔离。

**为什么用一条短锁 `_gate` 而不是无锁**：无锁交换数组会让"已经在写旧桶"的事件落到被丢弃的数组里（§5.3）。`_gate` 唯一持有者、永不跨 I/O、无竞争时约 20ns，是正确性与成本的最优解。

---

## 3. 模块划分

```
src/KeyMouseCounter.App/
├─ Program.cs            入口：命令行 → AppHost → Application.Run
├─ CommandLine.cs        --register-autostart / --unregister-autostart / --purge /
│                        --reset-data / --no-widget / --count-injected / --help
├─ AppPaths.cs           %LOCALAPPDATA%\KeyMouseCounter 下所有路径；Version="2.3.0"
├─ Theme.cs              深/浅色调色板 + 字体；Theme.Changed 通知 UI 重绘
├─ Core/
│  ├─ CounterEngine.cs   ★ 计数引擎：1440 分钟桶 + 自维护按键状态 + 活跃时长 + 跨天
│  ├─ MinuteBuckets.cs   9 个 long 计数器的值类型（K/ML/MR/MM/WU/WD/WL/WR/AS）
│  ├─ IClock.cs          可注入时钟（SystemClock / 测试 FakeClock），Stopwatch 单调基准
│  └─ StatsModels.cs     DayPoint/HourPoint/MinutePoint/HeatCell/HeatMatrix/Metric/IStatsQuery
├─ Input/
│  ├─ InputEvent.cs      InputKind/MouseButton/InputEvent/IInputSource
│  └─ LowLevelHookSource.cs  WH_KEYBOARD_LL + WH_MOUSE_LL 实现，委托存字段防 GC
├─ Native/
│  ├─ NativeMethods.cs   全部 P/Invoke（§15.1），含 GetWindowLong/SetWindowLong
│  ├─ Structs.cs         KBDLLHOOKSTRUCT/MSLLHOOKSTRUCT/LASTINPUTINFO/RECT/MSG/POINT
│  ├─ HotkeyParser.cs    "Ctrl+Alt+K" → (modifiers, vk)
│  └─ HotKeyWindow.cs    message-only 窗口 + RegisterHotKey + 占用探测
├─ Storage/
│  ├─ SqliteStore.cs     ★ 唯一 DB 访问点（WAL、事务、upsert、查询、清空、log_cursor）
│  ├─ MinuteLine.cs      JSONL 行模型 + System.Text.Json 源生成上下文
│  ├─ DailyLogWriter.cs  追加写 + header 行 + 失败回滚 + summary 快照
│  ├─ DailyLogReader.cs  整文件/增量解析、截断尾部修复、日期集合
│  ├─ LogDbReconciler.cs ★ 启动对账/重建（日志为权威源）
│  └─ DataPurger.cs      ★ 三档清空（day / range / all）
├─ Services/
│  ├─ FlushService.cs    ★ 刷盘流水线：快照 → 日志 → DB，失败留队列/记 log_gap
│  ├─ StatsQuery.cs      查询门面（今天的结果合并内存未刷盘增量）
│  ├─ RetentionService.cs 保留期清理（dataRetentionDays）
│  ├─ SingleInstance.cs  单实例锁 + 激活已有实例
│  └─ AutostartService.cs 计划任务注册/注销（schtasks / Register-ScheduledTask）
├─ Export/CsvExporter.cs 三档 CSV（UTF-8 **with BOM**）
├─ Charts/               图表抽象与 ScottPlot 实现（曲线/柱状/热力图/表格）
└─ UI/                   主窗口 / 挂件 / 设置 / 托盘 / 对话框 / AppHost 装配
```

**分层规则**：`UI/Charts → Services → Storage → Core`，反向不依赖；`SqliteStore` 是进程内唯一 DB 写入点，串行化由内部 `lock(_gate)` 保证。

---

## 4. 计数引擎（`Core/CounterEngine.cs`）

### 4.1 数据结构

- `_cur = new MinuteBuckets[1440]`：当前天，索引 `minute_of_day = hour*60+minute`；
- `_pending: List<(DateOnly Day, MinuteBuckets[] Buckets)>`：已跨天但**尚未刷盘**的整天数据（§5.4）；
- `_downKeys: HashSet<int>`：自维护"当前按下的键"（`WH_KEYBOARD_LL` 不给 `lParam` bit30，自动重复只能自己判，§5.5）；
- `_downButtons: HashSet<MouseButton>`：同上，保证"每次物理按下计 1、拖拽只计 1"；
- `_vAcc/_hAcc`：滚轮 delta 余量，跨事件累积，不足 120 不丢（§15.5 坑 20）；
- `_lastInputTick`：活跃时长的单调基准（`Stopwatch` tick，绝不用 `DateTime`）。

### 4.2 线程安全

所有写入路径统一走 `Add(...)`：`lock(_gate) { 选中分钟槽 → 自增 }`。
`TakeSnapshot()` 在同一把锁内把非零槽**拷贝并原地清零**，返回 `List<(Day, Minute, MinuteBuckets)>`。
`PendingMinutes(day)` 供查询层读"未刷盘增量"，同样在锁内拷贝（不清零）。

### 4.3 计数口径实现（对应文档 §2.3，红线）

| 规则 | 实现位置 |
|---|---|
| 只算物理按下 | `OnKey(vk, down, injected)` 只看 `down`；`keyup` 只做 `_downKeys.Remove` |
| 自动重复默认不计 | `_downKeys.Add(vk)` 失败且 `!CountAutoRepeat` → 不计数但仍刷新活跃时长 |
| 修饰键默认计入 | vk ∈ {0x10,0x11,0x12,0x5B,0x5C}，`CountModifierKeys=false` 时跳过 |
| 注入默认不计 | `injected && IgnoreInjected && !Debug.CountInjected` → return |
| 点击每次 DOWN 计 1 | `_downButtons.Add(btn)` 成功才计数（双击=2，拖拽=1） |
| 滚轮 120/格 | `_vAcc += delta; while(_vAcc>=120){WU++; _vAcc-=120;}`，反向同理；横滚单独 W L/W R |
| 光标移动完全不统计 | `LowLevelHookSource` 的鼠标回调入口 `if (msg == WM_MOUSEMOVE) return CallNextHookEx(...)` |
| 活跃时长 | `gap = (nowTick - _lastInputTick)/Frequency`；`0 < gap <= idleThresholdSec` → `AS += round(gap)`，落在**间隔结束时刻**的分钟 |
| 分钟归属 | 每个事件各自取当前分钟落桶，**不用刷盘时刻** |

### 4.4 时钟

`IClock.Now`（DateTimeOffset）+ `Ticks/Frequency`（Stopwatch）。引擎默认 **200ms 缓存**当前时间（§5.2），
即分钟边界最多 200ms 误差（已知限制 R6）；单测用 `CounterEngine(cfg, fakeClock, cacheClock: false)` 精确控制。

### 4.5 跨天

`RefreshClock()` 发现日期变化 → `Rollover(newDay)`：旧 `_cur` 整体放入 `_pending`，新建 1440 槽，
清空 `_downKeys/_downButtons/_lastInputTick/_vAcc/_hAcc`，触发 `DayRollover` 事件（UI 刷新）。
`_pending` 只会由 `TakeSnapshot()` 取走，保证跨天瞬间未刷盘数据不丢（§5.4）。

### 4.6 自身进程排除

两层：
1. 钩子回调侧每 500ms 缓存一次前台窗口 PID，与 `Environment.ProcessId` 比对（`excludeSelf`）；
2. 挂件是 `WS_EX_NOACTIVATE`，可能永远不成为前台窗口 → 挂件鼠标按下时额外 `engine.SuppressFor(50ms)`（§5.7）。

---

## 5. 存储设计

### 5.1 两套数据，日志为权威

| 数据 | 位置 | 角色 |
|---|---|---|
| SQLite（`data.db`） | `minute_stat` / `day_stat` / `log_cursor` / `log_gap` / `purge_log` / `schema_meta` | 查询快、可聚合；**可被重建的派生数据** |
| JSONL（`data\YYYY-MM-DD.jsonl`） | 每行一个**增量**（delta） | **权威源**：删库后 100% 重建 |

关键推论：写入必须**先日志后数据库**；某天日志写失败 → `log_gap(day)` → 重建跳过该天（DB 仍是该天唯一来源），下次刷盘再重试。

### 5.2 表结构（schema v2）

```sql
schema_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)
minute_stat(stat_date TEXT, minute_of_day INT, user_sid TEXT,
            key_press, mouse_left, mouse_right, mouse_middle,
            wheel_up, wheel_down, wheel_left, wheel_right, active_seconds,
            PRIMARY KEY(stat_date, minute_of_day, user_sid)) WITHOUT ROWID
CREATE INDEX ix_minute_date ON minute_stat(stat_date)
day_stat(stat_date, user_sid, <同 9 个计数列>, first_ts TEXT, last_ts TEXT,
         utc_offset_min INT DEFAULT 0, updated_at TEXT,
         PRIMARY KEY(stat_date, user_sid))
purge_log(id INTEGER PRIMARY KEY AUTOINCREMENT, purged_at, scope, from_date, to_date)
log_gap(stat_date TEXT PRIMARY KEY, reason, created_at)
log_cursor(stat_date TEXT PRIMARY KEY, byte_offset INT, line_count INT,
           total_key INT, total_click INT)
```

- `PRAGMA journal_mode=WAL; synchronous=NORMAL; busy_timeout=3000`；
- `minute_of_day ∈ 0..1439`；`user_sid` 隔离多用户；
- **没有 hour_stat**：小时视图由 `minute_of_day/60` 聚合（v2 决策，减少写放大）。

### 5.3 写入（幂等增量合并）

```sql
-- minute_stat
INSERT ... ON CONFLICT(stat_date,minute_of_day,user_sid) DO UPDATE SET
  key_press = key_press + excluded.key_press, ... ;
-- day_stat
INSERT ... ON CONFLICT(stat_date,user_sid) DO UPDATE SET
  key_press = key_press + excluded.key_press, ...,
  first_ts = COALESCE(day_stat.first_ts, excluded.first_ts),
  last_ts  = excluded.last_ts,
  updated_at = excluded.updated_at;
```

一批写入 = 一个事务（一次刷盘一个事务），避免 30s 内成百上千次提交。

### 5.4 JSONL 日志格式（契约，逐字）

首行（文件新建时写一次，可缺省）：
```json
{"type":"header","v":1,"app":"2.3.0","host":"DESKTOP-ABC","sid":"S-1-5-...","utcOffset":480,"createdAt":"2025-01-01T00:00:12+08:00"}
```
数据行（**全部是增量 delta**，同分钟可多行，导入时求和）：
```json
{"v":1,"date":"2025-01-01","min":515,"k":35,"ml":8,"mr":1,"mm":0,"wu":2,"wd":0,"wl":0,"wr":0,"as":30,"t":"08:35"}
```
- UTF-8 **无 BOM**，`\n` 换行，追加写，每次 flush 落盘；
- 只写非全零行；字段可缺省视为 0；
- 未知字段（含旧版 `mp`）直接忽略；未知 `v` 版本按最高兼容尽力解析；
- 末行半行（进程崩溃）解析失败 → 忽略该行 + 告警 + `RepairTail` 截断到最后一个完整行；
- `{date}.summary.json` 只是给人看的汇总快照，**不参与任何计算**。

### 5.5 刷盘流水线（顺序不可颠倒）

```
引擎快照 TakeSnapshot()（锁内拷贝+清零）
  → 空则 return
  → 按 Day 分组
  → 每天：DailyLogWriter.Append(day, items)  ── 失败 → AddLogGap(day)，数据留在队列下次重试
                                              └ 成功 → SqliteStore.SaveBatch + SetCursor
  → pending 全部成功后才清空
```

两个队列：`_logQueue`（未进日志）与 `_dbQueue`（已进日志未进库）。
**合并只发生在 `_logQueue`**，避免新数据绕过日志直接入库（否则日志不再是权威源）。
日志连续失败 > 1 小时 → 降级为"只写库 + `log_gap`"，防止磁盘长期不可写时丢数据。

### 5.6 启动对账 / 重建（`LogDbReconciler`）

单实例锁之后、钩子启动之前执行（预算 20s，超时转后台继续）：

1. `ListLogDays(dataDir)` 得到日志日期集合 F，与 `day_stat` 日期集合比对；
2. 每天：`RepairTail` 修复半行 → 有 `log_gap` 则跳过；
3. `CountMinuteRows(day)==0 && GetCursor(day)==null` → **全量导入**（先删该日 `minute_stat` **与** `day_stat` 再插入，保证幂等）；
4. 否则从 `log_cursor.byte_offset` **增量导入**，按 `line_count/total_key/total_click` 校验；
5. 每步成功后写 `log_cursor`；
6. 数据库有、日志缺的日期：非今天 → 计入 `OrphanDbDays` 并告警（提示日志被手工删过）。

### 5.7 清空（`DataPurger`）

| 档 | 动作 |
|---|---|
| 今日 | `engine.ClearDay` + 删今日 `minute_stat`/`day_stat`/`log_cursor` + 删除并重建今日 jsonl + `purge_log('day')` |
| 区间 | 逐日同上一档 + `DeleteRange` + `purge_log('range')` |
| 全部 | 停钩子 → `FlushNow` → 删全部表 → 删全部 jsonl/summary → `purge_log('all')` → 重启钩子 → 清空内存桶 |

全部必须输入 `DELETE` 二次确认；清空动作只记录范围与时间，不记录数值。

### 5.8 保留期

`dataRetentionDays > 0` 时删除 `stat_date < today - N` 的整天（DB 行 + 日志文件），与手动清空共用 `DataPurger`，
写 `purge_log('range')`；0 = 永久保留（默认）。

---

## 6. 查询层（`StatsQuery`）

- 所有**今天**的查询都把 `engine.PendingMinutes(today)` 合并进数据库结果——否则用户最多看到 30s 前的旧数据（§5.10）；
- 打开窗口 / 切标签 / 点刷新前先 `FlushNow()`（把内存与日志落库，保证"刚按的键立刻可见"）；
- 异步方法一律 `Task.Run` 包装同步实现，UI 线程不阻塞；
- `metric` 参数决定聚合列：`key_press` / `(mouse_left+mouse_right+mouse_middle)` / 滚轮四列之和 / `active_seconds`。

---

## 7. 界面设计

### 7.1 主窗口（`UI/MainForm.cs`，默认 1000×700）

```
┌ 顶栏：KeyMouseCounter                                    [⚙][刷新][导出][暂停] ┐
│ 三张大数字卡：键盘按键 / 鼠标点击(=左+右+中) / 活跃时长（副行滚轮格数）+ 较昨日      │
│ 工具栏：[今日·按小时|今日·按分钟|多日·按天]  日期▾  最近[7▾]天  [曲线|热力图|表格]  │
│ 图表区（曲线/热力图/表格）                                                        │
└ 摘要：指标：[键盘按键▾] 合计 15,234 · 峰值 08:00(1,820) · 日均 2,176              ┘
```

- 今日视图窗口可见时每 5s 刷新；多日视图仅在数据变动时刷新；
- 关闭窗口 = 隐藏到托盘（真正退出走托盘菜单）；
- 快捷键：`F5`/`Ctrl+R` 刷新，`Esc` 隐藏，`Ctrl+D` 日期菜单。

### 7.2 三视图

| 视图 | 今日·按小时 | 今日·按分钟 | 多日·按天 |
|---|---|---|---|
| 曲线 | 24 点折线/柱状 | 1440 点（直线段，不做样条） | 日期轴（`DateTimeTicksBottom`） |
| 热力图 | **不做，固定柱状图** | 24 行 × 60 列（行=小时，列=分钟） | 24 行 × N 列（列=日期） |
| 表格 | VirtualMode，默认只显示非零行 | 1440 行 VirtualMode，可选"显示全部" | 日期/星期/键盘/点击/滚轮/活跃/较前日 |

热力图配色：`ColorRamp` 五档（`#111827 → #1E3A8A → #2563EB → #60A5FA → #FBBF24`），
按 P50/P75/P90/P99 分位分级；0 值用背景色且**不参与**分位数；
`Pick(value)` 命中判定必须 `value <= thresholds[i] → Stops[i+1]`（§15.4 的 off-by-one 坑，已有单测 T24 守）。

曲线：1.6px 主色 + 8% 透明面积填充；空数据显示居中灰字"暂无数据"，不画空坐标系。

### 7.3 挂件（`UI/WidgetForm.cs`）

- 无边框、置顶、不占任务栏、`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，`ShowWithoutActivation => true`；
- 默认 240×96（随 `fontSize` 缩放），两行大数字 + 标签 + 分隔线 + "活跃 xh ym · 滚轮 n 格"；
- 拖动：`ReleaseCapture()` + `WM_NCLBUTTONDOWN/HTCAPTION`；位置 500ms 防抖持久化；启动越界校正回主屏；
- 穿透：额外 `WS_EX_LAYERED|WS_EX_TRANSPARENT`，切换后必须 `RecreateHandle()`（ExStyle 只在创建时生效）；
- `Ctrl+滚轮` 调透明度 0.4–1.0；首次启用 (40,40) 3 秒淡入；1s 定时器刷新，文本未变不 `Invalidate`；
- 暂停态整体变灰 + 右上角 `Ⅱ`。

### 7.4 设置（`UI/SettingsForm.cs`，左导航 + 右内容）

常规（自启/启动最小化/关闭行为/主题）· 挂件（启用/穿透/透明度/字号/显示活跃/热键+占用检测/重置位置）·
计数口径（6 个开关，即时生效 + 提示"口径变更只影响之后的数据"）· 数据（目录/保留天数/三档清空/CSV/从日志重建/完整性检查）· 关于（版本/运行时长/今日行数/日志路径/隐私说明）。

### 7.5 图表抽象

`interface IChartView { Control Host { get; } void Render(ChartData data); }`
主窗口只认接口；`Charts/LineChartView`（ScottPlot）、`Charts/HeatmapView`（自实现 `IColormap` 分级配色）、
`Charts/TableChartView`（DataGridView VirtualMode）各实现一份。若某环境无法用 ScottPlot，可按 §7.5 用 GDI+ 实现同接口替换。

---

## 8. 可靠性与自愈（文档 §9.2）

| 场景 | 处理 |
|---|---|
| 未捕获异常 | `AppDomain.UnhandledException` / `Application.ThreadException` / `TaskScheduler.UnobservedTaskException` 全捕获写日志并提示托盘 |
| 钩子安装失败 | 托盘气泡 + 日志 + 每 60s 重试 |
| 钩子被静默摘除 | 每 60s 检查：`GetLastInputInfo` 显示系统 2 分钟内有输入而引擎 `LastCountedTick` 超 120s → 重装钩子并告警 |
| SQLite 损坏 | `IntegrityCheck()!=0` 或 `SqliteException` → 备份 `data.db.corrupt` → 删 `-wal/-shm` → 新建库 → 从日志全量重建 |
| 磁盘满/日志不可写 | 停写日志、留 `log_gap`、继续内存与 DB 写入，每 5 分钟重试，挂件显示 ⚠ |
| 分辨率/DPI 变化 | 挂件处理 `WM_DPICHANGED` 并校正位置 |

运行日志：`logs\app-yyyyMMdd.log`，格式 `[yyyy-MM-dd HH:mm:ss.fff][LEVEL] msg`，单文件 ≤2MB 保留 3 个；
**禁止输出 vkCode 序列、窗口标题、输入文本**。

### 8.1 已修缺陷：启动即弹「参数无效」对话框

现象：程序启动瞬间弹 WinForms JIT 异常框 `System.ArgumentException: Parameter is not valid.`，
栈为 `System.Drawing.Region.GetHrgn` ← `PInvoke.RegionScope..ctor` ← `Control.SetRegion` ←
`Control.OnHandleCreated` ← `WidgetForm.OnHandleCreated`；点「继续」后程序一切正常。

根因：`WidgetForm.ApplyRoundedCorners` 早期写法是
`using var rgn = Region.FromHrgn(CreateRoundRectRgn(...)); Region = rgn;`。
`Region` 被 `using` 立刻 Dispose，而 `Control.Region` 只是**持有引用**；等 WinForms 在
`OnHandleCreated` 里 `SetRegion` 时再去取 HGDI 句柄 → 句柄已失效 → `ArgumentException`。
即「一次性代码在异常情况下才暴露」的典型资源所有权错误。

修法（`WidgetForm.cs`）：
- 不再把托管 `Region` 交给控件；改用原生 `SetWindowRgn(Handle, hrgn, redraw:true)`，
  成功后 HRGN 归系统所有（不得 `DeleteObject`），失败才自己 `DeleteObject` 回收；失败吞掉不影响启动。
- 调用点仍放在 `OnHandleCreated`（圆角/毛玻璃都要有效 HWND，且 DPI/ExStyle 变化重建句柄后能重设）。
- `Program.cs` 补上 §9.2 要求的全局兜底：`Application.SetUnhandledExceptionMode(CatchException)` +
  `Application.ThreadException` / `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`
  全部写日志（UI 线程异常不会让程序静默退出）。原代码没有兜底，所以才会弹系统 JIT 对话框。
- 回归测试：`KeyMouseCounter.Tests/WidgetFormTests.WidgetForm_HandleRecreation_DoesNotThrow`
  （建 `WidgetForm` → 取 `Handle` → `ToggleClickThrough()` 触发真实句柄重建 → 再取 `Handle`）。
  该测试在旧写法下必现同样异常，修复后通过。

**经验**：给 `Control.Region` / `Control.BackgroundImage` / `Font` 等「控件只持引用」的属性赋值时，
不要立刻 `Dispose` 源对象；能交给系统的原生资源（HRGN/HBITMAP/HFONT）优先走 `SetWindowRgn` 这类所有权转移 API。

---

## 9. 性能预算与实测

| 项 | 预算 |
|---|---|
| 键盘回调 | 20 事件/s 时 < 0.05ms/次 |
| 鼠标回调 | 1000 事件/s 时 < 0.005ms/次（`WM_MOUSEMOVE` 直接 `CallNextHookEx`，实际不进入引擎） |
| 一次刷盘 | 30s 数据 < 10ms |
| 今日按分钟图 | < 300ms |
| 90 天热力图 | < 500ms |

手段：回调零 I/O、零分配（无 LINQ）；分钟桶用值类型数组；查询走索引 `ix_minute_date`；表格 VirtualMode。

---

## 10. 配置（`config.json`，schema v2）

位于 `%LOCALAPPDATA%\KeyMouseCounter\config.json`；损坏 → 备份 `config.bad.json` + 回落默认值。
热加载：每 60s 比较 `LastWriteTimeUtc`，`counting.*` 与 `widget.hotkey` 立即生效（`ConfigWatcher`）。

```jsonc
{
  "schemaVersion": 2,
  "counting": { "countAutoRepeat": false, "countModifierKeys": true, "ignoreInjected": true,
                "excludeSelf": true, "countWheel": true, "idleThresholdSec": 60 },
  "storage":  { "writeDailyLog": true, "flushIntervalSec": 30, "dataRetentionDays": 0 },
  "widget":   { "enabled": true, "clickThrough": false, "opacity": 0.88, "x": 40, "y": 40,
                "fontSize": 10, "showActiveTime": true, "accentColor": "#4F8DF7",
                "hotkey": "Ctrl+Alt+K", "hideOnFullscreen": false, "compactNumbers": false },
  "ui":       { "defaultTab": "TodayHour", "defaultChart": "Line", "defaultRangeDays": 7,
                "startMinimized": true, "lastMainWindowSize": [1000, 700],
                "closeToTray": true, "theme": "dark" },
  "debug":    { "countInjected": false, "logLevel": "Info" }
}
```

---

## 11. 部署

- 发布：`scripts\publish.ps1`（内部即 §8.3 的 `dotnet publish` 组合），产物 `dist\KeyMouseCounter.exe`；
- 自启：计划任务 `KeyMouseCounter`（`AtLogOn`、`AllowStartIfOnBatteries`、`ExecutionTimeLimit=0`、
  `MultipleInstances=IgnoreNew`、`RestartCount=3/1min`、`LogonType=Interactive`、`RunLevel=Limited`）；
  exe 内置 `--register-autostart/--unregister-autostart`，设置页开关调它们；
  **注意**：创建/删除计划任务需要管理员权限（普通令牌下 `Register-ScheduledTask` 返回
  `0x80070005 拒绝访问`），所以 `install-autostart.ps1` 声明 `#Requires -RunAsAdministrator`；
  exe 未提权时返回可读中文提示而不是静默失败。任务本身以 `RunLevel=Limited` 运行，不常驻提权；
- 卸载：托盘退出 → `uninstall-autostart.ps1` → 删程序目录 → 按需 `--purge` 删数据目录。

---

## 12. 测试

- 单测 `src/KeyMouseCounter.Tests`：T01–T26 覆盖 26 条验收用例，当前 **33 个 Fact 全绿**；
  全部无 UI、无真实钩子（`FakeClock` 注入时间，`AppPaths.RootOverride` 重定向数据目录到临时目录）；
- 测试程序集禁用并行（`[assembly: CollectionBehavior(DisableTestParallelization = true)]`），因为 `AppPaths.RootOverride` 是静态状态；
- 手工矩阵 M01–M22 的执行结果与真机截图见 **`docs/screenshots/README.md`**（12 张截图，逐项标注证据或未完成原因）；
- 真机冒烟覆盖：`--count-injected` + 合成按键 → JSONL 只出现 delta 行；删 `data.db` 重启 → `reconcile … full=1` 全量重建；
  单实例唤起、钩子自愈（`GetLastInputInfo` 判定）、计划任务注册路径均在真机走过。

---

## 13. 关键决策记录（以及为什么）

| 决策 | 理由 |
|---|---|
| 日志为权威源、DB 可重建 | 用户删库/库损坏不丢历史；JSONL 人类可读、可校验、可手工修复 |
| 先日志后 DB | 反过来会在"日志失败 DB 成功"时产生"DB 有数据但无法重建"的不可恢复态 |
| 全部 delta 行而非快照 | 追加写天然幂等可重放；崩溃只丢最后半行；同分钟多行求和即可 |
| 没有 hour_stat | 小时 = `minute_of_day/60` 聚合，读时算一次远廉价于写时多一张表 |
| 分钟桶数组而非字典 | 1440 槽定长、索引即分钟，无哈希无分配，回调里最快 |
| 短锁而非无锁 | 无锁换数组会丢"正在写旧桶"的事件 |
| Stopwatch 而非 DateTime 算活跃 | 系统改时间/夏令时不影响时长（§5.2） |
| 挂件 `WS_EX_NOACTIVATE` + `SuppressFor` | 挂件永不抢焦点，但又要点它时不自计数 |
| 表格 VirtualMode | 1440 行 × 7 列常规绑定会卡；VirtualMode 只画可见行 |
| UI 与图表用 `IChartView` 抽象 | ScottPlot 不可用时按 §7.5 换 GDI+ 实现，不动主窗口 |

---

## 14. 已知限制

见 README §7 与文档 §13（UAC 安全桌面/锁屏无法统计、杀软误报、钩子摘除、IME 合成键、分钟归属 200ms 误差、
日志被手工删除、多用户并发写库用 WAL + `user_sid` 隔离等）。
