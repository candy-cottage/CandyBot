# AGENTS.md

本文件为在本仓库中工作的 AI 编码助手提供指引。

## 项目概览

CandyBot 是一个基于 ASP.NET Core（`net10.0`）的 QQ 官方机器人后端。它的核心**并不是** HTTP API，而是一个长期运行的 WebSocket 客户端：连接 QQ 机器人网关、接收用户消息，并以文本 / Markdown / 交互式按钮回复。HTTP 控制器是次要的（只读的赛事数据 + 加密测试接口）。

机器人面向用户的交互方式是**中文文本指令**（例如 `创建存档`、`赛事列表`、`预测 <比赛ID> <选项> <金额>`）。目前已落地两个功能域——**用户存档系统**和电竞**赛事预测系统**——此外还有一个大体上尚未接线的**经营模拟游戏**（"糖果屋"）位于 `Entities/` 目录，其指令目前仅以帮助文本形式存在。

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
             HandleShowHelpInput → HandleUserSavedInput → HandleBettingInput → HandleToggleCommand
```

- **`CandyBotService` 是一个按功能域拆分的 `partial class`（分部类）。** `Services/CandyBotService.cs` 保存 `Handler`、共享的 `CheckUser` 权限校验，以及 busy 提示语/正则辅助方法。每个 `HandleXxxInput` 各自位于 `Services/BotMessageHandler/` 下的独立文件中（`ShowHelpInputHandler.cs`、`UserSavedInputHandler.cs`、`BettingInputHandler.cs`）。**要新增一个指令域，请新增一个分部文件 + 处理方法，并将其插入 `Handler` 中的处理器链。**
- 在处理器内部，指令按**前缀**匹配（`e.Detail.StartsWith("...")`），且按优先级顺序排列——**顺序很重要**（更具体的前缀如 `赛事详情` 必须在 `赛事` 之前检查）。参数解析是手写的字符串切分 / `GeneratedRegex`（`GetFirstNumber`、`GetParamValue`）。
- **发送回复**统一经由 `QQBotService`（scoped，调用 `api.sgroup.qq.com` 的 REST 客户端）。`SendAsync(IBotMessage, BotReply)` 会自动在纯文本与 Markdown+键盘之间选择。Access Token 在 `IMemoryCache` 中获取/缓存。`BotReply` 提供了从 `string` 到 Markdown 的隐式转换，因此 `await botService.SendAsync(e, "文本")` 可直接使用。
- **交互式 UI** 由 `Models/QQBot.cs` 构建：`Button.CreateCmdButton`、`KeyboardMessage` 配合 `AppendButtons`/`AppendButtonsWithNewRow`/`AddPaginationRow` 扩展方法，以及 `string.CreateCmdInput()`（在 Markdown 中生成 `<qqbot-cmd-input>` 标签）。请复用这些辅助方法，不要手写 JSON。

### 权限

`CandyBotService.CheckUser(e, user, checkAdmin, checkOperator)` 是唯一的权限闸口。若 `user` 为 null，它会回复"创建存档"提示，然后校验 `user.IsAdmin` / `user.IsOperator`。仅管理员可用的指令调用 `CheckUser(e, user, false)`；仅注册用户可用的指令调用 `CheckUser(e, user, false, false)`。

### 数据层（双后端）

- `SQLHelper` 是抽象类；本仓库提供两个具体实现：`Utilities/SQLiteHelper.cs`（默认）和 `Utilities/MySQLHelper.cs`。
- 当前生效的后端由 `SQLConnectionConfig.Mode` 决定，并通过 DI 工厂 `SQLConnectionConfig.NewSQLHelper` 解析（注册为 `AddScoped(SQLConnectionConfig.NewSQLHelper)`）。常规代码中请注入 `SQLHelper`，**切勿**注入具体类型。
- `SQLHelperExtension.ExecuteDataSetForUpdate` 仅在 **MySQL** 上追加 `FOR UPDATE` 行锁（在 SQLite 上为空操作）——这与赛事预测的并发路径相关。
- 查询写法：`sql.Parameters["@x"] = v`，随后调用 `Execute` / `ExecuteDataSet` / `ExecuteDataRow`；检查 `sql.Success`。多步写操作用 `sql.NewTransaction()` / `Commit()` / `Rollback()` 包裹。

### 启动与首次初始化

`Program.cs` 装配 DI 并注册三个 hosted service：
- `SystemInitializeService`（启动时）：从 `PluginConfig` 文件加载 `SQLConnectionConfig` + `CandyBotFunctionConfig`，并设置货币/材料的显示名称。
- `QQBotWebSocketService`（后台）：即上文描述的网关循环。
- `SystemInitializedService`（启动时）：写入一条服务器登录日志记录。

**数据库不会在启动时自动建表。** 第一个发送 `系统初始化` 指令的用户会触发 `SQLTableInitializeService.Initialize`，它在一个事务内执行 SQL 脚本（根据后端选择 `fungame_sqlite.sql` *或* `fungame.sql`，然后是 `betting.sql`，再是 `candybot.sql`），在某个 `PluginConfig` 中标记初始化完成，并将该发送者提升为首位管理员/运营。SQL 脚本通过 `.csproj` 的 `CopyToOutputDirectory` 复制到输出目录（`scripts/`）。

### 配置

- **`appsettings.json` 的 `Bot` 段**（`AppId`、`Secret`、`UseSandbox`）绑定到 `BotConfig`。已提交的值是占位符；真实凭据应放入 user-secrets（csproj 中已设置 `UserSecretsId`）或环境变量，**不要提交**。
- **运行时功能开关与数据库连接** 通过 FunGame 的 `PluginConfig`（基于文件的键值存储，如 `candybot.functions`、`sqlconfig.connection`、`sqlconfig.initialization`）保存——这**独立于 `appsettings.json`**，且会跨重启持久化。`CandyBotFunctionConfig.ToggleConfigs` 驱动聊天内的 `开启<系统>` / `关闭<系统>` 管理员指令。`*.db` 与配置文件已在 gitignore 中。

### Entities/（经营模拟，大体上为半成品）

`Entities/`（`RealEstate`、`MovableProperty`、`HumanResource`）在 FunGame 的 `BaseEntity` 之上建模了一个糖果屋经营游戏，实现了本地的 `IDescription` / `IBusinessSimulationEntity` 接口。`ShowHelpInputHandler.cs` 中的 `*帮助` 文本宣传了大量商铺/工厂/物流指令，但对应的处理器**尚未实现**——请将这些实体视为未来功能域的脚手架，而非已生效的行为。

## 约定

- 代码、注释、日志消息以及所有面向用户的字符串都使用**中文**——编辑时请保持一致。
- 全程采用 C# 13 / .NET 10 惯用法：主构造函数、集合表达式 `[...]`、用 `partial` 类拆分处理器、`GeneratedRegex`。已启用 `Nullable` 和 `ImplicitUsings`。
- 根命名空间为 `CandyCottage.CandyBot`（注意：程序集名为 `CandyBot`）。
- 日志通过 `ILogger` 结构化输出；开销较大的日志用 `logger.IsEnabled(LogLevel.X)` 守卫。不要使用 `Console.WriteLine`。
