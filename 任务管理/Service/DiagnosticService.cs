using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WarehouseManagementSystem.Service
{
    public class DiagnosticService : BackgroundService
    {
        private readonly ILogger<DiagnosticService> _logger;

        public DiagnosticService(ILogger<DiagnosticService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("诊断服务已启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 获取当前进程
                    var process = Process.GetCurrentProcess();
                    
                    // 获取内存使用情况
                    var memoryInMB = process.WorkingSet64 / (1024 * 1024);
                    
                    // 获取线程数量
                    var threadCount = process.Threads.Count;
                    
                    // 获取处理器时间
                    var cpuTime = process.TotalProcessorTime;
                    
                    // 获取句柄数
                    var handleCount = process.HandleCount;
                    
                    // 获取GC信息
                    var gcMemory = GC.GetTotalMemory(false) / (1024 * 1024);
                    var gen0Count = GC.CollectionCount(0);
                    var gen1Count = GC.CollectionCount(1);
                    var gen2Count = GC.CollectionCount(2);

                    _logger.LogInformation(
                        "系统诊断: 内存={MemoryMB}MB, GC内存={GcMemoryMB}MB, 线程数={ThreadCount}, 句柄数={HandleCount}, " +
                        "CPU时间={CpuTime}, GC次数=[G0:{Gen0}, G1:{Gen1}, G2:{Gen2}]",
                        memoryInMB, gcMemory, threadCount, handleCount, cpuTime, gen0Count, gen1Count, gen2Count);

                    // 检查异常情况
                    if (memoryInMB > 1000) // 超过1GB内存
                    {
                        _logger.LogWarning("内存使用过高: {MemoryMB}MB", memoryInMB);
                    }
                    
                    if (threadCount > 100) // 线程数过多
                    {
                        _logger.LogWarning("线程数过多: {ThreadCount}", threadCount);
                    }
                    
                    if (handleCount > 1000) // 句柄数过多
                    {
                        _logger.LogWarning("句柄数过多: {HandleCount}", handleCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "诊断服务执行出错");
                }

                // 每分钟执行一次
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
} 