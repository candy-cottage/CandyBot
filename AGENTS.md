# AGENTS.md

本文件为在本仓库中工作的 AI 编码助手提供指引。

## 项目概览

CandyBot 是一个基于 ASP.NET Core（`net10.0`）的 QQ 官方机器人后端。它的核心**并不是** HTTP API，而是一个长期运行的 WebSocket 客户端：连接 QQ 机器人网关、接收用户消息，并以文本 / Markdown / 交互式按钮回复。HTTP 控制器是次要的（只读的赛事/车辆数据 + 加密测试接口 + 腾讯文档查看器）。

机器人面向用户的交互方式是**中文文本指令**（例如 `创建存档`、`赛事列表`、`预测 <比赛ID> <选项> <金额>`、`查车 <车名>`）。目前已落地的功能域有：

| 功能域 | 状态 | 说明 |
|--------|------|------|
| 用户存档系统 | ✅ 已落地 | 创建存档、昵称修改、糖果屋仪表盘 |
| 电竞赛事预测系统 | ✅ 已落地 | 赛事/比赛查询、投注预测、领奖 |
| 极限竞速：地平线6 | ✅ 已落地 | 查车、查赛道、查等级（数据源于 ZGC 车队调校表格） |
| 经营模拟游戏（糖果屋） | ⚠️ 半成品 | `Entities/` 中有完整实体建模，帮助文本已编写，但业务处理器尚未实现 |

## 常用命令

```bash
dotnet build                      # 构建
dotnet run                        # 以默认 (http) 启动配置运行
dotnet run --launch-profile https # 以 https 配置运行
dotnet tool restore               # 还原本地 dotnet-ef 工具（见 dotnet-tools.json）
dotnet ef <args>                  # EF Core CLI（工具版本锁定为 10.0.8）
```

- 在 Development 环境下，OpenAPI + Scalar API 文档在 `/scalar` 提供（启动配置会自动打开）。
- HTTP：`http://localhost:5139`；HTTPS：`https://localhost:7090`。
- **本项目没有测试工程或测试框架。** `Utilities/Tests.cs` 是一个手动演示用的辅助类（发送示例消息），并非自动化测试。不要假定 `dotnet test` 会执行任何有意义的内容。

## 必须理解的外部依赖

本项目是构建在两个由 Milimoe / ProjectRedbud 提供的外部 NuGet 库之上的一层薄应用代码。大多数"框架级"类型来自它们，而非本仓库：

- **`FunGame.Core`**（`Milimoe.FunGame.Core.*`）—— 提供核心领域模型：`User`/`Inventory` 实体、`SQLHelper`（抽象数据库访问）、`PluginConfig`（基于文件的配置存储）、`Encryption`/`Verification` 工具类、`BaseEntity`，以及游戏常量（如 `General.GameplayEquilibriumConstant.InGameCurrency` 等）。遇到陌生类型时，几乎都能在这里找到。
- **`FunGame.SQLQueryExtension`**（`ProjectRedbud.FunGame.SQLQueryExtension`）—— 针对 FunGame 表结构的 `SQLHelper` 扩展方法：`GetUserById`、`GetUserByUsernameAndAutoKey`、`RegisterUser`、`UpdateUser`、`UsernameExists` 等。

需要确认它们的确切 API 时，请使用 Context7（或查阅库源码），不要凭空猜测。

## 架构

### 消息处理管线（应用的核心）

```
QQBotWebSocketService (BackgroundService)
  → 维护网关连接：Hello/Identify/Resume、心跳 ACK 跟踪、自动重连
  → 收到 Dispatch (op 0) 时：将 C2C_MESSAGE_CREATE / GROUP_AT_MESSAGE_CREATE 反序列化为 IBotMessage
  → 去掉指令前导的 "/"，开启一个 DI scope
  → CandyBotService.Handler(IBotMessage)
        → 根据 openid 解析 User（内存缓存 10 分钟；通过 autokey = openid.Encrypt(openid) 查找）
        → 依次执行一条处理器链，每个处理器返回 bool（"我处理了，停止"）：
             HandleShowHelpInput → HandleCandyCottageInput → HandleUserSavedInput
             → HandleForzaHorizonInput → HandleBettingInput → HandleToggleCommand
```

