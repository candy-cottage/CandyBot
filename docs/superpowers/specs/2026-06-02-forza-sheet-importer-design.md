# Forza 车辆表自动导入器 — 设计文档

- 日期：2026-06-02
- 状态：已与用户确认，待写实现计划
- 分支：`feat/forza-sheet-importer`

## 1. 背景与问题

Forza（地平线6）`Vehicles` 表的数据来源是一张**私有腾讯文档表格**「地平线6车辆摸索V1.0」（fileId `DSXRieHNsZXpFZmZ4`，ZGC 车队调校摸索表）。该表需授权访问，只能经机器人 `TencentDocsConfig` 的腾讯 OpenAPI 凭据读取（即 `SheetService` 走的那条）。

当前灌库是**人工、Repo 外**的：有人把 `SheetService` 输出 dump 成 JSON，再跑一个一次性控制台导入器（`~/Downloads/Program.cs`，`namespace ForzaVehicleImporter`）逐条 POST 到 `/Forza/vehicles`。`SheetService` 并未与 Forza 打通——表格→DB 是手动断点。

合作者反馈"解析不稳定"。用真实 JSON（`DSXRieHNsZXpFZmZ4.json`）复现并量化后，确诊为**确定性 bug 导致的静默丢数据**（现行解析 128 辆，应有 157 辆，丢约 19%，且两个等级几乎全军覆没）：

- **根因 #1（灾难级，"不稳定"主因）**：每张表右侧另有一块「Purist专区」小表（面向 Purist 玩法的调校推荐，见 §2 另案），表头也叫`制造商`/`型号`。当主表表头与该小表表头**在同一行**时（B-600公路、C-500公路），`BuildColumnMap` 用字典存"表头文字→列号"，同名键**后者覆盖前者** → `colManufacturer`/`colModel` 指向右侧空列 → 型号恒空 → 几乎每行被"空型号 skip"丢弃。C-500公路 0 辆、B-600公路 1 辆（且是误读）。表头不同行的表（S1-800、A-700）因"假表头→break→真表头重入"的巧合而侥幸正常——这就是"不稳定"的根源：仅凭排版偶然，结构相同的表一个全对一个全崩。
- **根因 #2（慢性失血）**：型号纵向合并的子行（同车第 2/3 套调校，各带独立调校师/圈速）被"空型号 skip"丢掉；`lastModel` 算了却从没用来回填。实测丢 26 条带数据的子行。
- **根因 #3**：导入器硬编码 10 张表白名单，漏掉 `季节赛推荐调校（每周更新）`。

（已排除候选：锯齿行——每张表行宽统一，腾讯已补齐，不存在越界丢字段。）

## 2. 目标与非目标

**目标**：把"表格 → Vehicles 表"做成仓库内的、可重复、幂等的同步流程，并修复上述解析 bug。

**非目标（YAGNI，本期不做）**：
- 定时调度 / HTTP 触发（同步逻辑独立成方法，以后加触发器只是加调用方）
- 自然键 upsert（已选全量覆盖）
- 季节赛表（每周更新，静态导入会过期）
- **Purist 专区（右侧小表）——另案处理**：每张表右侧的「Purist专区」是面向 **Purist 玩法**（关闭全部辅助、手动挡的"原汁原味"驾驶）的专属调校推荐，列只有 `制造商 / 型号 / 推荐调教`。它是与"常规调校"**正交的玩法维度**，不能无标签平铺进 `Vehicles`（否则查车分不清常规 vs Purist）。本期解析器只取主表常规调校并**主动无视**右侧 Purist 区（修复 #1 的直接效果正是不再被它的重复表头干扰）。要做应作为独立小功能（打标签或独立查询类别），等 ZGC 把该区填充起来后再处理。现状：全书仅 3 条，其中 2 条为主表没有的独有车（A-700 的 GR86、RS4），约占 157 辆的 1.3%。
- 让 AI 进入解析/写入路径（AI 仅作只读诊断，见 §7）

## 3. 已确认的决策

