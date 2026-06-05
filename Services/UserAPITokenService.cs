using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;

namespace CandyCottage.CandyBot.Services
{
    public class UserAPITokenService(ILogger<UserService> logger, SQLHelper sql, CandyBotConfig candyBotConfig, IMemoryCache memoryCache)
    {
        /// <summary>
        /// 0: success, 1: sql error, 2: out of limit, 3: already exists
        /// </summary>
        /// <param name="username"></param>
        /// <param name="name"></param>
        /// <param name="secret"></param>
        /// <returns></returns>
        public int CreateApiTokenSecret(string username, string name, out string secret)
        {
            secret = "";

            try
            {
                // 检查令牌数量是否超过5个
                string script = "Select Count(TokenID) From ApiTokens Where Reference1 = @Username";
                sql.Parameters["@Username"] = username;
                sql.ExecuteDataSet(script);
                if (sql.Success && int.TryParse(sql.DataSet.Tables[0].Rows[0][0].ToString(), out int count))
                {
                    if (count >= 5)
                    {
                        return 2;
                    }
                    else
                    {
                        // 检查令牌名称是否存在
                        script = "Select Reference2 From ApiTokens Where Reference1 = @Username and Reference2 = @TokenName";
                        sql.Parameters["@Username"] = username;
                        sql.Parameters["@TokenName"] = name;
                        if (sql.ExecuteDataRow(script) != null)
                        {
                            return 3;
                        }

                        string tokenId = Guid.NewGuid().ToString("N");
                        secret = $"cd-{Guid.NewGuid():N}";
                        string secretHash = secret.Encrypt(candyBotConfig.ApiTokenSalt);
                        sql.Execute(Milimoe.FunGame.Core.Library.SQLScript.Common.ApiTokens.Insert_APIToken(sql, tokenId, secretHash, username, name));
                        return sql.Success ? 0 : 1;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {ex}", ex.Message);
            }

            return 1;
        }

        public string ShowTokenList(string username)
        {
            string script = "SELECT TokenID, Reference2 FROM ApiTokens WHERE Reference1 = @Username";
            sql.Parameters["@Username"] = username;
            sql.ExecuteDataSet(script);

            if (!sql.Success || sql.DataSet.Tables[0].Rows.Count == 0)
            {
                return $"你还没有任何令牌。可用指令：{"生成令牌 &lt;名称&gt;".CreateCmdInput("生成令牌")}";
            }

            StringBuilder sb = new();
            sb.AppendLine($"**🔑 令牌列表** {"生成令牌".CreateCmdInput()}");
            foreach (DataRow dr in sql.DataSet.Tables[0].Rows)
            {
                string tokenID = dr["TokenID"].ToString() ?? "";
                string name = dr["Reference2"].ToString() ?? "";
                sb.AppendLine($"**令牌名称：{name}**");
                sb.AppendLine($"ID：{tokenID}");
                sb.AppendLine($"操作：{"删除".CreateCmdInput($"删除令牌 {tokenID}")}");
            }

            return sb.ToString();
        }

        public string DeleteToken(string username, string tokenIdOrName)
        {
            string script = "SELECT SecretKey FROM ApiTokens WHERE (TokenID = @IdOrName OR Reference2 = @IdOrName) AND Reference1 = @Username";
            sql.Parameters["@IdOrName"] = tokenIdOrName;
            sql.Parameters["@Username"] = username;
            sql.ClearParametersAfterExecute = false;
            sql.ExecuteDataSet(script);
            sql.ClearParametersAfterExecute = true;
            if (!sql.Success)
            {
                return $"❌ 服务器错误，删除令牌失败。{"令牌列表".CreateCmdInput()}";
            }

            foreach (DataRow dr in sql.DataSet.Tables[0].Rows)
            {
                string secretKey = dr["SecretKey"].ToString() ?? "";
                memoryCache.Remove($"api::{secretKey}");
            }
            script = "DELETE FROM ApiTokens WHERE (TokenID = @IdOrName OR Reference2 = @IdOrName) AND Reference1 = @Username";
            sql.Execute(script);
            if (sql.Success)
            {
                return $"✅ 成功删除该令牌。{"令牌列表".CreateCmdInput()}";
            }

            return $"❌ 未找到匹配的令牌，请检查名称或ID是否正确。{"令牌列表".CreateCmdInput()}";
        }
    }
}
