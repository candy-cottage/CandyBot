using System.Data;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Library.Constant;

namespace CandyCottage.CandyBot.Utilities
{
    public static class SQLHelperExtension
    {
        /// <summary>
        /// 行级写锁定查询，需使用前开启事务。仅 MySQLHelper 有效，因 SQLiteHelper 事务可对全库进行锁定。
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="script">为空则使用 <see cref="SQLHelper.Script"/></param>
        /// <returns></returns>
        public static DataSet ExecuteDataSetForUpdate(this SQLHelper sql, string script = "")
        {
            if (script == "") script = sql.Script;
            if (sql.Mode == SQLMode.MySQL)
            {
                script = script.TrimEnd();
                if (!script.EndsWith("For Update", StringComparison.CurrentCultureIgnoreCase))
                {
                    script += " For Update";
                }
            }
            return sql.ExecuteDataSet(script);
        }

        /// <summary>
        /// 行级写锁定查询（异步版），需使用前开启事务。仅 MySQLHelper 有效，因 SQLiteHelper 事务可对全库进行锁定。
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="script">为空则使用 <see cref="SQLHelper.Script"/></param>
        /// <returns></returns>
        public static async Task<DataSet> ExecuteDataSetForUpdateAsync(this SQLHelper sql, string script = "")
        {
            if (script == "") script = sql.Script;
            if (sql.Mode == SQLMode.MySQL)
            {
                script = script.TrimEnd();
                if (!script.EndsWith("For Update", StringComparison.CurrentCultureIgnoreCase))
                {
                    script += " For Update";
                }
            }
            return await sql.ExecuteDataSetAsync(script);
        }

        /// <summary>
        /// 行级写锁定查询，调用 <see cref="ExecuteDataSetForUpdate"/> 查询返回第一行
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="script"></param>
        /// <returns></returns>
        public static DataRow? ExecuteDataRowForUpdate(this SQLHelper sql, string script = "")
        {
            DataSet ds = sql.ExecuteDataSet(script);
            return sql.Success ? ds.Tables[0].Rows[0] : null;
        }

        /// <summary>
        /// 行级写锁定查询（异步版），调用 <see cref="ExecuteDataSetForUpdateAsync"/> 查询返回第一行
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="script"></param>
        /// <returns></returns>
        public static async Task<DataRow?> ExecuteDataRowForUpdateAsync(this SQLHelper sql, string script = "")
        {
            DataSet ds = await sql.ExecuteDataSetForUpdateAsync(script);
            return sql.Success ? ds.Tables[0].Rows[0] : null;
        }
    }
}
