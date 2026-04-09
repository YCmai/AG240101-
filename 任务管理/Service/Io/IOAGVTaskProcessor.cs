using System.Data;
using System.Net;

using Dapper;

using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Models.IO;

using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;
namespace WarehouseManagementSystem.Service.Io
{
    public class IOAGVTaskProcessor : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IDatabaseService _db;
        private readonly ILogger<IOAGVTaskProcessor> _logger;
        private readonly IIOService _ioService;


        public IOAGVTaskProcessor(IServiceProvider serviceProvider, IDatabaseService db, IIOService ioService, ILogger<IOAGVTaskProcessor> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _db = db;
            _ioService = ioService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("开始IO交互信号");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        using var conn = _db.CreateConnection();

                        var tasks = await conn.QueryAsync<RCS_IOAGV_Tasks>(
                            @"SELECT * FROM RCS_IOAGV_Tasks 
                            WHERE Status = 'Pending' 
                            AND CreatedTime > DATEADD(HOUR, -6, GETUTCDATE())
                            ORDER BY CreatedTime ASC");

                        foreach (var task in tasks)
                        {
                            try
                            {
                                if (!Enum.TryParse<EIOAddress>(task.SignalAddress, out EIOAddress addressEnum))
                                {
                                    _logger.LogWarning($"无效的信号地址: IP={task.DeviceIP}, Address={task.SignalAddress}");
                                    continue;
                                }

                                bool success = false;
                                switch (task.TaskType)
                                {
                                    case "ArrivalNotify":
                                    case "PassComplete":
                                        // 首先读取当前值
                                        var currentValue = await _ioService.ReadSignal(task.DeviceIP, addressEnum);
                                        if (currentValue == task.Value)
                                        {
                                            // 如果当前值已经是目标值，直接标记为成功
                                            success = true;
                                            _logger.LogInformation($"信号已经是目标值 - TaskId: {task.Id}, Device: {task.DeviceIP}, Address: {task.SignalAddress}, Value: {task.Value}");
                                        }
                                        else
                                        {
                                            // 值不同时才写入
                                            await _ioService.WriteSignal(task.DeviceIP, addressEnum, task.Value);
                                            success = await VerifyIOSignal(task.DeviceIP, addressEnum, task.Value);
                                        }
                                        break;

                                    case "PassCheck":
                                        // 读取通行信号
                                        success = await _ioService.ReadSignal(task.DeviceIP, addressEnum);
                                        break;
                                }

                                if (success)
                                {
                                    await UpdateTaskStatus(conn, task.Id, true);
                                    _logger.LogInformation(
                                        "任务处理成功 - TaskId: {TaskId}, Type: {TaskType}, Device: {DeviceIP}, Address: {Address}, 耗时: {Duration}ms",
                                        task.Id, task.TaskType, task.DeviceIP, task.SignalAddress,
                                        (DateTime.Now - task.CreatedTime).TotalMilliseconds);
                                }
                                else
                                {
                                    await UpdateTaskStatus(conn, task.Id, false);
                                    _logger.LogWarning(
                                        "任务处理未完成 - TaskId: {TaskId}, Type: {TaskType}, Device: {DeviceIP}, Address: {Address}",
                                        task.Id, task.TaskType, task.DeviceIP, task.SignalAddress);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "处理任务失败 - TaskId: {TaskId}, Type: {TaskType}, Device: {DeviceIP}, Address: {Address}",
                                    task.Id, task.TaskType, task.DeviceIP, task.SignalAddress);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理AGV任务时发生错误");
                }

                await Task.Delay(500, stoppingToken);
            }
        }

        private async Task<bool> VerifyIOSignal(string deviceIP, EIOAddress address, bool expectedValue)
        {
            try
            {
                // 尝试最多3次读取验证
                for (int i = 0; i < 3; i++)
                {
                    var actualValue = await _ioService.ReadSignal(deviceIP, address);
                    if (actualValue == expectedValue)
                    {
                        return true;
                    }
                    await Task.Delay(100); // 短暂延迟后重试
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"验证IO信号失败 - Device: {deviceIP}, Address: {address}");
                return false;
            }
        }

        private async Task UpdateTaskStatus(IDbConnection conn, int taskId, bool isCompleted)
        {
            await conn.ExecuteAsync(
                @"UPDATE RCS_IOAGV_Tasks 
                SET Status = @Status, 
                    CompletedTime = @CompletedTime,
                    LastUpdatedTime = @LastUpdatedTime
                WHERE Id = @Id",
                new
                {
                    Id = taskId,
                    Status = isCompleted ? "Completed" : "Pending",
                    CompletedTime = isCompleted ? DateTime.Now : (DateTime?)null,
                    LastUpdatedTime = DateTime.Now
                });
        }
    }

    // AGV任务实体类
    public class RCS_IOAGV_Tasks
    {
        public int Id { get; set; }
        /// <summary>
        /// 任务类型：ArrivalNotify(到达通知), PassCheck(通行检查), PassComplete(通行完成)
        /// </summary>
        public string TaskType { get; set; }
        /// <summary>
        /// 任务状态：Pending(待处理), Completed(已完成)
        /// </summary>
        public string Status { get; set; }
        /// <summary>
        /// IO设备IP地址
        /// </summary>
        public string DeviceIP { get; set; }
        /// <summary>
        ///  IO信号地址
        /// </summary>
        public string SignalAddress { get; set; }

        public DateTime CreatedTime { get; set; }
        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? CompletedTime { get; set; }
        /// <summary>
        ///  最后更新时间
        /// </summary>
        public DateTime? LastUpdatedTime { get; set; }


        public string TaskId { get; set; }


        public bool Value { get; set; }

    }

    public enum TaskType
    {
        ArrivalNotify,
        PassCheck,
        PassComplete
    }

    public enum TaskStatus
    {
        Pending,
        Completed,
        Failed
    }

}
