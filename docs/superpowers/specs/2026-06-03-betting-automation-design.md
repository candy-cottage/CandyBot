# 赛事预测自动化设计（抓取入草 + 一键确认）

- 日期：2026-06-03
- 状态：设计已确认，待落实施计划
- 范围：CandyBot 赛事预测系统（`betting_*` 三表 + `BettingService` + `BettingInputHandler`）

## 1. 背景与目标

当前赛事预测的数据**全部由人工产生**：管理员在群里手打 `创建赛事`/`创建比赛`/`结算比赛`，用户发 `预测` 下注。没有任何外部数据源或导入器（与地平线 Forza 的"腾讯表格→同步导入器→库"模式不同）。

目标：把**真实职业电竞赛事（首期 CS2）**的赛程与结果自动化引入，减少管理员手工录入和盯结果结算的负担，但**保持人对两个关键动作的最终把关**：

1. 草稿**发布**给用户（决定哪些比赛对外开放预测）。
2. 比赛**结算派彩**（真金白银发放糖果/金币，错了难追回）。

### 核心决策（已与用户确认）

| 决策点 | 选定 | 理由 |
|--------|------|------|
| 数据真实来源 | 真实职业电竞赛事（首期 CS2） | 存在权威外部源，抓取 + 自动结算可行 |
| 人在回路程度 | **半自动**：抓取入草 + 一键确认 | 自动化省人力，发布/派彩必须有人背书 |
| 首期数据源 | **PandaScore API**（商业，有免费开发档） | 干净 JSON，含赛程/结果/博彩赔率，集成成本最低 |
| 数据源耦合 | **可插拔适配器接口**，先接一家 | 符合"硬编码实现、设计接口"原则，留换源/多源口子 |
| 抓取触发 | **定时后台轮询** + `立即同步赛事` 指令兜底，二者**共用核心方法** | 真正"自动抓取"；核心方法可单测、可指令触发 |

### 不在本期范围（YAGNI / 标 TODO(debt)）

- `score`（精确比分）与 `mvp` 选项的**自动结算**：很多数据源不提供逐图比分/本场 MVP，含这两类选项的比赛保持**纯人工结算**（现状）。
- 多数据源同时接入、跨源去重。
- 已发布比赛被 sync 更新时的精细冲突合并（本期只允许 sync 改"未开赛的开赛/截止时间"，不覆盖管理员改过的队名/赔率）。

## 2. 设计总览

```
PandaScore API
   │  (HTTP + IMemoryCache 缓存, 速率克制)
   ▼
IEsportsDataProvider  ──impl──▶ PandaScoreProvider     (可换源: Liquipedia/Fake 同接口)
   │  返回 provider-neutral DTO (ExternalEvent / ExternalMatch / ExternalResult)
   ▼
EsportsSyncService (scoped, 核心方法, 可单测/可指令触发)
   ├─ SyncSchedulesAsync()  → upsert 草稿 (is_published = 0)
   └─ SyncResultsAsync()    → 预填 result/winner + pending_settlement = 1 → 通知管理员
   ▲
   │ 两个触发器共用核心:
   ├─ EsportsSyncHostedService : BackgroundService  (定时, 每轮开 DI scope)
   └─ 管理员指令 立即同步赛事 (兜底)
                                   │ 管理员一键 确认结算 <id>
                                   ▼
                          已有的 SettleMatch()  ← 一行不改, 事务化派彩
```

**核心不变量**：自动化只负责"把数据搬到管理员面前并预填到草稿/待确认态"。真正动钱的 `SettleMatch` 完全复用、零改动。自动抓取出的脏数据永远碰不到派彩逻辑，除非有人点确认。

## 3. 模块设计

### 3.1 数据源适配器（`Services/Esports/`）

**`IEsportsDataProvider`** —— 与厂商无关的抓取接口：

- `Task<IReadOnlyList<ExternalMatch>> GetUpcomingMatchesAsync(string gameSlug, DateTime since, CancellationToken ct)`
- `Task<IReadOnlyList<ExternalResult>> GetFinishedResultsAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct)`

**Provider-neutral DTO**（不暴露任何 PandaScore 字段名）：

- `ExternalEvent` —— 外部赛事/联赛：`ExternalId`、`Name`、`StartTime`、`EndTime`。
- `ExternalMatch` —— 外部比赛：`ExternalId`、父赛事 `ExternalEventId` + 名称、`Team1Name`/`Team2Name`、`StartTime`、`Stage`、可选 `Team1WinProbability` 或 `Team1WinOdds`/`Team2WinOdds`（来自博彩盘口，缺省则空）。
- `ExternalResult` —— 结果：`ExternalId`、`ResultText`（如 `2:1`）、`Winner`（1/2）。

**`PandaScoreProvider`** —— `HttpClient` + `PandaScoreConfig.ApiKey`；JSON→DTO **纯映射**（这层可单测）。沿用 `SheetService` 的 `IMemoryCache` 缓存与速率克制。

**`FakeEsportsDataProvider`** —— 测试替身，返回固定 DTO，让链路无网络可单测。