- **`CandyBotService` 是一个按功能域拆分的 `partial class`（分部类）。** `Services/CandyBotService.cs` 保存 `Handler`、共享的 `CheckUser` 权限校验、`WillGoOn` 功能开关检查、`SendUserNotice` 后台通知发送，以及 `_busy` 提示语/正则辅助方法。每个 `HandleXxxInput` 各自位于 `Services/BotMessageHandler/` 下的独立文件中：
  - `ShowHelpInputHandler.cs` —— 帮助系统（含分页）
  - `CandyCottageInputHandler.cs` —— 糖果屋仪表盘（`糖果屋`、`我的存档`）
  - `UserSavedInputHandler.cs` —— 用户存档、昵称修改
  - `ForzaHorizonInputHandler.cs` —— 地平线6 车辆/赛道/等级查询
  - `BettingInputHandler.cs` —— 赛事预测系统
  **要新增一个指令域，请新增一个分部文件 + 处理方法，并将其插入 `Handler` 中的处理器链。**
- 在处理器内部，指令按**前缀**匹配（`e.Detail.StartsWith("...")`），且按优先级顺序排列——**顺序很重要**（更具体的前缀如 `赛事详情` 必须在 `赛事` 之前检查）。参数解析是手写的字符串切分 / `GeneratedRegex`（`GetFirstNumber`、`GetParamValue`）。
- 每个处理器内部都先检查对应功能开关（`WillGoOn(functionConfig.Xxx, user)`），若功能关闭且用户非管理员或未开启管理员调试，则返回维护提示并短路。
- **发送回复**统一经由 `QQBotService`（scoped，调用 `api.sgroup.qq.com` 的 REST 客户端）。`SendAsync(IBotMessage, BotReply)` 会自动在纯文本与 Markdown+键盘之间选择。Access Token 在 `IMemoryCache` 中获取/缓存。`BotReply` 提供了从 `string` 到 Markdown 的隐式转换，因此 `await botService.SendAsync(e, "文本")` 可直接使用。
- **交互式 UI** 由 `Models/QQBot.cs` 构建：`Button.CreateCmdButton`（Type=2 指令按钮）、`Button.CreateInteractionButton`（Type=1 回调按钮）、`KeyboardMessage` 配合 `AppendButtons`/`AppendButtonsWithNewRow`/`AddPaginationRow` 扩展方法，以及 `string.CreateCmdInput()`（在 Markdown 中生成 `<qqbot-cmd-input>` 标签）和 `string.CreateCmdEnter()`（C2C 的 `<qqbot-cmd-enter>` 标签）。请复用这些辅助方法，不要手写 JSON。

### 权限

`CandyBotService.CheckUser(e, user, checkAdmin, checkOperator)` 是唯一的权限闸口。若 `user` 为 null，它会回复"创建存档"提示（含快捷按钮），然后校验 `user.IsAdmin` / `user.IsOperator`。仅管理员可用的指令调用 `CheckUser(e, user, false)`；仅注册用户可用的指令调用 `CheckUser(e, user, false, false)`。

### 功能开关系统

`CandyBotFunctionConfig`（`Utilities/CandyBotFunctionConfig.cs`）管理运行时功能开关，通过 `PluginConfig`（`candybot.functions`）持久化。支持的开关：

| 开关名 | 聊天指令 | 说明 |
|--------|---------|------|
| `机器人` | `开启机器人` / `关闭机器人` | 全部系统的总开关 |
| `管理员调试` | `开启管理员调试` / `关闭管理员调试` | 允许管理员在功能关闭期间调试 |
| `糖果屋` | `开启糖果屋` / `关闭糖果屋` | 糖果屋经营模拟模块 |
| `用户系统` | `开启用户系统` / `关闭用户系统` | 用户存档相关 |
| `昵称修改` | `开启昵称修改` / `关闭昵称修改` | 昵称修改子功能 |
| `地平线` | `开启地平线` / `关闭地平线` | 地平线6 查询模块 |
| `赛事系统` | `开启赛事系统` / `关闭赛事系统` | 赛事预测系统 |
| `赛事只读` | `开启赛事只读` / `关闭赛事只读` | 禁止下注但保留查询 |

`WillGoOn(isOpen, user)` 方法在 `isOpen == false` 时仍允许管理员通过（若 `AllowAdminsDebug` 开启）。

### 数据层（双后端）

- `SQLHelper` 是抽象类；本仓库提供两个具体实现：`Utilities/SQLiteHelper.cs`（默认）和 `Utilities/MySQLHelper.cs`。
- 当前生效的后端由 `SQLConnectionConfig.Mode` 决定，并通过 DI 工厂 `SQLConnectionConfig.NewSQLHelper` 解析（注册为 `AddScoped(SQLConnectionConfig.NewSQLHelper)`）。常规代码中请注入 `SQLHelper`，**切勿**注入具体类型。注意：`MySQLHelper` 和 `SQLiteHelper` 也各自注册为 scoped，这是因为 `SQLTableInitializeService` 需要 `is` 类型判断来选择 SQL 脚本——**新代码不要注入它们**。
- `SQLHelperExtension.ExecuteDataSetForUpdate` 仅在 **MySQL** 上追加 `FOR UPDATE` 行锁（在 SQLite 上为空操作）——这与赛事预测的并发路径相关。
- 查询写法：`sql.Parameters["@x"] = v`，随后调用 `Execute` / `ExecuteDataSet` / `ExecuteDataRow`；检查 `sql.Success`。多步写操作用 `sql.NewTransaction()` / `Commit()` / `Rollback()` 包裹。
- 注意：`sql.ClearParametersAfterExecute` 默认为 `true`。部分代码（如 `ForzaService`）会临时将其设为 `false` 以复用参数。