| # | 决策 | 选择 |
|---|------|------|
| 1 | 运行时数据源 | **同步进 Vehicles 表**；查车 / ForzaController / 现有测试不变（仍读 DB） |
| 2 | 同步策略 | **全量覆盖**：单事务内 `DELETE` 后重灌 + **零行/异常少安全闸** |
| 3 | 触发方式 | **管理员指令 `同步车辆表`** 起步；同步逻辑独立成 `SyncAsync()` |
| 4 | 同步范围 | **自动识别**含"制造商+型号"表头的工作表；**排除**名含"季节赛/每周"的表 |
| 5 | 多套调校建模 | **一套调校一行**：空型号+有调校视为同车延续，继承制造商/型号单独成 `Vehicles` 记录，保留各自圈速/备注/强度 |
| 6 | 模块拆分 | **纯解析器 + 编排器**（I/O 与核心逻辑分离） |
| 7 | AI 兜底 | **确定性解析独占写库；AI 仅只读诊断**，解释解析失败原因，不碰数据库 |

## 4. 架构与数据流

```
管理员: 同步车辆表
  → CandyBotService.HandleForzaHorizonInput → CheckUser(e, user, false)  // 仅管理员
  → ForzaSheetImporter.SyncAsync(fileId)
       1. SheetService.GetSheetDataAsync(fileId)             // 取全表网格(带缓存)
       2. 选表: 每张工作表,名不含"季节赛/每周" 且 含"制造商+型号"表头 → 纳入
       3. VehicleSheetParser.Parse(sheetName, grid)          // 纯函数,3处修复,一套调校一行
       4. 安全闸: 解析总数==0 → 拒绝覆盖,回执失败(旧数据不动)
       5. 单事务: DELETE FROM Vehicles; 逐条 InsertVehicle; COMMIT(失败 ROLLBACK)
       6. 对"该解析却没解析出来"的表 → 调 ISheetParseDiagnostic(只读,best-effort)
  → 回执: ✅ 已同步 157 辆(原 128;跳过 N 行/表 + 原因 + AI 诊断提示)
```

## 5. 组件

### 5.1 `VehicleSheetParser`（新增，纯逻辑，`internal` + `InternalsVisibleTo` 便于单测）
- 输入：`(string sheetName, List<List<string>> grid)`；输出：`ParseResult { List<VehicleRequest> vehicles; List<SkippedRow> skipped }`。
- 移植 `ParseSheetImproved`，并施加三处修复：
  - **修复 #1（钉死方案）**：列映射改**首次命中优先**——构建列映射时，同名表头**不覆盖**先出现的列号（现行用字典 `map[cell]=i` 后者覆盖前者，是根因）。这样主表的`制造商`@0 胜过右侧小表的`制造商`@12。已用真实数据验证此法即可恢复 B-600(1→17)、C-500(0→7)。（备选"扫描止于主表区域"不采用，除非后续出现首次命中也歧义的表。）
  - **修复 #2**：空型号 + 有调校 → 视为同车延续，**继承制造商/型号**单独成行（=一套调校一行）。
  - **修复 #3**：解析器不再持有硬编码白名单；"哪些表要解析"的判断上移到编排器（§5.2 第 2 步）。
  - `Class` 由表名推导（复用 `ExtractClassFromSheetName`）；`MatchType` 由"比赛类型："段落行或表名默认推导。
- 必填校验：缺 `Class`/`MatchType`/`Manufacturer`/`Model`（DB 的 NOT NULL 列）任一的行 → 计入 `skipped`（带原因），不抛异常。

### 5.2 `ForzaSheetImporter`（新增，编排，scoped 服务）
- `SyncAsync(string fileId)`：取数 → 选表 → 调解析器 → 安全闸 → 单事务全量替换 → 收集跳过项/触发诊断 → 返回 `SyncReport`。
- 选表规则：工作表名不含"季节赛/每周"，且其网格含"制造商+型号"表头 → 解析；否则跳过（名含季节赛者静默跳过，应解析却无表头者送诊断）。

### 5.3 触发处理器
- `ForzaHorizonInputHandler` 增加 `同步车辆表` 前缀分支，`CheckUser(e, user, false)`（仅管理员），调用 `ForzaSheetImporter.SyncAsync` 并把 `SyncReport` 渲染成回执。

### 5.4 `ForzaService` 的定向改造（服务于本目标，最小化）
- 现有 `AddVehicle` 自带 `NewTransaction()/Commit()`，无法在外层事务里循环调用。
- 抽出**无事务插入** `InsertVehicle(VehicleRequest)`：只插入、不开事务、不做交互式必填校验（只受 DB NOT NULL 约束）。
- `AddVehicle` 改为"开事务 → `InsertVehicle` → 提交"，行为不变。
- 导入器在自己的单事务里循环调 `InsertVehicle`。

