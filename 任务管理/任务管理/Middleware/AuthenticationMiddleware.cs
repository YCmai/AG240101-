using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Services;

namespace WarehouseManagementSystem.Middleware
{
    public class AuthenticationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<AuthenticationMiddleware> _logger;

        public AuthenticationMiddleware(RequestDelegate next, ILogger<AuthenticationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IUserService userService)
        {
            // 记录请求信息
            _logger.LogDebug($"AuthenticationMiddleware: 处理请求 {context.Request.Path} 来自 {context.Connection.RemoteIpAddress}");
            
            // 从Cookie中获取用户名（与AccountController保持一致）
            var username = context.Request.Cookies["UserSession"];
            _logger.LogDebug($"AuthenticationMiddleware: Cookie UserSession = {username}");
            
            if (!string.IsNullOrEmpty(username))
            {
                // 检查token过期时间
                var tokenExpireTimeStr = context.Request.Cookies["TokenExpireTime"];
                if (!string.IsNullOrEmpty(tokenExpireTimeStr) && 
                    DateTime.TryParse(tokenExpireTimeStr, out var tokenExpireTime))
                {
                    if (DateTime.Now > tokenExpireTime)
                    {
                        // Token已过期，清除Cookie并重定向到登录页面
                        _logger.LogWarning($"AuthenticationMiddleware: 用户 {username} 的token已过期");
                        context.Response.Cookies.Delete("UserSession");
                        context.Response.Cookies.Delete("TokenExpireTime");
                        context.Response.Redirect("/Account/Login");
                        return;
                    }
                }
                
                // 获取用户信息
                var user = await userService.GetUserByUsername(username);
                if (user != null && user.IsActive)
                {
                    // 将用户信息存储在HttpContext.Items中
                    context.Items["User"] = user;
                    _logger.LogDebug($"AuthenticationMiddleware: 用户 {username} 认证成功");
                }
                else
                {
                    _logger.LogWarning($"AuthenticationMiddleware: 用户 {username} 不存在或未激活");
                    // 用户不存在或未激活，清除Cookie并重定向到登录页面
                    context.Response.Cookies.Delete("UserSession");
                    context.Response.Cookies.Delete("TokenExpireTime");
                    context.Response.Redirect("/Account/Login");
                    return;
                }
            }
            else
            {
                _logger.LogDebug("AuthenticationMiddleware: 未找到UserSession Cookie");
            }

            await _next(context);
        }
    }
} 