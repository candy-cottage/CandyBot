using CandyCottage.CandyBot.Models;
using Milimoe.FunGame.Core.Api.Utility;
using Milimoe.FunGame.Core.Entity;

namespace CandyBot.Tests;

/// <summary>
/// 单元测试的最小构造辅助：构造 <see cref="User"/> 与 <see cref="C2CMessage"/>。
/// 不引入任何框架，仅消除各测试间重复的样板代码。
/// </summary>
internal static class TestData
{
    public static User CreateUser(bool isAdmin = false, bool isOperator = false, long id = 1, string openid = "openid-1")
    {
        User user = Factory.GetUser();
        user.Id = id;
        user.Username = openid;
        user.IsAdmin = isAdmin;
        user.IsOperator = isOperator;
        return user;
    }

    public static C2CMessage CreateMessage(string detail, string openid = "openid-1") => new()
    {
        Detail = detail,
        Author = new() { Id = openid }
    };
}
