using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Library.SQLScript.Common;
using CandyCottage.CandyBot.Utilities;

namespace CandyCottage.CandyBot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [DevelopmentOnly]
    public class TestController(ILogger<TestController> logger, SQLHelper sql) : ControllerBase
    {
        [HttpGet("gethmacsha512")]
        public IActionResult UseHMACSHA512(string msg, string key)
        {
            return Ok(msg.Encrypt(key));
        }

        [HttpGet("gethmacsha256")]
        public IActionResult UseHMACSHA256(string msg, string key)
        {
            return Ok(Encryption.HmacSha256(msg, key));
        }

        [HttpGet("getrsa")]
        public IActionResult GetRSA(string msg, string key)
        {
            return Ok(Encryption.RSADecrypt(msg, key));
        }

        [HttpGet("setrsa")]
        public IActionResult SetRSA(string msg, string key)
        {
            return Ok(Encryption.RSAEncrypt(msg, key));
        }

        /// <summary>
        /// 1: public, 2: private
        /// </summary>
        /// <returns></returns>
        [HttpGet("getxml")]
        public IActionResult GetRSAXMLString()
        {
            using RSACryptoServiceProvider rsa = new();
            return Ok(new string[] { rsa.ToXmlString(false), rsa.ToXmlString(true) });
        }

        [HttpGet("getlastlogintime")]
        public IActionResult GetLastLoginTime()
        {
            sql.Parameters["@" + ServerLoginLogs.Column_ServerName] = "CandyBot";
            sql.ExecuteDataSet(ServerLoginLogs.Select_GetLastLoginTime() + " Where " + ServerLoginLogs.Column_ServerName + " = @" + ServerLoginLogs.Column_ServerName);
            if (sql.Success && DateTime.TryParse(sql.DataSet.Tables[0].Rows[0][ServerLoginLogs.Column_LastTime].ToString(), out DateTime date))
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("调用接口：获取服务器最后启动时间");
                string month = date.ToString("MMM", CultureInfo.InvariantCulture);
                int day = date.Day;
                string time = date.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                string msg = "服务器最后启动时间：" + $"{month}. {day}, {date.Year} {time}";
                return Ok(msg);
            }
            return Ok("无法调用此接口：与 SQL 服务器连接失败。");
        }
    }
}
