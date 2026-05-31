using System.Data;
using System.Text;
using CandyCottage.CandyBot.Models;
using CandyCottage.CandyBot.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;
using Milimoe.FunGame.Core.Library.Constant;
using ProjectRedbud.FunGame.SQLQueryExtension;

namespace CandyCottage.CandyBot.Services
{
    public class UserService(ILogger<UserService> logger, SQLHelper sql, IMemoryCache memoryCache)
    {
        public string GetUserInfo(long userId)
        {
            User? user = sql.GetUserById(userId, true);
            if (user is null)
            {
                return "这家店的存在似乎被「抹除」了。";
            }

            UpdateCache(user);

            StringBuilder builder = new();
            builder.AppendLine("🍭 糖果屋仪表盘 🍫");
            builder.AppendLine($"🍬 昵称：{user.NickName} | {"修改昵称".CreateCmdInput("修改昵称为")}");
            builder.AppendLine($"🍬 UID：1{user.Id.ToString().PadLeft(6, '0')}");
            builder.AppendLine($"💰 {General.GameplayEquilibriumConstant.InGameCurrency}：{user.Inventory.Credits:0.00}");
            builder.AppendLine($"🎟️ {General.GameplayEquilibriumConstant.InGameMaterial}：{user.Inventory.Materials:0.00}");
            builder.AppendLine($"🍧 注册时间：{user.RegTime.ToString(General.GeneralDateTimeFormatChinese)}");
            builder.AppendLine($"🥯 最后访问：{user.LastTime.ToString(General.GeneralDateTimeFormatChinese)}");

            return builder.ToString();
        }

        public bool CreateSaved(string openid, out string msg)
        {
            sql.NewTransaction();
            try
            {
                string username = openid;
                string autokey = username.Encrypt(username);
                string password = Verification.CreateVerifyCode(VerifyCodeType.NumberVerifyCode, 6).Encrypt(username);
                sql.RegisterUser(username, password, "", "", autokey);
                User user = sql.GetUserByUsernameAndAutoKey(username, autokey, true) ?? throw new Exception("添加新用户失败，无法获取新用户数据。");
                user.NickName = $"Candy#{Verification.CreateVerifyCode(VerifyCodeType.NumberVerifyCode, 4)}";
                user.Inventory.Credits = 5000;
                user.Inventory.Materials = 100;
                sql.UpdateUser(user);
                msg = $"存档创建成功！你的昵称是：{user.NickName.CreateCmdInput("我的存档")}。";
                sql.Commit();
                UpdateCache(user);
                return true;
            }
            catch (Exception ex)
            {
                sql.Rollback();
                logger.LogError("Error: {ex}", ex);
                msg = "存档创建失败，请稍后再试。";
                return false;
            }
        }