**DI**：`IEsportsDataProvider` 注册为 scoped，实现由 `PandaScoreConfig.Mode`（或等价开关）选取，思路同 `SQLConnectionConfig.NewSQLHelper` 工厂。

### 3.2 入库 upsert（`EsportsSyncService.SyncSchedulesAsync`）

按 `external_id` **幂等**对账（与 Forza 157 辆幂等导入同理）：

- **找到** → 更新可变字段（见冲突保护）。
- **没有** → 插入草稿：`is_published = 0`、`status = 0`、`betting_enabled = 1`、`available_options` 默认 `"team1_win,team2_win"`、`bet_deadline = start_time`、赔率优先取 PandaScore 盘口（有则用 `ExternalMatch` 提供的赔率/胜率经 `CalculateOdds` 换算），否则 `CalculateOdds(0.5m)`。
- **赛事（event）** 同样带 `external_id` + `is_published`，按 `external_id` 幂等 upsert，草稿态 `is_published = 0`。
- **冲突保护**：已发布（`is_published = 1`）的比赛，sync 只更新"未开赛的 `start_time`/`bet_deadline`"，**不覆盖**管理员改过的队名/赔率/描述。更细的字段级冲突合并标 `TODO(debt)`。

**错误隔离**：每场 upsert 独立 `try/catch`，单场失败 `log` 后继续下一场（"一项失败不拖垮整批"）。

### 3.3 草稿发布（管理员指令）

- `待发布比赛` —— 列出 `is_published = 0` 的比赛草稿，每条带 `发布比赛 <id>` 快捷按钮（复用 `Button.CreateCmdButton`）。
- `发布比赛 <id>` —— 置 `is_published = 1`；**连带把其父赛事一并发布**（避免空赛事泄给用户）。此后该比赛才进入用户的 `赛事列表`/`赛程`/`赛事详情`。
- **用户侧已有查询要加 `WHERE is_published = 1` 过滤**：`GetEventsOverview`、`GetAllMatches`、`GetEventDetail`、`GetMatchDetail`（含其计数 SQL）。这是对已测代码的**行为变更**，按本仓库规范属 **P1：必须配套测试**。
- 管理员手工建的赛事/比赛 `is_published` 默认 1，不受影响。

### 3.4 结果预填 + 确认结算（`EsportsSyncService.SyncResultsAsync` + 指令）

- `SyncResultsAsync`：筛 `external_id` 非空、`status ≠ 2`、`pending_settlement = 0`、`is_published = 1` 的已结束比赛，向 provider 拉结果；命中则写 `result`/`winner`、置 `pending_settlement = 1`，经**后台任务队列** `SendUserNotice` 通知管理员：「比赛X 结果就绪：NAVI 2:1 FaZe，确认结算 X」（带 `确认结算 X` 按钮）。
- `确认结算 <id>` —— 读暂存的 winner/result，把 `winner`(1/2) 映射成 `SettleMatch` 期望的 `"team1"/"team2"` 字符串，调**现有 `SettleMatch(matchId, winner, result)`**（status→2 + 事务化派彩），成功后清 `pending_settlement = 0`。**派彩逻辑零改动**。
- **仅 `team1_win/team2_win` 自动预填**。含 `score`/`mvp` 选项的比赛：不预填，通知里标「需人工结算」，保持现有人工 `结算比赛` 路径。标 `TODO(debt)`。
- 可选 `忽略结算 <id>` —— 管理员判定自动拉到的结果有误时清 `pending_settlement` 并转纯人工（本期可作为增强，先留接口）。

### 3.5 触发器

**`EsportsSyncHostedService : BackgroundService`**（注册为 Hosted Service，singleton）：

- 定时循环：赛程间隔较长（如 30 min），结果间隔较短（如 5 min）。每轮**创建 DI scope** 取 scoped 的 `EsportsSyncService`/`SQLHelper`（precedent：`QQBotWebSocketService` 每次 dispatch 开 scope）。
- 每轮先查 `CandyBotFunctionConfig.赛事自动化` 开关，**关则 no-op**。
- 整轮包 `try/catch`：provider 宕机或异常只 `log` 警告、跳过本轮，**不崩溃服务**。

**`立即同步赛事` 指令**（兜底）—— 管理员触发，调用同一份 `SyncSchedulesAsync`/`SyncResultsAsync` 核心方法。

### 3.6 配置与功能开关

- **`PandaScoreConfig`**（单例，仿 `TencentDocsConfig`）：`ApiKey`（user-secrets/环境变量，**不进 appsettings**）、`BaseUrl`、`GameSlug`（如 `csgo`/`cs2`）、`SchedulePollInterval`、`ResultPollInterval`。运行时部分经 `PluginConfig` 持久化。
- **`CandyBotFunctionConfig` 增开关 `赛事自动化`**（聊天指令 `开启赛事自动化`/`关闭赛事自动化`），**默认关**（暗发布，灰度可控）。与现有 `赛事系统`/`赛事只读` 开关并列。

## 4. 数据层变更（`scripts/betting.sql` 追加 PATCH）

