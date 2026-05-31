# Candy Bot

> 私有机器人（开发中 / Private Bot, Development in Progress）

CandyBot 是一个基于 ASP.NET Core（`net10.0`）的 QQ 官方机器人后端。其核心是一个长期运行的 WebSocket 客户端：连接 QQ 机器人网关、接收用户消息，并以文本 / Markdown / 交互式按钮回复。用户通过**中文文本指令**与机器人交互（例如 `创建存档`、`赛事列表`、`预测 <比赛ID> <选项> <金额>`）。

## 功能

- **用户存档系统** —— 创建存档、昵称修改及审核流程。
- **赛事预测系统** —— 浏览赛事/比赛、下注预测、领取奖励，以及管理员的创建/修改/结算/推迟等操作。
- **经营模拟游戏（糖果屋，开发中）** —— 住宅、商铺、仓库、工厂、物流等实体已建模，相关指令暂以帮助文本形式预留，处理逻辑尚未实现。
- **HTTP API（次要）** —— 提供只读的赛事数据接口与加密测试接口。

## 技术栈

- ASP.NET Core / .NET 10
- 构建于 [`FunGame.Core`](https://www.nuget.org/packages/FunGame.Core) 与 `FunGame.SQLQueryExtension` 之上（提供用户实体、数据库访问、配置存储等核心能力）
- 数据库双后端：**SQLite**（默认）或 **MySQL**
- OpenAPI + Scalar API 文档

## 快速开始

```bash
dotnet build                       # 构建
dotnet run                         # 以默认 (http) 启动配置运行
dotnet run --launch-profile https  # 以 https 配置运行
```

- HTTP：`http://localhost:5139`；HTTPS：`https://localhost:7090`。
- 在 Development 环境下，API 文档位于 `/scalar`（启动时会自动打开）。

### 首次初始化

数据库不会在启动时自动建表。第一个向机器人发送 `系统初始化` 指令的用户会触发建表脚本执行，并被提升为首位管理员。

## 配置

- **机器人凭据**：`appsettings.json` 的 `Bot` 段（`AppId`、`Secret`、`UseSandbox`）。仓库中为占位值，**真实凭据请放入 user-secrets 或环境变量，切勿提交**。
- **数据库连接与功能开关**：通过 `FunGame.Core` 的 `PluginConfig` 以文件形式保存，独立于 `appsettings.json` 并跨重启持久化。功能开关可由管理员在聊天中用 `开启<系统>` / `关闭<系统>` 指令切换。

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `Services/BotServices/` | QQ 网关 WebSocket 客户端与消息发送（REST） |
| `Services/BotMessageHandler/` | 各功能域的指令处理器（`CandyBotService` 的分部类） |
| `Services/SystemServices/` | 启动初始化与建表逻辑 |
| `Controllers/` | HTTP API 控制器 |
| `Models/` | QQ 协议模型、消息/按钮构造器、数据模型 |
| `Entities/` | 经营模拟游戏实体（开发中） |
| `Utilities/` | 数据库 Helper（SQLite/MySQL）、配置类、工具 |
| `Scripts/` | 建表 SQL 脚本 |

> 面向 AI 编码助手的开发指引详见 [`AGENTS.md`](AGENTS.md) / [`CLAUDE.md`](CLAUDE.md)。

## 许可证

见 [`LICENSE.txt`](LICENSE.txt)。
