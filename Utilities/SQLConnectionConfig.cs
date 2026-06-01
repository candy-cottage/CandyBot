using Microsoft.Data.Sqlite;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.Constant;
using MySql.Data.MySqlClient;

namespace CandyCottage.CandyBot.Utilities
{
    public class SQLConnectionConfig
    {
        public SQLMode Mode { get; set; } = SQLMode.SQLite;
        public string DataSource { get; set; } = "candybot";
        public string Port { get; set; } = "";
        public string DataBase { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";

        public PluginConfig SQLConfig { get; set; } = new("sqlconfig", "connection");

        public static SQLHelper NewSQLHelper(IServiceProvider sp)
        {
            SQLConnectionConfig config = sp.GetRequiredService<SQLConnectionConfig>();
            SQLHelper sql = config.Mode switch
            {
                SQLMode.MySQL => sp.GetRequiredService<MySQLHelper>(),
                SQLMode.SQLite => sp.GetRequiredService<SQLiteHelper>(),
                _ => throw new NotSupportedException($"不支持的数据库模式：{config.Mode}")
            };
            return sql;
        }

        public string GetConnectConfigsForMySQL()
        {
            MySqlConnectionStringBuilder mcsb = new()
            {
                Server = DataSource,
                Port = uint.TryParse(Port, out var port) ? port : 3306U,
                Database = DataBase,
                UserID = User,
                Password = Password,
                CharacterSet = "utf8mb4"
            };
            return mcsb.ToString();
        }

        public string GetConnectConfigsForSQLite()
        {
            if (DataSource == "") DataSource = "candybot";
            if (!DataSource.EndsWith(".db")) DataSource += ".db";
            SqliteConnectionStringBuilder scsb = new()
            {
                DataSource = DataSource
            };
            return scsb.ToString();
        }

        /// <summary>
        /// 优先级：环境变量 &gt; PluginConfig &gt; 默认值。
        /// 环境变量：CANDYBOT_SQL_MODE(enum) / CANDYBOT_SQL_DATASOURCE / CANDYBOT_SQL_PORT /
        /// CANDYBOT_SQL_DATABASE / CANDYBOT_SQL_USER / CANDYBOT_SQL_PASSWORD。
        /// 环境变量覆盖仅作用于内存，不写回配置文件。
        /// </summary>
        public void Init()
        {
            SQLConfig.LoadConfig();
            string mode = SQLConfig.GetOrAdd("Mode", Mode.ToString().ToLower()) ?? "sqlite";
            if (Enum.TryParse(mode, true, out SQLMode result))
            {
                Mode = result;
            }
            DataSource = SQLConfig.GetOrAdd("DataSource", DataSource) ?? "";
            Port = SQLConfig.GetOrAdd("Port", Port) ?? "";
            DataBase = SQLConfig.GetOrAdd("DataBase", DataBase) ?? "";
            User = SQLConfig.GetOrAdd("User", User) ?? "";
            Password = SQLConfig.GetOrAdd("Password", Password) ?? "";
            SQLConfig.SaveConfig();

            // 环境变量覆盖（在 SaveConfig 之后，仅内存生效，不持久化注入的机密）。
            Mode = ConfigEnv.Override("CANDYBOT_SQL_MODE", Mode);
            DataSource = ConfigEnv.Override("CANDYBOT_SQL_DATASOURCE", DataSource);
            Port = ConfigEnv.Override("CANDYBOT_SQL_PORT", Port);
            DataBase = ConfigEnv.Override("CANDYBOT_SQL_DATABASE", DataBase);
            User = ConfigEnv.Override("CANDYBOT_SQL_USER", User);
            Password = ConfigEnv.Override("CANDYBOT_SQL_PASSWORD", Password);
        }
    }
}