### 后台任务队列

`IBackgroundTaskQueue` / `BackgroundTaskQueue` 基于 `System.Threading.Channels` 实现有界队列（容量 100），由 `QueuedHostedService` 消费。用于异步发送用户通知等非实时任务。放入队列用 `await taskQueue.QueueAsync(workItem)`。

### 启动与首次初始化

`Program.cs` 装配 DI 并注册以下服务：

**单例：**
- `CandyBotConfig`、`BotConfig`、`SQLConnectionConfig`、`CandyBotFunctionConfig`、`TencentDocsConfig`
- `CharacterModule`、`ItemModule`、`SkillModule`（FunGame 游戏模块）
- `IBackgroundTaskQueue` / `BackgroundTaskQueue`

**Scoped：**
- `SQLHelper`（通过 `SQLConnectionConfig.NewSQLHelper` 工厂）
- `MySQLHelper`、`SQLiteHelper`（仅用于初始化时的类型判断）
- `QQBotService`、`SheetService`、`UserService`、`BettingService`、`ForzaService`、`CandyBotService`
- `SQLTableInitializeService`

**Hosted Service（后台）：**
- `SystemInitializeService`（启动时加载配置）
- `QQBotWebSocketService`（WebSocket 网关循环）
- `SystemInitializedService`（启动时写入服务器登录日志）
- `QueuedHostedService`（消费后台任务队列）

**认证：** `ApiTokensBearerHandler` 是自定义 `AuthenticationHandler`，从 `Authorization: Bearer <token>` 标头提取 API Token，查 `ApiTokens` 表验证，将用户信息设为 `ClaimsPrincipal`。`[AllowAnonymous]` 跳过认证；`[Authorize(Roles = "admin,op")]` 要求管理/运营角色。

**数据库不会在启动时自动建表。** 第一个发送 `系统初始化` 指令的用户会触发 `SQLTableInitializeService.Initialize`，它在一个事务内按顺序执行 SQL 脚本：
1. `fungame_sqlite.sql` 或 `fungame.sql`（根据后端）
2. `betting.sql`
3. `candybot.sql`
4. `forza.sql`

并在 `PluginConfig`（`sqlconfig.initialization`）中标记初始化完成，将该发送者提升为首位管理员/运营。SQL 脚本通过 `.csproj` 的 `CopyToOutputDirectory` 复制到输出目录（`scripts/`）。

### 各功能域详解

#### 帮助系统 (`ShowHelpInputHandler`)

- `帮助` —— 显示模块选择菜单（含交互按钮）
- `<模块名>帮助 [页码]` —— 分页显示指定模块的指令列表（每页 12 条）
- 模块包括：系统帮助、住宅帮助、商铺帮助、仓库帮助、工厂帮助、物流帮助、停车场帮助、经济帮助、管理员帮助
- 帮助字典定义在 `ShowHelpInputHandler.cs` 顶部（`GeneralHelp`、`SystemHelp`、`ResidenceHelp` 等静态属性）
- 管理员帮助受权限保护（`CheckUser(e, user, false)`）

#### 糖果屋仪表盘 (`CandyCottageInputHandler`)

- `糖果屋` / `我的存档` —— 显示用户基本信息面板（昵称、UID、金币、材料、注册时间等，由 `UserService.GetUserInfo` 生成）
- 糖果屋经营模拟的**业务指令尚未实现**，当前只有帮助文本

#### 用户存档系统 (`UserSavedInputHandler`)

- `系统初始化` —— 首次初始化数据库（仅未初始化时可用）
- `创建存档` —— 创建用户存档，初始 5000 金币 + 100 材料
- `修改昵称为<新昵称>` —— 提交昵称修改申请（需审核，`NicknameChangeRequests` 表）
- `确认修改昵称为<新昵称>` —— 二次确认昵称修改
- `取消修改昵称` —— 取消待审核的昵称申请

#### 极限竞速：地平线6 (`ForzaHorizonInputHandler` + `ForzaService`)

