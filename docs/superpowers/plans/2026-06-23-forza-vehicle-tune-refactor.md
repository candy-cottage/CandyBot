# Forza 车辆表重构（issue #21）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务执行本计划。所有步骤用 checkbox（`- [ ]`）跟踪。
> **语言约定**：本仓库所有代码注释、日志、面向用户字符串一律中文。测试名读起来像规格（中文）。

**Goal:** 把单一 `Vehicles` 表（车型+调校混合、全量清空重灌同步）拆成 **车型表 / 调校表 / 调校缓存表**，同步改为「按来源（fileId）幂等协调」，并修复多 DocsID 互相覆盖的现网数据丢失 bug。

**Architecture:** 三表 = `Vehicles`(车型元数据) + `VehicleTunes`(调校，FK→车型) + `VehicleTuneCache`(待应用更新快照，无 FK)。同步器按 `SourceIdentity`（源内容派生、不可变、**含车型 ModelKey**）+ `fileId` 协调每个来源自己的调校；同一身份组内**按源指纹配对**（内容驱动、对源重排免疫）：命中且非人手维护→就地更新；命中且人手维护→只在源指纹变化时写缓存并置「有更新」；未命中且来源为完整快照→人手维护置「已过时」、非人手维护删除。车型只增不删。手动同步走专用 actor 感知服务，不再复用计划任务名。

**Tech Stack:** C# 13 / .NET 10、ASP.NET Core、`SQLHelper`（MySQL 生产 / SQLite 兼容）、xUnit + FluentAssertions + NSubstitute（单元）、Testcontainers `mysql:8.0`（集成）。

## 评审修订记录（v2，5 路对抗评审后）

本计划经算法/迁移/双后端/覆盖/测试 5 路对抗评审，修订了 5 个 P0 与多个 P1。**执行前请先确认头号修订**：

1. **【P0·改动用户决策】SourceIdentity 必须含车型 ModelKey。** 真实表取证：仅用 issue 机制1 的 6 字段（不含车型），单一来源内有 **14 个碰撞组、约 47 行不同的车**共享同一身份（如 S1-800公路 一个身份命中 8 辆不同车）。这会让协调把 A 车调校配到 B 车 → 静默改错/删错（正是本重构要消灭的 bug 的同构变体）。**你此前选的「严格 6 字段」是针对「是否加 SurfaceCondition/Strength」这一轴——车型本不在其中、属遗漏。** 现 `SourceIdentity = ModelKey | 6 个调校判别字段 + fileId`，残余碰撞仅剩「同一辆车、6 字段全同、仅干/湿或强度不同」的同车孪生（罕见、局部），由「按指纹配对 + SourceIdentitySeq 存储消歧」处理。仍**不**把 SurfaceCondition/Strength 纳入身份（尊重你的决策），它们进指纹。
2. **【P0】D3 分隔符漏了全角逗号「，」**：真实调校列 6 个单元格用「，」分隔多调校师。已加入分隔符集合（换行 / 半角逗号 / 全角逗号 / 顿号）。
3. **【P0】迁移「假事务」**：MySQL DDL 隐式提交 + `MySQLHelper.Execute` 在外层事务内吞异常不回滚不冒泡（已核实 `MySQLHelper.cs:178-184`）→ 回填残缺却被盖章 v2。已改为「会抛异常的执行包装 + 回填可重放 + 置标记位前强制 count 校验」。
4. **【P0】迁移再入守卫盲点**：以「备份表存在」当「回填完成」会把半成品锁死为 v2。已改为以「回填行数 == 备份行数」判定，并让回填从备份幂等重放。
5. **【P0】Task 13「渲染零改动」不成立**：JOIN 必须把 `PerformancePreference AS Attribute`、`TuneRaw AS Tune` 别名回旧名、并 table-qualify 所有 WHERE 列，否则属性/调校行静默消失。已写明显式别名 JOIN，并把 `ForzaCardRenderingTests` 当回归金丝雀。
6. **【P1】配对算法/接口断层/FK 级联测试/指纹冻结/旧测试改写**等已在对应任务补全（见各任务）。

## Global Constraints

- 目标框架 `net10.0`；C# 13 惯用法（主构造函数、集合表达式 `[...]`、`partial` 类、`GeneratedRegex`）；`Nullable` + `ImplicitUsings` 已启用。
- 根命名空间 `CandyCottage.CandyBot`；程序集名 `CandyBot`。
- 新建/迁移后，**全新建库（forza.sql）与存量旧库（迁移）最终 schema 必须逐表逐列逐索引一致**。为杜绝漂移，**三表 DDL 抽到单一来源**（`scripts/forza.sql` 为唯一真相；迁移代码读取同一脚本/同一常量，不另写一份 DDL）。
- 新表沿用 Forza 既有风格：**PascalCase 列名 + 表级 `utf8mb4_unicode_ci`**；但 `ModelKey`、`SourceIdentity` 两个**匹配键列单列 `COLLATE utf8mb4_bin`**，使 DB 唯一性判定与 C# 归一化后的字符串相等严格对齐（避免 unicode_ci 折叠与 C# `Normalize` 折叠不一致导致迁移撞键）。
- 迁移/缓存判定一律走 `information_schema` 存在性探测 + 一个 PluginConfig 标记位 `('forza','schema_version')='2'` 作「回填已完成」守卫（列存在性不足以判定结构性迁移完成）；**置标记位前必须通过回填 count 校验**。
- 注入抽象 `SQLHelper`，绝不注入 `MySQLHelper`/`SQLiteHelper`（除初始化期类型判断）。
- 新表 DDL 仅 MySQL；SQLite 不做新三表 DDL 兜底。**取证脚注**：Forza 在 SQLite 上**本就不可用**——`系统初始化`在 `betting.sql`（纯 MySQL 方言）即整事务回滚，`Vehicles` 从未在 SQLite 建成；集成测试亦为 MySQL-only（Testcontainers）。故 MySQL-only 新三表与现状一致，**非回归**（见 `SQLTableInitializeService.cs:29-40`）。
- 行为变更（同步语义、指令拆分、{id} 语义、模型拆分）按本仓 review 政策视为 **P1**：核心逻辑必须配套测试。

## 锁定决策（issue D1–D4 + 澄清 + 评审修订）