```sql
-- 赛事自动化：外部源锚点 + 草稿可见性
ALTER TABLE `betting_events`
    ADD COLUMN `external_id` VARCHAR(64) NULL COMMENT '外部数据源赛事ID，手工创建为NULL',
    ADD COLUMN `is_published` TINYINT(1) NOT NULL DEFAULT 1 COMMENT '是否对用户可见：0=草稿，1=已发布';

ALTER TABLE `betting_matches`
    ADD COLUMN `external_id` VARCHAR(64) NULL COMMENT '外部数据源比赛ID，手工创建为NULL',
    ADD COLUMN `is_published` TINYINT(1) NOT NULL DEFAULT 1 COMMENT '是否对用户可见：0=草稿，1=已发布',
    ADD COLUMN `pending_settlement` TINYINT(1) NOT NULL DEFAULT 0 COMMENT '结果已预填待管理员确认结算：0=否，1=是';

-- 幂等对账锚点
CREATE INDEX `idx_events_external_id` ON `betting_events` (`external_id`);
CREATE INDEX `idx_matches_external_id` ON `betting_matches` (`external_id`);
```

- `external_id` 为幂等键：sync 重复运行不产生重复记录。
- 现有数据 `external_id = NULL`、`is_published = 1`、`pending_settlement = 0`，对存量行为零影响。
- SQLite 后端需在 `forza.sql`/对应 SQLite 脚本等价位置同步加列（注意 SQLite 的 `ALTER TABLE ADD COLUMN` 语法限制，逐列添加；具体在实施计划中处理双后端脚本）。

## 5. 错误处理

| 失败点 | 处理 |
|--------|------|
| 单场 upsert / 单条结果失败 | 独立 `try/catch`，`log` 后继续，整批不中断 |
| provider HTTP 失败 / 限流 | 整轮 `try/catch`，跳过本轮，`log` 警告，服务不崩；`IMemoryCache` 缓解限流 |
| 重复运行（幂等） | `external_id` 锚点 check-before-insert，不产生重复 |
| 自动拉到的结果有误 | `pending_settlement` 暂存态，未确认不派彩；管理员可改用人工 `结算比赛` |
| 结算派彩本身 | 完全走现有 `SettleMatch` 事务（`NewTransaction`/`Commit`/`Rollback` + `FOR UPDATE` 行锁），不改动 |

## 6. 测试策略

**单元测试（`CandyBot.Tests`，无 DB）**

- PandaScore JSON → DTO 映射（纯函数）。
- 赔率预填、`available_options` 选项闸、`winner`(1/2)→`"team1"/"team2"` 映射等纯逻辑。
- 用 `FakeEsportsDataProvider` 驱动可分离的核心逻辑。

**集成测试（`CandyBot.IntegrationTests`，Testcontainers MySQL）**

- **幂等**：同一批 `SyncSchedulesAsync` 连跑两次 → 记录数不变、无重复。
- **草稿不可见**：`is_published = 0` 的比赛不出现在 `GetEventsOverview`/`GetAllMatches`/`GetEventDetail`。
- **发布翻可见**：`发布比赛` 后比赛进入用户查询，父赛事一并可见。
- **确认结算**：`SyncResultsAsync` 预填 → `确认结算` → `SettleMatch` 确实把 `status` 置 2 并按 `odds_at_bet` 派彩、用户可 `预测领奖`。
- 对齐现有 `BettingServiceTests` 写法。

**行为变更测试（P1）**

- 用户侧四个查询的 `is_published = 1` 过滤，确保草稿不泄露、存量已发布数据照常可见。

## 7. 分期建议（MVP first）

1. **阶段一（抓取入草 MVP）**：§3.1 适配器 + §3.2 upsert + §4 schema + §3.3 草稿发布 + `is_published` 过滤与测试。用户能看到管理员一键发布的真实赛程，下注链路复用现有逻辑。
2. **阶段二（结果自动化）**：§3.4 结果预填 + `确认结算` + §3.5 定时服务 + §3.6 开关。
3. **阶段三（增强，TODO(debt)）**：`score`/`mvp` 自动结算、多源、精细冲突合并、`忽略结算`。

## 8. 受影响文件清单（预估）

- 新增：`Services/Esports/IEsportsDataProvider.cs`、`PandaScoreProvider.cs`、`FakeEsportsDataProvider.cs`、DTO；`Services/Esports/EsportsSyncService.cs`、`EsportsSyncHostedService.cs`；`Utilities/PandaScoreConfig.cs`；`Services/BotMessageHandler/EsportsSyncInputHandler.cs`（或并入 `BettingInputHandler`）。
- 修改：`Services/BettingService.cs`（用户侧查询加 `is_published` 过滤）；`Utilities/CandyBotFunctionConfig.cs`（加 `赛事自动化` 开关）；`scripts/betting.sql` + SQLite 等价脚本；`Program.cs`（注册 provider/sync service/hosted service/config）；`Models/BettingModels.cs`（如需新字段映射）；`Controllers/BettingController.cs`（如需对外暴露 `is_published`/草稿过滤）。
- 测试：`CandyBot.Tests` 新增映射/逻辑单测；`CandyBot.IntegrationTests/Betting` 新增同步与可见性集成测试。
