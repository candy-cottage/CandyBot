using System.Data;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Milimoe.FunGame.Core.Api.Transmittal;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyCottage.CandyBot.Utilities
{
    public class ApiTokensBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SQLHelper sql, IMemoryCache memoryCache, CandyBotConfig config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // 获取 Authorization 标头
            if (!Request.Headers.TryGetValue("Authorization", out StringValues authHeader))
            {
                return AuthenticateResult.NoResult();
            }

            if (!AuthenticationHeaderValue.TryParse(authHeader.ToString(), out AuthenticationHeaderValue? headerValue) || !"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.Fail("Invalid Authorization header");
            }
            string token = headerValue.Parameter ?? "";
            if (string.IsNullOrEmpty(token))
            {
                return AuthenticateResult.Fail("Token is empty");
            }

            // 进行 token 验证
            string name;
            string role = "user";
            string hashKey = token.Encrypt(config.ApiTokenSalt);
            string cacheKey = $"api::{hashKey}";
            if (memoryCache.TryGetValue(cacheKey, out object? value) && value is (string n, string r))
            {
                name = n;
                role = r;
            }
            else
            {
                string query = "Select Users.* From Users Join ApiTokens On ApiTokens.Reference1 = Users.Username Where ApiTokens.SecretKey = @SecretKey";
                sql.Parameters["@SecretKey"] = hashKey;
                DataRow? dr = await sql.ExecuteDataRowAsync(query);
                if (dr is null)
                {
                    return AuthenticateResult.Fail("Invalid token");
                }

                User user = Factory.GetUser(dr);
                name = user.Username;
                if (user.IsAdmin)
                {
                    role = "admin";
                }
                else if (user.IsOperator)
                {
                    role = "op";
                }
                memoryCache.Set(cacheKey, (name, role), TimeSpan.FromMinutes(10));
            }

            // 创建 Claims 和身份标识
            Claim[] claims = [
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role)
            ];
            ClaimsIdentity identity = new(claims, Scheme.Name);
            ClaimsPrincipal principal = new(identity);
            AuthenticationTicket ticket = new(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
    }
}
