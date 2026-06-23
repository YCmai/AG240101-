using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace WarehouseManagementSystem.Middleware
{
    public class PerformanceMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<PerformanceMiddleware> _logger;

        public PerformanceMiddleware(RequestDelegate next, ILogger<PerformanceMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            var originalBodyStream = context.Response.Body;

            try
            {
                using var memoryStream = new MemoryStream();
                context.Response.Body = memoryStream;

                await _next(context);

                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(originalBodyStream);

                stopwatch.Stop();
                var elapsed = stopwatch.ElapsedMilliseconds;

                // 记录慢请求
                if (elapsed > 1000) // 超过1秒的请求
                {
                    _logger.LogWarning($"慢请求检测: {context.Request.Method} {context.Request.Path} - {elapsed}ms - 来自 {context.Connection.RemoteIpAddress}");
                }
                else if (elapsed > 500) // 超过500ms的请求
                {
                    _logger.LogInformation($"较慢请求: {context.Request.Method} {context.Request.Path} - {elapsed}ms");
                }
            }
            finally
            {
                context.Response.Body = originalBodyStream;
            }
        }
    }
} 