- `地平线` —— 显示主菜单（查车/查赛道/查等级三个快捷按钮）
- `查车 <车名> [页码]` —— 模糊搜索车辆（按制造商/型号），分页返回
- `查赛道 <赛道名> [页码]` —— 搜索赛道信息
- `查等级 <等级> [比赛类型] [页码]` —— 按等级/比赛类型筛选车辆
- 所有查询结果带分页键盘（`AddPaginationRow`）
- `ForzaService` 底层查询 `Vehicles` / `Tracks` / `TrackRecords` 表
- HTTP API：`ForzaController` 提供 `/forza/vehicles` GET（匿名）+ POST/PUT/DELETE（admin/op 角色）

#### 赛事预测系统 (`BettingInputHandler` + `BettingService`)

- `赛事列表` —— 分页显示所有赛事
- `赛事详情 <ID> [页码]` —— 查看赛事下的比赛列表
- `赛程` / `比赛列表 [页码]` —— 全局比赛列表
- `比赛详情 <ID>` —— 查看单场比赛详情及用户在该场的预测
- `预测 <比赛ID> <选项> <金额>` —— 下注
- `我的预测 [比赛ID] [页码]` —— 查看预测记录
- `预测领奖` —— 领取已结算预测的奖励
- `预测帮助` / `赛事帮助` —— 赛事帮助菜单
- HTTP API：`BettingController` 提供 `/betting/events`、`/betting/matches` GET（匿名）
- 管理指令（通过 `BettingService`）：创建赛事、创建比赛、修改比赛、结算比赛、退赛、取消比赛等

#### 腾讯文档集成 (`SheetService` + `SheetController`)

- `SheetService` 调用腾讯文档 API 获取在线表格数据，本地缓存到 `PluginConfig`（`files/`、`sheets/`），缓存时间由 `IMemoryCache` 管理
- `SheetController` 提供 Web 端表格查看器（`/sheet/view?fileId=xxx`）和 JSON API（`/sheet/{fileId}`、`/sheet/file/{fileId}`）

### 配置

- **`appsettings.json`** 不包含敏感信息。`Bot` 段的 `AppId`、`Secret`、`UseSandbox` 应通过 user-secrets 或环境变量提供（csproj 中已设置 `UserSecretsId`）。
- **运行时功能开关与数据库连接** 通过 `PluginConfig` 持久化，独立于 `appsettings.json`。`*.db` 与配置文件已在 gitignore 中。
- **`CandyBotConfig`** 包含 `ApiTokenSalt` 等内部配置。
- **`TencentDocsConfig`** 存储腾讯文档 API 凭据。

### Modules/（FunGame 游戏模块）

`Modules/` 目录包含 FunGame 框架的游戏模块定义（角色、技能、道具），用于经营模拟游戏：

- `CharacterModule` —— 角色注册工厂（`CharacterID` 枚举驱动）
- `ItemModule` —— 道具注册（`ItemID`）
- `SkillModule` —— 技能注册（`SkillID`）
- `Characters/` —— 具体角色实现（`Mili.cs`、`CandyCottageCharacter.cs`）
- 这些模块目前为经营模拟游戏（糖果屋）的脚手架，尚未在消息处理器中实际调用

### Entities/（经营模拟，半成品）

`Entities/`（`RealEstate`、`MovableProperty`、`HumanResource`）在 FunGame 的 `BaseEntity` 之上建模了一个糖果屋经营游戏，实现了本地的 `IDescription` / `IBusinessSimulationEntity` 接口。`ShowHelpInputHandler.cs` 中的帮助文本宣传了大量商铺/工厂/物流指令，但对应的处理器**尚未实现**——请将这些实体视为未来功能域的脚手架，而非已生效的行为。

## 约定

- 代码、注释、日志消息以及所有面向用户的字符串都使用**中文**——编辑时请保持一致。
- 全程采用 C# 13 / .NET 10 惯用法：主构造函数、集合表达式 `[...]`、用 `partial` 类拆分处理器、`GeneratedRegex`。已启用 `Nullable` 和 `ImplicitUsings`。
- 根命名空间为 `CandyCottage.CandyBot`（注意：程序集名为 `CandyBot`）。
- 日志通过 `ILogger` 结构化输出；开销较大的日志用 `logger.IsEnabled(LogLevel.X)` 守卫。不要使用 `Console.WriteLine`。
- 新增指令域时：创建 `Services/BotMessageHandler/XxxInputHandler.cs`（`partial class CandyBotService`），在 `Handler` 方法中的处理器链里插入调用，确保前缀匹配优先级正确，在 `CandyBotFunctionConfig` 中添加对应开关（如需运行时控制）。