        public string SubmitNicknameChange(long userId, string oldName, string newName)
        {
            try
            {
                sql.NewTransaction();
                // 检查是否有进行中的申请（Status = 0）
                string checkSql = "SELECT Id, SubmitCount FROM NicknameChangeRequests WHERE UserId = @uid AND Status = 0";
                sql.Parameters.Clear();
                sql.Parameters["@uid"] = userId;
                DataRow? existing = sql.ExecuteDataRowForUpdate(checkSql);

                if (existing != null)
                {
                    long requestId = Convert.ToInt64(existing["Id"]);
                    int submitCount = Convert.ToInt32(existing["SubmitCount"]) + 1;
                    string updateSql = @"UPDATE NicknameChangeRequests 
                        SET NewName = @newName, SubmitCount = @submitCount, SubmitTime = NOW(), Status = 0 
                        WHERE Id = @id";
                    sql.Parameters.Clear();
                    sql.Parameters["@newName"] = newName;
                    sql.Parameters["@submitCount"] = submitCount;
                    sql.Parameters["@id"] = requestId;
                    int rows = sql.Execute(updateSql);
                    if (rows > 0)
                    {
                        sql.Commit();
                        return $"已更新昵称修改申请，新昵称：{newName}，请静候佳音。";
                    }
                    sql.Rollback();
                    return "更新申请失败，请稍后重试。";
                }
                else
                {
                    string insertSql = @"
                    INSERT INTO NicknameChangeRequests (UserId, OldName, NewName, SubmitCount, Status)
                    VALUES (@uid, @oldName, @newName, 1, 0)";
                    sql.Parameters.Clear();
                    sql.Parameters["@uid"] = userId;
                    sql.Parameters["@oldName"] = oldName;
                    sql.Parameters["@newName"] = newName;
                    int rows = sql.Execute(insertSql);
                    if (rows > 0)
                    {
                        sql.Commit();
                        return $"已提交昵称修改申请：{oldName} → {newName}，请静候佳音。";
                    }
                    sql.Rollback();
                    return "提交失败，请稍后重试。";
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {ex.Message}", ex.Message);
                return $"提交失败，原因：{ex.Message}，请稍后重试。";
            }
        }

        public string CancelNicknameChange(long userId)
        {
            string sqlScript = @"
                UPDATE NicknameChangeRequests 
                SET Status = 3, ReviewTime = NOW() 
                WHERE UserId = @uid AND Status = 0";
            sql.Parameters["@uid"] = userId;
            int rows = sql.Execute(sqlScript);
            return rows > 0 ? "已取消昵称修改申请。" : "没有找到进行中的申请。";
        }

        public DataTable GetPendingRequests(int page, int pageSize)
        {
            int offset = (page - 1) * pageSize;
            string sqlScript = $@"
                SELECT r.Id, r.UserId, u.Username, u.Nickname AS CurrentName, r.NewName, r.SubmitTime, r.SubmitCount
                FROM NicknameChangeRequests r
                LEFT JOIN Users u ON r.UserId = u.Id
                WHERE r.Status = 0
                ORDER BY r.SubmitTime ASC
                LIMIT {pageSize} OFFSET {offset}";
            DataSet ds = sql.ExecuteDataSet(sqlScript);
            return ds.Tables.Count > 0 ? ds.Tables[0] : new DataTable();
        }

        public int GetPendingCount()
        {
            string sqlScript = "SELECT COUNT(*) FROM NicknameChangeRequests WHERE Status = 0";
            DataRow? row = sql.ExecuteDataRow(sqlScript);
            return row != null ? Convert.ToInt32(row[0]) : 0;
        }

        public bool ApproveNicknameChange(long requestId, out long requestUserId, long reviewerId)
        {
            // 开始事务，确保两步操作原子性
            sql.NewTransaction();
            try
            {
                // 1. 获取申请详情
                string selectSql = "SELECT UserId, NewName FROM NicknameChangeRequests WHERE Id = @id AND Status = 0";
                sql.Parameters["@id"] = requestId;
                DataRow? req = sql.ExecuteDataRowForUpdate(selectSql);
                if (req == null)
                {
                    sql.Rollback();
                    requestUserId = 0;
                    return false;
                }

                long userId = Convert.ToInt64(req["UserId"]);
                string newName = req["NewName"].ToString() ?? "";

                // 2. 更新 Users 表的 Nickname
                string updateUserSql = "UPDATE Users SET Nickname = @newName WHERE Id = @uid";
                sql.Parameters["@newName"] = newName;
                sql.Parameters["@uid"] = userId;
                int userRows = sql.Execute(updateUserSql);
                if (userRows == 0) throw new Exception("更新用户昵称失败");

                // 3. 更新申请状态为通过
                string updateReqSql = @"
                    UPDATE NicknameChangeRequests 
                    SET Status = 1, ReviewerId = @reviewerId, ReviewTime = NOW() 
                    WHERE Id = @id";
                sql.Parameters["@reviewerId"] = reviewerId;
                sql.Parameters["@id"] = requestId;
                int reqRows = sql.Execute(updateReqSql);
                if (reqRows == 0) throw new Exception("更新申请状态失败");

                if (sql.GetUserById(userId) is User user)
                {
                    UpdateCache(user);
                }
                sql.Commit();
                requestUserId = userId;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error: {ex.Message}", ex.Message);
                sql.Rollback();
                requestUserId = 0;
                return false;
            }
        }

        public bool RejectNicknameChange(long requestId, out long requestUserId, long reviewerId, string reason = "")
        {
            string script = @"
                UPDATE NicknameChangeRequests 
                SET Status = 2, ReviewerId = @reviewerId, ReviewTime = NOW(), RejectReason = @reason 
                WHERE Id = @id AND Status = 0";
            sql.Parameters["@reviewerId"] = reviewerId;
            sql.Parameters["@reason"] = reason;
            sql.Parameters["@id"] = requestId;
            int rows = sql.Execute(script);
            sql.Parameters["@id"] = requestId;
            requestUserId = sql.ExecuteDataRow("SELECT UserId, NewName FROM NicknameChangeRequests WHERE Id = @id") is DataRow dr ? Convert.ToInt64(dr["UserId"]) : 0;
            return rows > 0;
        }

        public void UpdateCache(User user)
        {
            memoryCache.Set($"bot::{user.Username}", user, TimeSpan.FromMinutes(10));
        }
    }
}
