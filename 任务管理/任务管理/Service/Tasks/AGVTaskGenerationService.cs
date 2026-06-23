using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Dapper;
using WarehouseManagementSystem.Service.Io;
using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Models.IO;

namespace WarehouseManagementSystem.Service.Tasks
{
    public class AGVTaskGenerationService : BackgroundService
    {
        private readonly ILogger<AGVTaskGenerationService> _logger;
        private readonly IDatabaseService _db;
        private readonly ITaskGenerationService _taskGenerationService;
        private ConcurrentDictionary<string, DateTime> _di7SignalStartTimes = new ConcurrentDictionary<string, DateTime>();
        private ConcurrentDictionary<string, DateTime> _downlineSignalStartTimes = new ConcurrentDictionary<string, DateTime>();

        public AGVTaskGenerationService(
            ILogger<AGVTaskGenerationService> logger,
            IDatabaseService db,
            ITaskGenerationService taskGenerationService)
        {
            _logger = logger;
            _db = db;
            _taskGenerationService = taskGenerationService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AGV任务生成服务已启动");
            int consecutiveErrorCount = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessTasks();
                    _logger.LogInformation("完成执行ProcessTasks");
                    
                    // 成功执行后重置错误计数
                    consecutiveErrorCount = 0;
                    
                    // 正常间隔
                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    consecutiveErrorCount++;
                    
                    _logger.LogError(ex, $"生成AGV任务时发生错误 (第{consecutiveErrorCount}次连续错误)");
                    
                    // 根据连续错误次数增加等待时间
                    int delayMs = Math.Min(consecutiveErrorCount * 1000, 10000); // 最多等待10秒
                    _logger.LogWarning($"等待{delayMs/1000}秒后重试...");
                    
                    try
                    {
                        await Task.Delay(delayMs, stoppingToken);
                    }
                    catch (TaskCanceledException)
                    {
                        // 应用正在关闭，忽略此异常
                        break;
                    }
                }
            }
        }

        private async Task ProcessTasks()
        {
            // 使用超时控制
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // 10秒超时
            
            try
            {
                using var conn = _db.CreateConnection();
                
                // 添加命令超时
                var command = new CommandDefinition(
                    "SELECT * FROM RCS_IODevices WHERE IsEnabled = 1",
                    commandTimeout: 5); // 5秒超时
                    
                var devices = await conn.QueryAsync<RCS_IODevices>(command);

                foreach (var device in devices)
                {
                    // 创建带超时的CommandDefinition
                    command = new CommandDefinition(
                        "SELECT * FROM RCS_IOSignals WHERE DeviceId = @DeviceId",
                        new { DeviceId = device.Id },
                        commandTimeout: 5); // 5秒超时
                        
                    var signals = (await conn.QueryAsync<RCS_IOSignals>(command)).ToList();

                    if (device.Id <= 27) // 上料架任务
                    {
                        // 1. 首先检查 DI7 信号
                        var di7Signal = signals.FirstOrDefault(s => s.Address == "DI7");
                        if (di7Signal == null || di7Signal.Value != 1)
                        {
                            // DI7 信号不存在或未激活，清除该设备的计时
                            //_di7SignalStartTimes.TryRemove(device.Id.ToString(), out _);
                            continue;
                        }

                        // 2. 检查 DI7 信号持续时间
                        //var now = DateTime.Now;
                        //var signalStartTime = _di7SignalStartTimes.GetOrAdd(device.Id.ToString(), now);
                        
                        //if ((now - signalStartTime).TotalSeconds >= 1) // DI7 信号已持续 1 秒
                        //{
                            // 3. 检查 DI1-DI6 的信号
                            var diSignals = signals.Where(s => 
                                s.Address.StartsWith("DI") && 
                                s.Address != "DI7" &&
                                int.TryParse(s.Address.Substring(2), out int num) && 
                                num <= 6 &&
                                s.Value == 0).ToList();

                            // 4. 为每个激活的信号生成任务
                            foreach (var signal in diSignals)
                            {
                                await _taskGenerationService.GenerateAGVTask(signal, device);
                            }
                        //}
                    }
                    else if (device.Id >= 29) // 下料任务
                    {
                        foreach (var signal in signals)
                        {
                            if (signal.Value == 1 && signal.Address=="DI1")
                            {
                                _logger.LogInformation($"捕捉到{device.Id}的DI1信号闭合-1");

                                //var signalKey = $"{device.Id}_{signal.Address}";
                                //var now = DateTime.Now;

                                //// 如果信号首次出现，记录开始时间
                                //var signalStartTime = _downlineSignalStartTimes.GetOrAdd(signalKey, now);

                                //// 检查信号是否持续了1秒
                                //if ((now - signalStartTime).TotalSeconds >= 1)
                                //{
                                await _taskGenerationService.GenerateAGVTask(signal, device);
                                    // 任务生成后清除计时，避免重复生成
                                    //_downlineSignalStartTimes.TryRemove(signalKey, out _);
                                //}
                                //else
                                //{
                                //    _logger.LogWarning("触发持续时间不够，任务跳过");
                                //}
                            }
                            else
                            {
                                // 如果信号变为OFF，清除对应的计时
                                //_downlineSignalStartTimes.TryRemove($"{device.Id}_{signal.Address}", out _);
                            }
                        }
                    }
                }

                // 清理过期的信号记录
               // CleanupExpiredSignals();
            }
            catch (Exception ex) when (ex is TimeoutException || ex is TaskCanceledException)
            {
                _logger.LogWarning("任务处理操作超时");
                throw new TimeoutException("处理AGV任务生成超时", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "任务生成服务执行出错");
                throw; // 重新抛出异常，让上层处理重试逻辑
            }
        }

        private void CleanupExpiredSignals()
        {
            var now = DateTime.Now;
            var expiredTime = TimeSpan.FromMinutes(10); // 10分钟后清理

            // 清理上料架信号记录
            var expiredDi7Signals = _di7SignalStartTimes
                .Where(kvp => (now - kvp.Value) > expiredTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredDi7Signals)
            {
                _di7SignalStartTimes.TryRemove(key, out _);
            }

            // 清理下料信号记录
            var expiredDownlineSignals = _downlineSignalStartTimes
                .Where(kvp => (now - kvp.Value) > expiredTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredDownlineSignals)
            {
                _downlineSignalStartTimes.TryRemove(key, out _);
            }
        }
    }
} 