### 5.5 `ISheetParseDiagnostic`（新增接口）
- `Task<string> DiagnoseAsync(string sheetName, List<List<string>> sampleRows)` → 返回人话提示。
- **AI 实现（DeepSeek，见 §9）**：把样本前 ~10 行喂给 LLM，提示"本表应是车辆调校表，列含制造商/型号/…，但未识别到表头，一句话说哪里不同"。
- **No-op 默认实现**：无 API key 时返回空 → 功能降级为纯确定性。
- 注入到 `ForzaSheetImporter`；单测/集成测试一律注入 No-op。

## 6. 全量覆盖事务与安全闸
- `DELETE FROM Vehicles` + 逐条 `InsertVehicle` 包在单个 `sql.NewTransaction()` 内，任一步失败 → `Rollback()`，旧数据原样保留（全有或全无）。
- **安全闸**：解析车辆总数为 0（抓取异常 / 全表解析失败）→ **绝不执行 DELETE**，直接回执失败。这是全量覆盖唯一的硬风险，必须有。（按比例的"远低于现有"阈值留到加定时触发时再引入；管理员手动触发有人盯着结果，零行闸足够。）

## 7. 错误处理（契合"单条失败不拖垮整批"）
- **解析层**：缺必填的行 → 跳过 + 计数 + 原因，进 `SkippedRow`，不中断。
- **写库层**：见 §6，事务保证原子性。
- **AI 诊断层**：best-effort + 限时 + 吞异常。超时/报错/无 key → 同步照常成功，回执只少一句提示，降级为"跳过 N 张表（无 AI 诊断）"。诊断只跑在管理员手动指令路径，不在热路径。

## 8. 测试策略
- **纯解析器单元测试**（`CandyBot.Tests`，无 DB）：用真实 JSON 抽样的几行造 fixture，锁死回归：
  - ① 同名表头列（B-600 那种）→ 解析出 ≥17 辆（验证修复 #1）
  - ② R8 LMS 合并子行 → 恢复出 `CybrexzzZ[META] / 白川54.5·传奇岛14.9` 那条（验证修复 #2 + 一套调校一行）
  - ③ 名含"季节赛"的表 → 被排除
  - ④ 缺型号且无调校的行 → 计入 skipped 而非崩溃
- **导入器集成测试**（`CandyBot.IntegrationTests`，Testcontainers MySQL，注入 No-op 诊断）：
  - 喂完整 `DSXRieHNsZXpFZmZ4.json` → 断言库里 157 辆
  - 重跑一次 → 仍 157（幂等）
  - 喂空/零行数据 → 断言安全闸拒绝、旧数据不动

## 9. AI 诊断提供方：DeepSeek（已定）
- **提供方**：DeepSeek API，**OpenAI 兼容**（base URL `https://api.deepseek.com`，模型 `deepseek-chat`，`Authorization: Bearer <key>`）。选它因为：低频只读诊断、成本低、国内可达、契合中文场景。
- **实现**：`ISheetParseDiagnostic` 的 DeepSeek 实现经 `IHttpClientFactory` 发 chat-completions 请求（复用项目既有的 `IHttpClientFactory` 模式，如 `SheetService`）；prompt 见 §5.5。
- **配置**：新增轻量配置（如 `DeepSeekConfig { ApiKey, BaseUrl=https://api.deepseek.com, Model=deepseek-chat, Enabled }`），**API key 走 user-secrets / 环境变量**，不入 `appsettings.json`（与 `Bot` 段同规）。
- **降级**：未配置 key 或 `Enabled=false` → DI 注册 No-op 实现，功能退化为纯确定性（§7）。单测/集成测试一律注入 No-op，不依赖外部网络。

## 附录：修复前后实测（来自 `DSXRieHNsZXpFZmZ4.json` 真实数据）

| 工作表 | 现行 | 修复后 | 增量 |
|--------|------|--------|------|
| R-998公路 | 9 | 9 | |
| S2-900公路 | 18 | 19 | +1 |
| S1-800公路 | 52 | 56 | +4 |
| S1-800泥地 | 9 | 9 | |
| A-700公路 | 25 | 26 | +1 |
| A-700泥地 | 8 | 8 | |
| B-600公路 | 1 | 17 | +16 |
| B-600泥地 | 3 | 3 | |
| C-500公路 | 0 | 7 | +7 |
| C-500泥地 | 3 | 3 | |
| **合计** | **128** | **157** | **+29** |
