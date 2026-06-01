using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CandyCottage.CandyBot.Utilities
{
    /// <summary>
    /// 将控制器 / 操作限定为仅在 Development 环境可用。
    /// 在非 Development 环境下，路由仍被注册，但请求会被短路返回 404，
    /// 等效于该端点不存在——用于隐藏只在本地调试时才需要的接口（如 <c>TestController</c> 的加密工具）。
    /// </summary>
    public sealed class DevelopmentOnlyAttribute() : TypeFilterAttribute(typeof(DevelopmentOnlyFilter))
    {
        private sealed class DevelopmentOnlyFilter(IWebHostEnvironment env) : IResourceFilter
        {
            public void OnResourceExecuting(ResourceExecutingContext context)
            {
                if (!env.IsDevelopment())
                {
                    context.Result = new NotFoundResult();
                }
            }

            public void OnResourceExecuted(ResourceExecutedContext context) { }
        }
    }
}