| 项 | 决策 | 依据 |
|----|------|------|
| **D1** SourceIdentity 含行号？ | **否**，纯内容派生；同身份组内**按源指纹配对**（非按解析序），对源重排免疫 | issue D1 + 评审修订 |
| **D2** 「完整快照」判据 | **保守版**：所有 `LooksLikeForzaClassSheet` 的 sheet 都成功解析出 ≥1 行、无 sheet 因缺表头被跳过、无诊断失败。**不设行数比例阈值**（留 `// TODO(debt):` 钩子） | issue D2 |
| **D3** Tuner 提取规则 | 见「核心算法 §2」。分隔符=**换行 / 半角逗号 / 全角逗号「，」/ 顿号「、」**；id↔关键字边界是**括号**不是空格（id 含内部空格/战队前缀）；`[681478665]` 整体是 id | 真实表取证 + 评审修订 |
| **D4** 旧数据 provenance | 旧 `Vehicles` 每行回填为 1 条调校，`IsManual=1`、`SourceFileId='migration'`、`SourceIdentity=''`（排除出协调），状态`正常` | issue D4 |
| **匹配键字段集** | `SourceIdentity = ModelKey \| 等级 \| 比赛类型 \| 驱动 \| 性能偏好 \| 胎面 \| 调校师 + fileId`。**车型必含**（修正 issue 机制1 的遗漏）；6 个调校判别字段严格不含 SurfaceCondition/Strength（尊重用户拍板，二者进指纹） | 评审 P0 + 用户拍板 |
| 旧表处理 | 迁移重命名 `Vehicles`→`Vehicles_legacy_v1` 保留备份，人工核对后手动 DROP | 用户拍板 |
| 表命名 | 车型=`Vehicles`；调校=`VehicleTunes`；缓存=`VehicleTuneCache` | 一致性 |
| 性能偏好 | 新列名 `PerformancePreference`，导入仍映射表头「属性」；**查询 JOIN 时 SELECT 别名回 `Attribute`** 以保渲染契约 | issue 决策 6 + 评审 P0 |
| 驱动 | 表头实际是「**传动**」，列名 `Drivetrain` | 取证 |
| 调校师列 `Tuner` | **仅供 identity 匹配**（TunerKey 排序后归一化）；**面向用户展示一律用 `TuneRaw`**（保留源书写顺序/主次） | 评审 |

## Non-Goals（本次不做）

- 赛道体系（`Maps`/`MapGroups`/`MapGroupMaps`，即 CLAUDE.md 误称的 Tracks/TrackRecords）不重构；仅与车辆代码并存于同文件，改车辆时不碰赛道。
- 赔率、跨文档调校聚合（仅「展示来源、不静默去重」）。
- SQLite 新三表 DDL 及跨后端初始化重构（应另立独立 issue）。
- D2 行数比例阈值（先上 100% 完整判据，留钩子）。

## 核心算法（实现前定死，全部纯函数，独立单元测试）

新建 `Utilities/ForzaTuneIdentity.cs`（静态类 `ForzaTuneIdentity`）。**单一归一化真理来源**：迁移回填、同步、`新增调校`、查询都调它，保证迁移键 == 导入键。

### §1 归一化 `Normalize(string? raw) -> string`
`null/空 -> ""`；否则 `Trim → 内部连续空白折叠为单个半角空格 → 全角转半角(NFKC) → ToLowerInvariant`。用于 identity 各分量与 ModelKey 各分量。

### §2 调校列解析 `ParseTuners(string? cell) -> IReadOnlyList<TunerToken>`（D3 取证规则，**含评审修订**）
`record TunerToken(string Id, string IdNormalized, string? Keyword)`。顺序敏感：
1. `null/空` → 空列表。
2. 整串若匹配 `^\s*\[\s*[0-9][0-9\s]*\]\s*$`（纯数字、容忍内部空格的方括号整串）→ 单 token：`Id=` 方括号内数字串（去内部空白）、`Keyword=null`。
3. 否则按 **分隔符集合 `[\n，,、]`**（换行 / 全角逗号 / 半角逗号 / 顿号）切分为多 token。（`/`、`&` 不切分——未观测；且不误伤含空格的 id。）
4. 每 token 用 `GeneratedRegex` `[\[\(（][^\]\)）]*[\]\)）]`（宽松匹配任意一对方/圆括号，容忍全半角开闭混配）：
   - `Keyword =` 最后一个括号组的内容（去括号）；无括号组则 `null`。
   - `Id =` 把 token 内**所有**括号组移除后的剩余串，再 `折叠空白 + Trim`（**保留内部空格**，如 `OSM Cinnabar`、`PTW KDEdward`；正确处理一 token 多括号组如 `SakuraMxSPD[new] （半热熔）` → Id=`SakuraMxSPD`、Keyword=`半热熔`）。
5. `IdNormalized = Normalize(Id)`；丢弃 `IdNormalized` 为空的 token。
6. 按 `IdNormalized` 顺序去重。
- **表头匹配不收紧**：现 `VehicleSheetParser` 用「列文本包含『调校』」匹配列，已覆盖真实表 ≥4 种表头变体（`调校[调校师ID+关键字]`/`调校[调教师Xbox ID+调教名称]`/`调校[调校师XboxID+关键词]`/`调校[调教师ID+调教名称关键字]`）；**保持包含匹配，勿穷举全文**。`ParseTuners` 不依赖表头。

### §3 调校师匹配分量 `TunerKey(IReadOnlyList<TunerToken>) -> string`
取所有 `IdNormalized`，**排序后** `` 连接（使 `A，B` 与 `B\nA` 等价，identity 对书写顺序稳定）。**仅用于匹配/identity；展示走 TuneRaw。**

### §4 SourceIdentity `BuildSourceIdentity(VehicleTuneRequest tune, string modelKey, string fileId) -> string`
```
join("", [ modelKey,                       // ← 评审 P0：含车型
           Normalize(Class), Normalize(MatchType), Normalize(Drivetrain),
           Normalize(PerformancePreference), Normalize(TireCompound),
           TunerKey(ParseTuners(TuneRaw)) ]) + "" + fileId
```
**冻结**：写库时算一次存 `SourceIdentity` 列，永不重算（管理员改显示值不触发重算，机制1）。哨兵迁移行 `SourceIdentity=''`。

### §5 SourceFingerprint `BuildFingerprint(VehicleTuneRequest tune, string modelKey) -> string`
对**全部源派生展示字段**做 SHA-256(Hex)：`modelKey + identity 六分量 + SurfaceCondition + Strength + TuneRaw + Remarks + LapTime`。机制3：仅当当前源指纹 ≠ 已存指纹时才判「源真变了」。含 `modelKey`+`SurfaceCondition`+`Strength` 使「同身份组内孪生」指纹不同 → 可按指纹稳定配对。

### §6 ModelKey `BuildModelKey(mfr, model, year) -> string`
`Normalize(mfr) + "" + Normalize(model) + "" + Normalize(year ?? "")`。Year NULL/"" 归一同串（否则 MySQL 唯一键判空年份同名车型不冲突）。列存 `utf8mb4_bin`。

### §7 完整快照判据 `IsCompleteSnapshot(parsedClassSheets, skippedClassSheets, emptyClassSheets, diagnosticFailureCount) -> bool`（D2 保守版）
```
return skippedClassSheets 为空 && emptyClassSheets 为空 && diagnosticFailureCount == 0;
// TODO(debt): 后续加「本次行数 ≥ 上次完整解析行数 * 阈值」比例闸门
```
仅 `true` 才允许「未命中→已过时/删除」；`false` 仅 upsert（命中更新+新增），绝不删（机制2）。

