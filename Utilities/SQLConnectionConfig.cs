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
        }
    }
}
