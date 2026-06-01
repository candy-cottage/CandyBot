# Candy Bot

> 私有机器人（开发中 / Private Bot, Development in Progress）

CandyBot 是一个基于 ASP.NET Core（`net10.0`）的 QQ 官方机器人后端。其核心不是一个标准 HTTP 服务，而是一个长期运行的 WebSocket 客户端：连接 QQ 机器人网关、接收 C2C/群消息，并通过文本、Markdown 和交互式按钮回复用户。

用户与机器人交互的方式主要是中文文本指令，例如 `创建存档`、`赛事列表`、`预测 <比赛ID> <选项> <金额>`、`查车 <车名>`。

## 主要功能

- **用户存档系统**：创建存档、查看仪表盘、昵称修改申请与审核。
- **赛事预测系统**：浏览赛事/比赛、下注预测、领取奖励；管理员可创建/修改/结算/退赛/取消比赛。
- **极限竞速：地平线6 查询**：查车、查赛道、查等级，支持分页结果和快速命令按钮。
- **经营模拟游戏（糖果屋）**：住宅、商铺、仓库、工厂、物流等实体已建模，业务处理尚未完全落地。
- **腾讯文档集成**：通过 `SheetService` 缓存在线文档并提供 Web 视图/JSON API。
- **HTTP API（辅助）**：只读赛事数据、车辆数据、腾讯文档访问接口，以及测试/加密相关接口。

## 技术栈

- .NET 10 / ASP.NET Core
- `FunGame.Core` + `FunGame.SQLQueryExtension`：用户实体、数据库访问、配置存储、加密验证等基础能力
- 数据库双后端：**SQLite**（默认）和 **MySQL**
- `OpenAPI` + `Scalar`：开发环境下提供 API 文档
- `System.Threading.Channels`：后台任务队列用于异步通知发送

## 快速启动

```bash
dotnet build
(dotnet run | dotnet run --launch-profile https)
```

默认启动后：
- HTTP：`http://localhost:5139`
- HTTPS：`https://localhost:7090`

开发环境下会启用 API 文档：
- OpenAPI：自动映射
- Scalar API：`/scalar`

## 首次初始化

本项目不会在启动时自动建表。首次创建数据库结构的时机是：

1. 启动后机器人收到 `系统初始化` 指令
2. `SQLTableInitializeService` 执行 `scripts/` 中的 SQL 脚本
3. 触发初始化的用户会被提升为首位管理员/运营

## 配置说明

- **机器人凭据**：`appsettings.json` 中的 `Bot` 段仅用于占位。真实 `AppId`、`Secret`、`UseSandbox` 应通过 user-secrets 或环境变量注入，避免提交到仓库。
- **数据库和运行时开关**：由 `PluginConfig` 持久化保存，独立于 `appsettings.json`。运行时可通过聊天命令 `开启<开关>` / `关闭<开关>` 切换。
- **认证**：API 使用自定义 `ApiTokensBearerHandler`，通过 `Authorization: Bearer <token>` 验证 `ApiTokens` 表中的凭证。

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `Program.cs` | 应用启动及服务注册 |
| `Services/BotServices/` | QQ 网关 WebSocket 客户端、消息发送、Tencent Docs 服务 |
| `Services/BotMessageHandler/` | `CandyBotService` 的分部处理器，按功能域拆分指令逻辑 |
| `Services/SystemServices/` | 系统初始化、数据库建表、后台任务队列等基础服务 |
| `Controllers/` | HTTP API 控制器 |
| `Models/` | QQ 消息模型、按钮/键盘构建、DTO、腾讯文档模型 |
| `Utilities/` | SQL Helper、配置类、工具、功能开关实现 |
| `Entities/` | 经营模拟游戏实体模型（半成品） |
| `Modules/` | FunGame 游戏模块（角色 / 技能 / 道具，糖果屋脚手架） |
| `Interfaces/` | 本地接口定义（如 `IBackgroundTaskQueue`） |
| `Views/` | Razor 视图（腾讯文档表格查看器 `Sheet/viewer.cshtml`） |
| `configs/` | 运行时数据配置（如 `candy-cottage/extend-items.json`） |
| `scripts/` | SQL 建表脚本（`fungame.sql` / `fungame_sqlite.sql` / `betting.sql` / `candybot.sql` / `forza.sql`） |

## 核心模块

- `QQBotWebSocketService`：连接 QQ Gateway，处理心跳、重连、Dispatch 事件。
- `QQBotService`：负责发送消息与构建回复，自动在纯文本与 Markdown+键盘间切换。
- `CandyBotService`：消息处理入口，按顺序调用 `HandleShowHelpInput`、`HandleCandyCottageInput`、`HandleUserSavedInput`、`HandleForzaHorizonInput`、`HandleBettingInput`、`HandleToggleCommand`。
- `SQLConnectionConfig.NewSQLHelper`：根据配置动态选择 `SQLiteHelper` 或 `MySQLHelper`。
- `BackgroundTaskQueue` / `QueuedHostedService`：异步队列用于发送后台通知。
- `CandyBotFunctionConfig`：运行时功能开关管理，支持 `机器人`、`管理员调试`、`糖果屋`、`用户系统`、`昵称修改`、`地平线`、`赛事系统`、`赛事只读` 等开关。

## 重要命令

- `创建存档`
- `糖果屋` / `我的存档`
- `赛事列表` / `赛事详情 <ID>`
- `赛程` / `比赛列表`
- `比赛详情 <ID>`
- `预测 <比赛ID> <选项> <金额>`
- `我的预测 [比赛ID] [页码]`
- `预测领奖`
- `地平线` / `查车 <车名> [页码]` / `查赛道 <赛道名> [页码]`
- `查等级 <等级> [比赛类型] [页码]`
- `系统初始化`
- `开启机器人` / `关闭机器人`
- `开启管理员调试` / `关闭管理员调试`

## HTTP API 概览

- `GET /betting/events` / `GET /betting/events/{id}`
- `GET /betting/matches` / `GET /betting/matches/{id}`
- `GET /betting/bets`
- `GET /forza/vehicles` / `GET /forza/vehicles/{id}` / `GET /forza/vehicles/match`（匿名只读；`POST` / `PUT` / `DELETE /forza/vehicles` 需 admin/op 角色）
- `GET /sheet/{fileId}`
- `GET /sheet/file/{fileId}`
- `GET /sheet/view?fileId={fileId}`
- `GET /Test/...`（加密 / 测试辅助接口）

## 其他说明

- 本项目没有专门的自动化测试工程，`Utilities/Tests.cs` 仅提供手动演示辅助方法。
- 若需要快速理解项目架构，可参阅仓库根目录的 [`AGENTS.md`](AGENTS.md) 和 [`CLAUDE.md`](CLAUDE.md)。
- 代码风格偏向中文注释和中文用户提示，新增业务功能时请保持一致。

## 许可证

见 [`LICENSE.txt`](LICENSE.txt)。