### §8 同身份组配对（Reconcile 内核，评审 P0/P1）
匹配键 = `(SourceFileId, SourceIdentity)`；一个键可对应组内多行（同车孪生/精确重复）。配对：
1. 取既有组 `E`（DB 中该 fileId+identity 的行）与解析组 `P`。
2. **先按 `SourceFingerprint` 相等配对** `E↔P`（内容驱动，对源重排免疫；同车干/湿孪生指纹不同 → 各自正确配对）。
3. 剩余未配对的 `P` → 新增（`SourceIdentitySeq = 该组当前 MAX(seq)+1`，仅作存储唯一性）。
4. 剩余未配对的 `E` → 视为「未命中」，按 §7/机制2 在完整快照下删除（非人手维护）/置已过时（人手维护）。
- `SourceIdentitySeq` **不参与匹配**，只在 `uq(SourceFileId,SourceIdentity,SourceIdentitySeq)` 上保证精确重复行可共存。

## 数据模型（forza.sql 新增；迁移产物须逐列一致）

### 表 `Vehicles`（车型，重塑）
```sql
CREATE TABLE IF NOT EXISTS `Vehicles` (
  `Id`            INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `Manufacturer`  VARCHAR(100) NOT NULL,
  `Model`         VARCHAR(100) NOT NULL,
  `Year`          VARCHAR(20)  NULL,
  `ModelKey`      VARCHAR(255) NOT NULL COLLATE utf8mb4_bin,    -- Normalize(制造商|型号|年份)
  `IsManual`      TINYINT(1)   NOT NULL DEFAULT 0,
  `LastUpdatedUserId` BIGINT   NOT NULL DEFAULT 0,
  `LastUpdatedAt` DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `CreatedAt`     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt`     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `uq_modelkey` (`ModelKey`),
  KEY `idx_manufacturer_model` (`Manufacturer`,`Model`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

### 表 `VehicleTunes`（调校）
```sql
CREATE TABLE IF NOT EXISTS `VehicleTunes` (
  `Id`               INT UNSIGNED NOT NULL AUTO_INCREMENT,    -- TuneId
  `VehicleId`        INT UNSIGNED NOT NULL,                   -- FK -> Vehicles.Id
  `Class`            VARCHAR(20)  NOT NULL,
  `MatchType`        VARCHAR(20)  NOT NULL,
  `Drivetrain`           VARCHAR(20)  NULL,
  `PerformancePreference` VARCHAR(20) NULL,                   -- 旧 Attribute/「属性」
  `TireCompound`     VARCHAR(50)  NULL,
  `SurfaceCondition` VARCHAR(20)  NULL,                       -- 干/湿地（仅公路；进指纹不进身份）
  `Strength`         VARCHAR(20)  NULL,                       -- 进指纹不进身份
  `Tuner`            VARCHAR(255) NULL,                       -- TunerKey；仅匹配，勿展示
  `TuneRaw`          VARCHAR(255) NULL,                       -- 调校原文（展示）
  `Remarks`          VARCHAR(255) NULL,
  `LapTime`          VARCHAR(255) NULL,
  `ExtData`          TEXT         NULL,
  `SourceFileId`     VARCHAR(128) NOT NULL,                   -- 哨兵='migration'
  `SourceSheet`      VARCHAR(128) NULL,
  `SourceRow`        INT          NULL,
  `SourceDisplayName` VARCHAR(255) NULL,
  `SourceIdentity`   VARCHAR(512) NOT NULL COLLATE utf8mb4_bin, -- 冻结；哨兵=''
  `SourceIdentitySeq` INT UNSIGNED NOT NULL DEFAULT 0,        -- 仅存储消歧，不参与匹配
  `SourceFingerprint` CHAR(64)    NULL,
  `IsManual`         TINYINT(1)   NOT NULL DEFAULT 0,
  `TuneStatus`       VARCHAR(10)  NOT NULL DEFAULT '正常',     -- 正常/有更新/已过时
  `LastUpdatedUserId` BIGINT      NOT NULL DEFAULT 0,
  `LastUpdatedAt`    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `CreatedAt`        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  `UpdatedAt`        DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  KEY `idx_vehicle` (`VehicleId`),
  KEY `idx_class_match` (`Class`,`MatchType`),
  KEY `idx_source` (`SourceFileId`),
  KEY `idx_status` (`TuneStatus`),
  UNIQUE KEY `uq_source_identity` (`SourceFileId`,`SourceIdentity`,`SourceIdentitySeq`),
  CONSTRAINT `fk_tune_vehicle` FOREIGN KEY (`VehicleId`) REFERENCES `Vehicles`(`Id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

### 表 `VehicleTuneCache`（缓存，无 FK）
与 `VehicleTunes` 同业务字段，外加 `TuneId`（影子的 live 调校）：
```sql
CREATE TABLE IF NOT EXISTS `VehicleTuneCache` (
  `Id`        INT UNSIGNED NOT NULL AUTO_INCREMENT,
  `TuneId`    INT UNSIGNED NOT NULL,
  `VehicleId` INT UNSIGNED NOT NULL,
  `Class` VARCHAR(20) NOT NULL, `MatchType` VARCHAR(20) NOT NULL,
  `Drivetrain` VARCHAR(20) NULL, `PerformancePreference` VARCHAR(20) NULL,
  `TireCompound` VARCHAR(50) NULL, `SurfaceCondition` VARCHAR(20) NULL, `Strength` VARCHAR(20) NULL,
  `Tuner` VARCHAR(255) NULL, `TuneRaw` VARCHAR(255) NULL,
  `Remarks` VARCHAR(255) NULL, `LapTime` VARCHAR(255) NULL, `ExtData` TEXT NULL,
  `SourceFileId` VARCHAR(128) NOT NULL, `SourceSheet` VARCHAR(128) NULL, `SourceRow` INT NULL,
  `SourceDisplayName` VARCHAR(255) NULL, `SourceIdentity` VARCHAR(512) NOT NULL COLLATE utf8mb4_bin,
  `SourceFingerprint` CHAR(64) NULL,
  `CapturedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `uq_cache_tune` (`TuneId`),
  KEY `idx_cache_source` (`SourceFileId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

## 迁移 `EnsureForzaTuneSchema()`（MySQL only，幂等可重放，C# 内回填）

位置：`SQLTableInitializeService.cs` 新增 `public void EnsureForzaTuneSchema()`；在 `SystemInitializeService.cs` 的 `using` 块内、`EnsureForzaMapSchema()` 调用之后（当前 `:28` 与 `:29` 之间，**仍在 scope 内**）追加 `sqlInitService.EnsureForzaTuneSchema();`。

**关键纠正（评审 P0×2）**：MySQL `RENAME/CREATE/DROP` 隐式提交，不可被 `NewTransaction()` 回滚；`MySQLHelper.Execute` 在外层事务内吞异常不回滚不冒泡（`MySQLHelper.cs:178-184`）。故：(a) 所有回填 INSERT 走**会抛异常的包装** `ExecuteOrThrow`（执行后 `if (sql.Result == SQLResult.SQLError) throw sql.LastException;`，仿 `ExecuteSchemaStatement`）；(b) 不依赖事务回滚 DDL；(c) **置 `schema_version='2'` 前必须通过 count 校验**；(d) 回填以 `Vehicles_legacy_v1` 为「可重放的源」，幂等。

```
if (sql is not MySQLHelper) return;                                  // SQLite 不迁移
if (PluginConfig('forza','schema_version') == "2") return;          // 已完成

// 0) 前置守卫：Vehicles 不存在 => 尚未 系统初始化，建表交给 forza.sql，避免新 FK 因父表缺失建表失败致启动崩溃
if (!MySqlTableExists("Vehicles")) return;                          // 仿 EnsureMySqlBettingAutomationSchema:168

bool legacyShape = MySqlColumnExists("Vehicles","Tune");            // 旧合并表特征列
bool isV2New     = MySqlColumnExists("Vehicles","ModelKey");        // 新车型表特征列

if (legacyShape) {                                                  // 存量旧库：需结构迁移 + 回填
    ExecuteSchemaStatement(<VehicleTunes DDL>);                     // 先建子/缓存表（CREATE IF NOT EXISTS）
    ExecuteSchemaStatement(<VehicleTuneCache DDL>);
    if (!MySqlTableExists("Vehicles_legacy_v1"))
        ExecuteSchemaStatement("RENAME TABLE `Vehicles` TO `Vehicles_legacy_v1`");  // 隐式提交，不可回滚
    if (!MySqlColumnExists("Vehicles","ModelKey") && !MySqlTableExists("Vehicles") /*被改名后*/ )
        ExecuteSchemaStatement(<新 Vehicles 车型表 DDL>);            // CREATE IF NOT EXISTS

    // —— 幂等可重放回填：以 legacy 为源，先清空再重灌（legacy 是完整真相）——
    int legacyCount = SELECT COUNT(*) FROM `Vehicles_legacy_v1`;
    int tuneMig     = SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration';
    if (tuneMig != legacyCount) {                                  // 回填缺失/残缺 => 重放
        ExecuteOrThrow("DELETE FROM `VehicleTunes` WHERE SourceFileId='migration'"); // 清半成品
        // 车型：去重 ModelKey；回读 Id（不信 LastInsertId）
        var modelIdByKey = {};
        foreach (row in SELECT * FROM `Vehicles_legacy_v1`) {
            key = ForzaTuneIdentity.BuildModelKey(Manufacturer,Model,Year);
            if (!modelIdByKey.has(key)) {
                ExecuteOrThrow("INSERT INTO `Vehicles`(Manufacturer,Model,Year,ModelKey,IsManual)
                                VALUES(@m,@mo,@y,@key,1)
                                ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id)");      // 既有则取回 Id
                modelIdByKey[key] = SELECT Id FROM `Vehicles` WHERE ModelKey=@key;    // 回读，最稳
            }
        }
        int seq = 0;
        foreach (row in SELECT * FROM `Vehicles_legacy_v1`) {
            vid = modelIdByKey[BuildModelKey(...)];
            ExecuteOrThrow("INSERT INTO `VehicleTunes`(VehicleId,Class,MatchType,Drivetrain,
              PerformancePreference,TireCompound,SurfaceCondition,Strength,Tuner,TuneRaw,Remarks,
              LapTime,ExtData,SourceFileId,SourceIdentity,SourceIdentitySeq,IsManual,TuneStatus,LastUpdatedUserId)
              VALUES(@vid,<Class>,<MatchType>,<Drivetrain>,<Attribute→PerfPref>,@tire,@surf,@str,
                     NULL,<旧 Tune 原文>,@rem,@lap,@ext,'migration','',@seq,1,'正常',0)");  // 哨兵 identity=''；seq 递增
            seq++;
        }
    }
    // count/碰撞校验（不过即 throw，绝不置标记位）
    assert SELECT COUNT(*) FROM `VehicleTunes` WHERE SourceFileId='migration' == legacyCount;
    assert SELECT COUNT(*) FROM `Vehicles` == COUNT(DISTINCT ModelKey from legacy);
}
else if (!isV2New) {
    // 既非旧合并表、又无 ModelKey：异常半建状态，建齐三表（CREATE IF NOT EXISTS）
    ExecuteSchemaStatement(<VehicleTunes DDL>); ExecuteSchemaStatement(<VehicleTuneCache DDL>);
}

PluginConfig('forza').Add("schema_version","2"); SaveConfig();
```
- **全新库**：`forza.sql` 末尾（或 `Initialize()` 成功后）直接写 `PluginConfig('forza','schema_version')='2'`，**不依赖**会 throw 的启动方法兜底。这样全新库 `EnsureForzaTuneSchema` 首句即 return。
- **再入安全**：崩溃后重跑——`legacyShape` 若已被改名则 false，但若 `Vehicles_legacy_v1` 存在且 `tuneMig != legacyCount`，仍会进入「重放」分支重灌（DELETE migration 行 + 重插），直到 count 相等才置标记。备份表始终保命。
- **失败冒泡**：`SystemInitializeService.StartAsync` 对 Ensure*Schema 无 try/catch，迁移异常 → HostedService 启动失败（可见信号，符合预期）。
- 旧表 `Vehicles_legacy_v1` 不进 forza.sql/DataTables，人工核对后手动 DROP。

## File Structure

**新建：**
- `Utilities/ForzaTuneIdentity.cs` — 归一化/调校解析/SourceIdentity/指纹/ModelKey/完整快照/配对（纯函数）。
- `Services/ForzaSyncService.cs` — actor 感知专用同步 `SyncAsync(long actorUserId, string sourceFileId, CancellationToken)` + 按来源串行 + `Reconcile`（替代 importer 的 clear+reload 与 SystemInitializedService 的 DocsID 直驱）。
- `Models/Forza.cs` 内新增 `VehicleModelRequest`；扩展 `VehicleTuneRequest`（**含 Manufacturer/Model/Year**，解决接口断层）。

**修改：**
- `scripts/forza.sql` — 三表 DDL 替换旧 `Vehicles` 单表段（Maps 不动）；末尾写 schema_version=2（或在 Initialize 成功后写）。
- `Services/SystemServices/SQLTableInitializeService.cs` — 加 `EnsureForzaTuneSchema()` + `ExecuteOrThrow` 包装。
- `Services/SystemServices/SystemInitializeService.cs` — `EnsureForzaMapSchema()` 后追加 `EnsureForzaTuneSchema()`。
- `Services/SystemServices/SystemInitializedService.cs` — `SyncForzaWithDocs` 改为 `foreach fileId: ForzaSyncService.SyncAsync(0, fileId)`；加功能开关闸门 + actor 上报（T18）。
- `Services/ForzaService.cs` — 车型 CRUD + 调校 CRUD + JOIN 查询（别名回旧列名）；删旧单表 SQL。
- `Services/ForzaSheetImporter.cs` — **职责收窄为「拉表 + 解析 → 产出 (parsed, IsCompleteSnapshot)」**，去掉写库/`DELETE FROM Vehicles`/事务；由 `ForzaSyncService` 负责开事务 + `Reconcile`。
- `Models/ForzaSheetImport.cs` — `ForzaSheetSyncReport.InsertedVehicleCount` 改名/补 `InsertedTuneCount`（语义=调校数）。
- `Services/BotMessageHandler/ForzaHorizonInputHandler.cs` — 查询 JOIN；指令拆分；`待更新调校表`/`已过时调校表`；编辑清缓存；同步分支改 actor 感知（去 `RunTaskNow(TaskName_12ClockSyncCar)`）。
- `Controllers/ForzaController.cs` — VehicleId vs TuneId 语义、响应形状、`/match`。
- `Program.cs` — 注册 `ForzaSyncService`（scoped）。
- `tests/CandyBot.IntegrationTests/Infrastructure/MySqlFixture.cs` — `DataTables` 加 `VehicleTunes`/`VehicleTuneCache`（**TRUNCATE 不级联，必须各自列出**）。
- `tests/CandyBot.IntegrationTests/Forza/ForzaServiceTests.cs` — 改写裸 `WHERE Class/MatchType` 单表 SQL → JOIN；`SeedVehicle` 返回确定 VehicleId（out 参，不靠 LastInsertId）；新增 FK 级联删除测试。
- `tests/CandyBot.IntegrationTests/Forza/ForzaSheetImporterTests.cs` — 157 计数从 `Vehicles`→`VehicleTunes`；空解析拒绝路径断言**车型+调校均不动**。
- `tests/CandyBot.Tests/Services/ForzaCardRenderingTests.cs` — **作为别名回旧列名的回归金丝雀**（保持绿=别名正确）。
- `tests/CandyBot.Tests/Services/VehicleSheetParserTests.cs` — 视 `VehicleRequest` 兼容别名是否保留决定是否改。
- `Services/BotMessageHandler/ShowHelpInputHandler.cs` + `CLAUDE.md` — 帮助/文档同步（含订正 Tracks→Maps）。

---

# 任务（依赖序；每任务自带测试 + 提交）

## P0 — Schema / 纯算法 / 迁移 / 模型

### Task 1：纯算法 `ForzaTuneIdentity`（归一化 + 调校解析）
**Files:** Create `Utilities/ForzaTuneIdentity.cs`；Test `tests/CandyBot.Tests/Services/ForzaTuneIdentityTests.cs`
**Produces:** `Normalize`；`record TunerToken(string Id,string IdNormalized,string? Keyword)`；`ParseTuners`；`TunerKey`
- [ ] **Step 1: 写失败测试**（真实取证样例，**含全角逗号/多括号/含空格 id/纯数字**）
```csharp
[Theory]
[InlineData("LyricismRain", new[]{"lyricismrain"})]
[InlineData("K1Z bala, LetzeLU", new[]{"k1z bala","letzelu"})]
[InlineData("CybrexzzZ，Cirno1X[META+]", new[]{"cybrexzzz","cirno1x"})]                 // 全角逗号
[InlineData("Mustuff124，CybrexzzZ[THE WHEELCHAIR]，Cirno1X[META+]", new[]{"mustuff124","cybrexzzz","cirno1x"})]
[InlineData("LetzeLU[AWD Road]\nnyasmowisher[AWD grip meta V2]", new[]{"letzelu","nyasmowisher"})]
[InlineData("OSM Cinnabar[V2.1]", new[]{"osm cinnabar"})]                                // 含空格 id + 方括号关键字
[InlineData("SakuraMxSPD[new] （半热熔）", new[]{"sakuramxspd"})]                         // 一 token 多括号组
[InlineData("[681478665]", new[]{"681478665"})]
[InlineData(" PTW KDEdward", new[]{"ptw kdedward"})]
[InlineData("Shinnonsuke[?]", new[]{"shinnonsuke"})]
public void ParseTuners_提取调校师并归一化(string cell, string[] expectedNormIds) =>
    ForzaTuneIdentity.ParseTuners(cell).Select(t => t.IdNormalized).Should().Equal(expectedNormIds);

[Fact] public void ParseTuners_书写顺序经TunerKey后等价() {
    var a = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners("Cirno1X，CybrexzzZ"));
    var b = ForzaTuneIdentity.TunerKey(ForzaTuneIdentity.ParseTuners("CybrexzzZ，Cirno1X"));
    a.Should().Be(b);
}
[Fact] public void ParseTuners_保留括号关键字而不并入Id() {
    var t = ForzaTuneIdentity.ParseTuners("Dack86（全热熔）").Single();
    t.Id.Should().Be("Dack86"); t.Keyword.Should().Be("全热熔");
}
```
- [ ] **Step 2: 运行→失败** `dotnet test tests/CandyBot.Tests --filter ForzaTuneIdentityTests`（类型不存在）。
- [ ] **Step 3: 实现**（§1/§2/§3；`GeneratedRegex` 实现整串数字方括号与括号组移除）。
- [ ] **Step 4: 运行→通过**。
- [ ] **Step 5: 提交** `feat(forza): 调校师解析与归一化纯函数（D3 取证规则，含全角逗号/多括号）`

### Task 2：纯算法 SourceIdentity / 指纹 / ModelKey
**Files:** Modify `Utilities/ForzaTuneIdentity.cs`；同测试文件
**Consumes:** Task 1。**Produces:** `BuildModelKey`、`BuildSourceIdentity(tune, modelKey, fileId)`、`BuildFingerprint(tune, modelKey)`
- [ ] **Step 1: 写失败测试**：
  - 同 modelKey、6 字段相同、仅 `SurfaceCondition` 不同 → **identity 相同、fingerprint 不同**（验证孪生靠指纹区分）。
  - **改车型（modelKey 变）→ identity 必变**（评审 P1：车型变更不再静默）。
  - `Year=null` 与 `""` 的 ModelKey 相同。
- [ ] **Step 2→4: 失败→实现→通过**（identity 用 `` 连接，首段为 modelKey；指纹含 modelKey）。
- [ ] **Step 5: 提交** `feat(forza): SourceIdentity（含车型）/源指纹/ModelKey 纯函数`

### Task 3：模型 record（车型/调校请求，含车型字段）
**Files:** Modify `Models/Forza.cs`
**Produces:** `VehicleModelRequest{int? Id;string? Manufacturer;string? Model;string? Year;}`；`VehicleTuneRequest{int? Id;int? VehicleId;string? Manufacturer;string? Model;string? Year;string? Class;string? MatchType;string? Drivetrain;string? PerformancePreference;string? TireCompound;string? SurfaceCondition;string? Strength;string? TuneRaw;string? Remarks;string? LapTime;string? ExtData;string? SourceFileId;string? SourceSheet;int? SourceRow;string? SourceDisplayName;bool IsManual;string? TuneStatus;long LastUpdatedUserId;}`（**含 Manufacturer/Model/Year**，使 Reconcile 能构造 VehicleModelRequest）。旧 `VehicleRequest` 保留为兼容别名。
- [ ] **Step 1: 加类型**；**Step 2: `dotnet build` 成功**；**Step 3: 提交** `feat(forza): 车型/调校请求模型（调校携带车型字段）`

### Task 4：forza.sql 新三表（全新建库）+ schema_version 标记
**Files:** Modify `scripts/forza.sql`
- [ ] **Step 1: 写失败测试**（集成）`ForzaSchemaTests.cs`：`CreateFreshDatabaseAsync` 跑四脚本后断言 `Vehicles.ModelKey`+`uq_modelkey`、`VehicleTunes.uq_source_identity` 三列 + `fk_tune_vehicle`(ON DELETE CASCADE)、`VehicleTuneCache.uq_cache_tune`；`ModelKey`/`SourceIdentity` 列 collation == `utf8mb4_bin`。
- [ ] **Step 2: 运行→失败**。
- [ ] **Step 3: 实现** 三表 DDL 替换旧 `Vehicles` 段。
- [ ] **Step 4: 通过**。**Step 5: 提交** `feat(forza): 三表 DDL（车型/调校/缓存，匹配键列 utf8mb4_bin）`

### Task 5：`EnsureForzaTuneSchema()` 迁移 + 可重放回填
**Files:** Modify `SQLTableInitializeService.cs`、`SystemInitializeService.cs`；Test `ForzaMigrationTests.cs`
**Consumes:** Task 1/2/4。**Produces:** `EnsureForzaTuneSchema()`、`ExecuteOrThrow`
- [ ] **Step 1: 写失败测试**（在 `CreateFreshDatabaseAsync` 上手建旧版 `Vehicles` 单表插 3 行：2 同车型 1 异）：
  - 调 `EnsureForzaTuneSchema()` → `Vehicles`(车型)=2、`VehicleTunes`=3 全 `SourceFileId='migration'`/`IsManual=1`/`SourceIdentity=''`、`Vehicles_legacy_v1` 存在、`schema_version=="2"`。
  - **再调一次**：无异常、行数不变（幂等）。
  - **半成品恢复**：手动删掉 1 条 migration 调校（模拟残缺）再调 → 重放回到 3 条（验证 count 校验 + 重放，而非误判已完成）。
- [ ] **Step 2→4: 失败→实现→通过**（按上「迁移」算法：前置守卫、`ExecuteOrThrow`、回读 Id、count 校验后置标记）。
- [ ] **Step 5: 提交** `feat(forza): v1→v2 迁移与历史回填（幂等可重放，置标记前强制 count 校验）`

### Task 6：测试夹具接线
**Files:** Modify `MySqlFixture.cs`
- [ ] **Step 1:** `DataTables` 加 `"VehicleTunes"`、`"VehicleTuneCache"`；注释订正为「**TRUNCATE 不触发 CASCADE，子表必须独立列出**；`FOREIGN_KEY_CHECKS=0` 仅用于绕过 TRUNCATE 父表时的 FK 拒绝」。加断言测试：`ResetDataAsync()` 后 `COUNT(VehicleTunes)==0`。
- [ ] **Step 2:** 跑 Schema/Migration 测试应绿（CRUD/importer 仍红，预期）。
- [ ] **Step 3: 提交** `test(forza): 夹具清表纳入调校/缓存表（订正级联注释）`

## P1 — CRUD + 同步协调

### Task 7：车型 CRUD（确定性返回 VehicleId）
**Files:** Modify `ForzaService.cs`；Test `ForzaServiceTests.cs`
**Produces:** `int EnsureVehicleModel(VehicleModelRequest, long actorUserId, out string error)`（按 ModelKey upsert，**回读返回 VehicleId**，不靠 LastInsertId；只增不删）；`(...) GetVehicleModelById(int)`；`bool UpdateVehicleModel(...)`；保留 `SELECT DISTINCT Manufacturer FROM Vehicles`。
- [ ] **Step 1: 写失败测试**：`EnsureVehicleModel` 同 ModelKey 两次只建 1 行返同 Id；`Year=null`/`""` 同车型；返回的 Id 经 `GetVehicleModelById` 命中正确制造商（不串行污染）。
- [ ] **Step 2→4: 失败→实现→通过**（`ON DUPLICATE KEY UPDATE Id=LAST_INSERT_ID(Id)` 或回读）。
- [ ] **Step 5: 提交** `feat(forza): 车型 upsert/查询（ModelKey 唯一，确定性返回 Id，只增不删）`

### Task 8：调校 CRUD（冻结 identity + 冻结指纹 + 清缓存）
**Files:** Modify `ForzaService.cs`；Test 同上
**Produces:** `int InsertTune(VehicleTuneRequest, int seq, out string error)`（internal，无自身事务，写 `SourceIdentity`/`SourceFingerprint`/`SourceIdentitySeq`）；`bool UpdateTune(VehicleTuneRequest, long actorUserId, out string error)`；`(...,int) DeleteTunesByIds(List<int>)`；`(...) GetTunesByVehicleId(int)`；`(...) ListTunesByStatus(string status,int page)`
- [ ] **Step 1: 写失败测试**：`UpdateTune` 改 `Drivetrain` 显示值后 → `SourceIdentity` **与** `SourceFingerprint` 两列**均不变**（二者皆源派生、冻结，仅同步路径写——评审 P2 防回归）、`TuneStatus` 回`正常`（未显式给状态时）、对应 `VehicleTuneCache` 行被删；`DeleteTunesByIds` 删后查不到。
- [ ] **Step 2→4: 失败→实现→通过**（`UpdateTune` 的 SET 列表**不含** `SourceIdentity`/`SourceFingerprint`）。
- [ ] **Step 5: 提交** `feat(forza): 调校 CRUD（编辑冻结 identity+指纹、清缓存、状态回正常）`

### Task 9：完整快照判据（纯函数）
**Files:** Modify `Utilities/ForzaTuneIdentity.cs`；Test `ForzaSyncDecisionTests.cs`
**Produces:** `IsCompleteSnapshot(parsedClassSheets, skippedClassSheets, emptyClassSheets, diagnosticFailureCount)`
- [ ] **Step 1→5:** 全解析→true；有 skipped/empty class sheet→false；诊断失败→false。`// TODO(debt):` 比例阈值。提交 `feat(forza): 完整快照判据（D2 保守版）`

### Task 10：同步协调 `ForzaSyncService.Reconcile`（含配对算法）
**Files:** Create `Services/ForzaSyncService.cs`；Test `ForzaSyncServiceTests.cs`（集成）
**Consumes:** Task 7/8/9。**Produces:** `internal ForzaSyncReport Reconcile(IReadOnlyList<VehicleTuneRequest> parsed, string fileId, bool isCompleteSnapshot, long actorUserId)`
- [ ] **Step 1: 写失败测试**（核心，对照 §8 配对）：
  - 命中非人手维护 → 就地更新、状态`正常`、用户 0。
  - 命中人手维护且指纹变 → 写缓存、status=`有更新`、**live 行显示不被覆盖**。
  - 命中人手维护且指纹不变 → 无操作（不刷「有更新」）。
  - **管理员改键后重跑同步仍命中**（矩阵 #4 的同步部分钉在此）：InsertTune→UpdateTune 改显示值→以原始源元组重跑 Reconcile→命中同一 TuneId、无新增、无过时。
  - 未命中 + 完整快照：非人手删除、人手置`已过时`；未命中 + 非完整快照：**不删不过时**。
  - **同身份组孪生**（同车、6 字段同、仅干/湿不同）：两 parsed 各按指纹配到正确既有行，无错配（评审 P0 回归）。
  - **同身份精确重复**：DB 已有 seq=0,1，parsed 仍 2 条 → 不撞唯一键。
  - 哨兵 `SourceFileId='migration'` 行不参与任何来源协调。
- [ ] **Step 2→4: 失败→实现→通过**：`SELECT ... FROM VehicleTunes WHERE SourceFileId=@fileId`（MySQL 用 `ExecuteDataSetForUpdate` 行锁）按 `(identity)` 分组；组内 §8 指纹配对；车型经 `EnsureVehicleModel`（只增）；新增传 `seq=MAX(seq)+1`。
- [ ] **Step 5: 提交** `feat(forza): 按来源协调（含车型身份匹配 + 指纹配对 + 完整快照闸门）`

### Task 11：Importer 收窄为解析；Sync 负责协调
**Files:** Modify `ForzaSheetImporter.cs`、`Models/ForzaSheetImport.cs`；Test `ForzaSheetImporterTests.cs`
**接口划清（评审 P2）**：`ForzaSheetImporter` 只「拉表 + 解析 → 产出 `(IReadOnlyList<VehicleTuneRequest> parsed, bool isCompleteSnapshot, 诊断/跳过信息)`」，**不写库、不开事务、删 `DELETE FROM Vehicles`**；`ForzaSyncService` 调它取 parsed 后开事务调 `Reconcile`。`ForzaSheetSyncReport.InsertedVehicleCount` → 语义改为调校数（改名 `InsertedTuneCount` 或注释订正）。
- [ ] **Step 1: 改/加测试**：
  - 157：`COUNT(VehicleTunes)`==157、`COUNT(Vehicles)`==去重车型数；重跑仍相等（0 增 0 删）。
  - **两来源隔离**（修现网 bug）：先 `fileId=A` 导入，再 `fileId=B` 导入；A 调校仍在。
  - **部分解析安全**：跳过一张 class sheet（非完整快照）→ 该 sheet 既有调校不被过时/删除。
  - **空解析拒绝**：解析 0 行 → 车型表与调校表**均不变**（旧 `COUNT FROM Vehicles==1` 守卫已不足，需同时断言调校行还在）。
- [ ] **Step 2→4: 失败→实现→通过**。
- [ ] **Step 5: 提交** `fix(forza): 同步改为按来源协调，去除全表清空（修多 DocsID 互相覆盖数据丢失）`

### Task 12：actor 感知专用同步入口 + 串行
**Files:** `ForzaSyncService.cs`（`Task<ForzaSyncReport> SyncAsync(long actorUserId, string sourceFileId, CancellationToken)`：调 importer 解析 → 开事务 → `Reconcile`，**按 `sourceFileId` 串行**）；`SystemInitializedService.cs`（`SyncForzaWithDocs` 改 `SyncAsync(0, fileId)`）；`Program.cs`（注册 scoped）。Test 单元。
- [ ] **Step 1: 写失败测试**：同 `fileId` 并发两次 `SyncAsync` 被串行化；`actorUserId` 进报告。
- [ ] **Step 2→4: 失败→实现→通过**（来源级锁字典）。
- [ ] **Step 5: 提交** `feat(forza): actor 感知专用同步服务，按来源串行`

## P2 — 指令面 + HTTP

### Task 13：查询 JOIN（显式别名）+ 手动同步去任务名复用
**Files:** Modify `ForzaHorizonInputHandler.cs`、`ForzaService.cs`；Test 集成 + 单元
**JOIN 契约（评审 P0）**：`GetVehicleDataTable`/分页 COUNT/`GetVehiclesByClass*` 改 `Vehicles v JOIN VehicleTunes t ON t.VehicleId=v.Id`，且：
- SELECT **别名回旧列名**：`t.PerformancePreference AS Attribute, t.TuneRaw AS Tune, t.Class, t.MatchType, t.Drivetrain, t.TireCompound, t.SurfaceCondition, t.Strength, t.Remarks, t.LapTime, v.Manufacturer, v.Model, v.Year`，`Id` 明确为 `t.Id`（TuneId，渲染卡片粒度=每调校一卡）。
- WHERE/CONCAT/REPLACE 全部 **table-qualify**：`v.Manufacturer`、`v.Model`；`GetVehiclesByClass*` 用 `t.Class`/`t.MatchType`；`SELECT DISTINCT Manufacturer FROM Vehicles` 不变（仍在车型表）。
- 分页 COUNT 与 SELECT **同 FROM/JOIN/WHERE**（COUNT 调校行，与每调校一卡一致）；保留 `ClearParametersAfterExecute=false` 让 `@search/@alias*` 参数在 SELECT 与 COUNT 间复用。
- [ ] **Step 1: 写失败测试**：
  - 集成：JOIN DataTable 含**字面列名** `Attribute`、`Tune`（非 PerformancePreference/TuneRaw）；>12 条匹配调校跨多车型时 totalPages 正确。
  - 单元（金丝雀）：`ForzaCardRenderingTests` 保持绿（别名正确才绿）。
  - 单元：`查等级 S1 公路`(AND)/`查等级 越野`(OR 回退)/`查等级 S1`(class-only) 三分支产出正确 service 调用 + 分页 baseCmd（用 NSubstitute ForzaService + RecordingBotReplySender）。
  - 单元：`同步车辆表` **不再调用 `RunTaskNow` / 不再引用 `TaskName_12ClockSyncCar`**（替身断言未被调用），改调 `ForzaSyncService.SyncAsync(adminUserId, ...)`；保留「正在运行中」提示与拒绝多余参数。
- [ ] **Step 2→4: 失败→实现→通过**。
- [ ] **Step 5: 提交** `feat(forza): 查询走车型+调校 JOIN（别名回旧列名）；手动同步改 actor 感知`

### Task 14：`新增车型`/`新增调校` + `新增车辆` 兼容别名
**Files:** Modify `ForzaHorizonInputHandler.cs`、`ForzaService.cs`
- [ ] **Step 1: 写失败测试**：`新增车型` 仅需 厂商/型号；`新增调校 车辆=<id> 等级 比赛类型 传动` 必填校验（旧 5 字段强制移到调校路径）；`新增车辆`（旧别名）一次建车型+调校。手动创建 `IsManual=1`、`SourceIdentity=''`。
- [ ] **Step 2→4→5:** 实现 + 提交 `feat(forza): 新增车辆拆为新增车型+新增调校（保留兼容别名）`

### Task 15：`修改/删除车型`(VehicleId) 与 `修改/删除调校`(TuneId) + FK 级联测试
**Files:** Modify `ForzaHorizonInputHandler.cs`；Test `ForzaServiceTests.cs`（集成）
- [ ] **Step 1: 写失败测试**：
  - **集成（评审 P1）**：`Vehicles` 1 行 + ≥2 调校（含 1 人手维护），经真实服务路径删车型 → `COUNT(VehicleTunes WHERE VehicleId=X)==0`（验证 InnoDB FK CASCADE 真生效）。
  - `删除调校 <TuneId>` 只删该调校；`修改调校` 改显示值贯通 T8（identity/指纹冻结、清缓存、状态回正常）。
  - 沿用「全数字直删 / 非数字→FindByCriteria→确认按钮」双模式。
- [ ] **Step 2→4→5:** 实现 + 提交 `feat(forza): 车型/调校按 VehicleId/TuneId 增删改（FK 级联删除）`

### Task 16：`待更新调校表`/`已过时调校表` + 差异对比
**Files:** Modify `ForzaHorizonInputHandler.cs`、`ForzaService.cs`
**Produces:** `bool ApplyCachedTune(int tuneId, long actorUserId, out string error)`（从缓存应用→清 `IsManual`→状态`正常`→删缓存行）；`string RenderTuneDiff(DataTable live, DataTable cache)`（仅变化字段，`字段：旧值 -> 新值`）。
- [ ] **Step 1→5:** 测试：`待更新调校表` 列 status=`有更新` 带更新按钮→应用后清人手维护/状态正常/缓存消失；`已过时调校表` 列 status=`已过时` 带删除按钮；差异仅列变化字段。提交 `feat(forza): 待更新/已过时调校表与差异对比`

### Task 17：ForzaController 路由语义（VehicleId vs TuneId）
**Files:** Modify `Controllers/ForzaController.cs`；Test `ForzaServiceTests.cs`（控制器层）
- [ ] **Step 1: 写失败测试**：`GET /Forza/vehicles/{id}`=VehicleId 返车型+调校列表；`GET /Forza/tunes/{id}`=TuneId；`/vehicles/match` 按车型身份命中返 VehicleId。响应 envelope 显式 `vehicleId`/`tuneId`。`{id}` 保持 `int`（仅承载数值主键，拒绝非法路径——**非歧义考虑**，`match` 本是独立路由段）。
- [ ] **Step 2→4→5:** 实现 + 提交 `feat(forza): HTTP 区分 VehicleId 与 TuneId`

## P3 — 收尾

### Task 18：计划同步功能开关闸门 + actor 上报
**Files:** Modify `SystemInitializedService.cs`
- [ ] **Step 1→5:** 测试：`地平线`/`赛事自动化`关时计划同步不执行（仿 Esports `DecideSyncs`）；手动 actor≠0 时报告回发触发者。提交 `feat(forza): 计划同步加功能开关闸门与 actor 上报`

### Task 19：帮助文本 + CLAUDE.md 同步
**Files:** Modify `ShowHelpInputHandler.cs`、`CLAUDE.md`
- [ ] **Step 1→2:** 地平线帮助加 `新增车型/新增调校/修改调校/删除调校/待更新调校表/已过时调校表`；CLAUDE.md 改述三表/按来源同步，订正「Tracks/TrackRecords」实为 `Maps`。提交 `docs(forza): 帮助与 CLAUDE.md 对齐三表/按来源同步`

### Task 20：测试矩阵全量回归
- [ ] **Step 1:** `dotnet test tests/CandyBot.Tests`（无 Docker）+ `dotnet test tests/CandyBot.IntegrationTests`（Docker）全绿。
- [ ] **Step 2:** 核对下表 9 项均有用例。
- [ ] **Step 3: 提交** `test(forza): 重构测试矩阵全量回归`

## MySQL 集成测试矩阵（行为变更=P1）

| # | 场景 | 落在 |
|---|------|------|
| 1 | v1→v2 迁移：拆分正确、count/碰撞校验、可重入幂等、半成品重放 | T5 |
| 2 | 双来源隔离：同步 A 不动 B | T11 |
| 3 | 部分解析安全：跳过 sheet 时其调校不被过时/删除 | T11 |
| 4 | 手动改匹配键：改后 SourceIdentity/指纹不变、下次同步仍命中、无重复无误删 | T8 + T10 |
| 5 | 缓存 apply/delete：有更新→写缓存→更新→应用+清人手维护+状态正常+清缓存 | T10 + T16 |
| 6 | 同身份组：孪生按指纹正确配对、精确重复 seq 不撞键 | T10 |
| 7 | 并发 计划 vs 手动：按来源串行、无交叉污染 | T12 |
| 8 | FK 级联：删车型连带删其全部调校（真 InnoDB） | T15 |
| 9 | 查询 JOIN 别名：DataTable 含 `Attribute`/`Tune` 字面列、分页 totalPages 正确、查等级三分支 | T13 |

## Self-Review

- **Spec 覆盖**：issue 决策 1–19 + 机制 1–3 + 迁移 + 测试矩阵均映射任务；评审 5 个 P0 已逐条落地（见「评审修订记录」）。
- **占位符扫描**：无 TBD/泛化 error-handling；唯一 `// TODO(debt):` 为 D2 比例阈值。
- **类型一致性**：`VehicleTuneRequest` 含车型字段（解决 parser→Reconcile 断层）；`InsertTune(req, seq, out)` 与 Reconcile 的 `MAX(seq)+1` 自洽；`SourceIdentity`/`SourceFingerprint`/`SourceIdentitySeq` 列名跨 DDL(T4)/迁移(T5)/CRUD(T8)/协调(T10) 一致；查询别名 `Attribute`/`Tune` 与 `RenderVehiclesAsCards`/`ForzaCardRenderingTests` 契约一致。
- **已知残余风险（已缓解/接受）**：
  - 同车孪生（同车、6 字段同、仅干/湿/强度不同）仍共享 identity，靠**指纹配对**（内容驱动、对源重排免疫）+ `SourceIdentitySeq` 存储消歧；仅「同车、连指纹都相同」的精确重复才退化为「不可区分但等价」，覆盖即可、无数据损坏。
  - 管理员改显示值与冻结 identity 背离：源若日后改回，identity（仍按源算）不变仍命中——一致；若源改成**另一辆车/另一组判别字段**，则 identity 变 → 旧行过时 + 新增，由 `已过时调校表` 人工清理（机制1 既定）。
  - 迁移中途崩溃：备份表保命 + 「count 不等即重放」自愈 + 启动失败可见；人工 DROP 备份前应核对 count。
  - SQLite：Forza 本就不可用（取证脚注），新三表 MySQL-only 非回归。
