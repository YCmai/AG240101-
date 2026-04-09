using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service
{
    /// <summary>
    /// 缓存任务后台处理器。
    /// </summary>
    public class TaskCacheProcessor : BackgroundService
    {
        private readonly ILogger<TaskCacheProcessor> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDatabaseService _db;

        private const int RetentionDays = 5;
        private const int MaxCachedRows = 50000;
        private const int ProcessBatchSize = 500;
        private const int CleanupIntervalMinutes = 10;
        private const int DispatchCandidateBatchSize = 50;
        private const int SmallCacheThreshold = 3;
        private const int SmallCacheWaitSeconds = 10;
        private const int PrioritizedCachePriority = 10;

        private DateTime _lastCleanupTime = DateTime.MinValue;

        public TaskCacheProcessor(
            ILogger<TaskCacheProcessor> logger,
            IServiceProvider serviceProvider,
            IDatabaseService db)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _db = db;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("缓存任务处理器已启动");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessCachedTasks(stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理缓存任务时发生异常");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("缓存任务处理器已停止");
        }

        private async Task ProcessCachedTasks(CancellationToken stoppingToken)
        {
            try
            {
                using var connection = _db.CreateConnection();
                connection.Open();

                _ = RetentionDays;
                _ = MaxCachedRows;
                _ = ProcessBatchSize;
                _ = CleanupIntervalMinutes;
                _ = _lastCleanupTime;

                var pendingCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM RCS_TaskCache WITH (READPAST) WHERE Status = 0");

                if (pendingCount <= 0)
                {
                    return;
                }

                var prioritizedPendingCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM RCS_TaskCache WITH (READPAST) WHERE Status = 0 AND Priority = @Priority",
                    new { Priority = PrioritizedCachePriority });

                if (prioritizedPendingCount > 0)
                {
                    _logger.LogInformation(
                        "检测到{PrioritizedPendingCount}条抢单缓存任务，本轮先处理抢单任务，再处理普通缓存任务。",
                        prioritizedPendingCount);
                }
                else if (pendingCount == 1)
                {
                    var oldestCreateTime = await GetOldestPendingCreateTimeAsync(connection);
                    if (oldestCreateTime.HasValue)
                    {
                        var waitDeadline = oldestCreateTime.Value.AddSeconds(SmallCacheWaitSeconds);
                        var now = DateTime.Now;

                        if (waitDeadline > now)
                        {
                            var waitDuration = waitDeadline - now;
                            _logger.LogInformation(
                                "当前仅有1条缓存任务，兜底调度前等待{WaitSeconds}秒。",
                                Math.Ceiling(waitDuration.TotalSeconds));

                            await Task.Delay(waitDuration, stoppingToken);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "当前仅有1条缓存任务，且已等待至少{WaitSeconds}秒，本轮直接尝试下发。",
                                SmallCacheWaitSeconds);
                        }
                    }
                }
                else if (pendingCount < SmallCacheThreshold)
                {
                    _logger.LogInformation(
                        "缓存任务数量较少（{PendingCount}条），调度前等待{WaitSeconds}秒，给可能的聚合留时间。",
                        pendingCount,
                        SmallCacheWaitSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(SmallCacheWaitSeconds), stoppingToken);
                }

                var dispatchContext = await GetLatestCompletedTaskContextAsync(connection);
                var cachedTaskList = (await GetCandidateCachedTasksAsync(connection, dispatchContext)).ToList();

                _logger.LogInformation(
                    "本轮选出{Count}条缓存任务。最近一条已完成正式任务上下文：TaskIdentification={TaskIdentification}，ShelvesIdentification={ShelvesIdentification}",
                    cachedTaskList.Count,
                    dispatchContext?.TaskIdentification,
                    dispatchContext?.ShelvesIdentification);

                foreach (var cachedTask in cachedTaskList)
                {
                    _logger.LogInformation(
                        "开始处理缓存任务：Id={Id}，托盘号={PalletNumber}，请求号={RequestCode}，重试次数={RetryCount}，优先级={Priority}，TaskIdentification={TaskIdentification}，ShelvesIdentification={ShelvesIdentification}",
                        cachedTask.Id,
                        cachedTask.PalletNumber,
                        cachedTask.RequestCode,
                        cachedTask.RetryCount,
                        cachedTask.Priority,
                        cachedTask.TaskIdentification,
                        cachedTask.ShelvesIdentification);

                    try
                    {
                        var existingTask = await connection.QueryFirstOrDefaultAsync<RCS_UserTasks>(
                            "SELECT TOP 1 * FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber AND taskStatus != 30",
                            new { PalletNumber = cachedTask.PalletNumber });

                        if (existingTask != null)
                        {
                            await connection.ExecuteAsync(
                                "DELETE FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber",
                                new { PalletNumber = cachedTask.PalletNumber });

                            _logger.LogInformation(
                                "托盘{PalletNumber}已存在正式任务，已删除对应缓存任务。",
                                cachedTask.PalletNumber);
                            continue;
                        }

                        if (await AreLocationsAvailable(connection, cachedTask.SourcePosition, cachedTask.TargetPosition))
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var rcsTaskService = scope.ServiceProvider.GetRequiredService<IRcsTaskService>();

                            var request = new AddTaskRequest
                            {
                                toNum = cachedTask.RequestCode,
                                taskType = cachedTask.TaskType.ToString(),
                                sourceBin = cachedTask.SourcePosition,
                                destBin = cachedTask.TargetPosition,
                                material = cachedTask.MaterialCode,
                                materialNum = cachedTask.MaterialQuantity,
                                suNum = cachedTask.PalletNumber,
                                sourceType = cachedTask.SourceType,
                                destType = cachedTask.DestType,
                                createTime = cachedTask.CreateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                confirmTime = cachedTask.ConfirmTime?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                TaskIdentification = cachedTask.TaskIdentification,
                                ShelvesIdentification = cachedTask.ShelvesIdentification
                            };

                            var response = await rcsTaskService.AddTaskAsync(request);
                            _logger.LogInformation(
                                "缓存任务调用AddTaskAsync返回：Status={Status}，Message={Message}，FailType={FailType}，RequestCode={RequestCode}",
                                response.status,
                                response.message,
                                response.failType,
                                cachedTask.RequestCode);

                            if (response.status == "Success")
                            {
                                if (!string.IsNullOrEmpty(response.message) &&
                                    (response.message.Contains("缓存") ||
                                     response.message.Contains("已缓存")))
                                {
                                    await HandleStillCachedTaskAsync(connection, cachedTask);
                                }
                                else
                                {
                                    await connection.ExecuteAsync(
                                        "DELETE FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber",
                                        new { PalletNumber = cachedTask.PalletNumber });

                                    _logger.LogInformation(
                                        "缓存任务下发成功：PalletNumber={PalletNumber}，RequestCode={RequestCode}",
                                        cachedTask.PalletNumber,
                                        cachedTask.RequestCode);
                                }
                            }
                            else
                            {
                                await connection.ExecuteAsync(
                                    "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                                    new
                                    {
                                        RetryCount = cachedTask.RetryCount + 1,
                                        LastError = response.message,
                                        Id = cachedTask.Id
                                    });

                                _logger.LogWarning(
                                    "缓存任务下发失败：RequestCode={RequestCode}，Message={Message}，RetryCount={RetryCount}",
                                    cachedTask.RequestCode,
                                    response.message,
                                    cachedTask.RetryCount + 1);
                            }
                        }
                        else
                        {
                            await connection.ExecuteAsync(
                                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                                new
                                {
                                    RetryCount = cachedTask.RetryCount + 1,
                                    LastError = "起点或终点仍被锁定，等待下次重试",
                                    Id = cachedTask.Id
                                });

                            _logger.LogWarning(
                                "缓存任务本轮跳过，原因是起点或终点不可用。RequestCode={RequestCode}，SourcePosition={SourcePosition}，TargetPosition={TargetPosition}，RetryCount={RetryCount}",
                                cachedTask.RequestCode,
                                cachedTask.SourcePosition,
                                cachedTask.TargetPosition,
                                cachedTask.RetryCount + 1);
                        }

                        _logger.LogInformation(
                            "缓存任务处理完成：Id={Id}，PalletNumber={PalletNumber}，RequestCode={RequestCode}",
                            cachedTask.Id,
                            cachedTask.PalletNumber,
                            cachedTask.RequestCode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "处理单条缓存任务时发生异常：Id={Id}，PalletNumber={PalletNumber}，RequestCode={RequestCode}",
                            cachedTask.Id,
                            cachedTask.PalletNumber,
                            cachedTask.RequestCode);

                        if (cachedTask.RetryCount < 100)
                        {
                            await connection.ExecuteAsync(
                                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                                new
                                {
                                    RetryCount = cachedTask.RetryCount + 1,
                                    LastError = ex.Message,
                                    Id = cachedTask.Id
                                });
                        }
                        else
                        {
                            await connection.ExecuteAsync(
                                "UPDATE RCS_TaskCache SET Status = 3, LastError = @LastError WHERE Id = @Id",
                                new
                                {
                                    LastError = $"重试次数过多：{ex.Message}",
                                    Id = cachedTask.Id
                                });

                            _logger.LogWarning(
                                "缓存任务重试次数过多，已标记为取消。RequestCode={RequestCode}",
                                cachedTask.RequestCode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量处理缓存任务时发生异常");
            }
        }

        private async Task<DateTime?> GetOldestPendingCreateTimeAsync(IDbConnection connection)
        {
            return await connection.ExecuteScalarAsync<DateTime?>(
                "SELECT MIN(CreateTime) FROM RCS_TaskCache WITH (READPAST) WHERE Status = 0");
        }

        private async Task<LastCompletedTaskContext> GetLatestCompletedTaskContextAsync(IDbConnection connection)
        {
            return await connection.QueryFirstOrDefaultAsync<LastCompletedTaskContext>(@"
                SELECT TOP 1
                    TaskIdentification,
                    ShelvesIdentification
                FROM RCS_UserTasks WITH (READPAST)
                WHERE taskStatus = @TaskFinish
                ORDER BY ISNULL(endTime, creatTime) DESC, ID DESC",
                new { TaskFinish = (int)TaskStatuEnum.TaskFinish });
        }

        private async Task<IEnumerable<RCS_TaskCache>> GetCandidateCachedTasksAsync(IDbConnection connection, LastCompletedTaskContext dispatchContext)
        {
            var prioritizedTasks = (await connection.QueryAsync<RCS_TaskCache>(@"
                SELECT TOP (@BatchSize) *
                FROM RCS_TaskCache WITH (READPAST)
                WHERE Status = 0
                    AND Priority = @PrioritizedPriority
                ORDER BY
                    CreateTime ASC,
                    RetryCount ASC,
                    Id ASC",
                new
                {
                    BatchSize = DispatchCandidateBatchSize,
                    PrioritizedPriority = PrioritizedCachePriority
                })).ToList();

            var remainingSlots = DispatchCandidateBatchSize - prioritizedTasks.Count;
            if (remainingSlots <= 0)
            {
                return prioritizedTasks;
            }

            var regularTasks = await connection.QueryAsync<RCS_TaskCache>(@"
                SELECT TOP (@BatchSize) *
                FROM RCS_TaskCache WITH (READPAST)
                WHERE Status = 0
                    AND (Priority IS NULL OR Priority <> @PrioritizedPriority)
                ORDER BY
                    CASE
                        WHEN @LastShelvesIdentification IS NULL OR LTRIM(RTRIM(@LastShelvesIdentification)) = '' THEN 2
                        WHEN ShelvesIdentification IS NULL OR LTRIM(RTRIM(ShelvesIdentification)) = '' THEN 2
                        WHEN ShelvesIdentification = @LastShelvesIdentification THEN 0
                        ELSE 1
                    END ASC,
                    CASE
                        WHEN @LastTaskIdentification IS NULL OR TaskIdentification IS NULL THEN 2
                        WHEN TaskIdentification <> @LastTaskIdentification THEN 0
                        ELSE 1
                    END ASC,
                    CreateTime ASC,
                    RetryCount ASC,
                    Id ASC",
                new
                {
                    BatchSize = remainingSlots,
                    PrioritizedPriority = PrioritizedCachePriority,
                    LastTaskIdentification = dispatchContext?.TaskIdentification,
                    LastShelvesIdentification = dispatchContext?.ShelvesIdentification
                });

            return prioritizedTasks.Concat(regularTasks);
        }

        private async Task HandleStillCachedTaskAsync(IDbConnection connection, RCS_TaskCache cachedTask)
        {
            var newCacheCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber AND Status = 0",
                new { PalletNumber = cachedTask.PalletNumber });

            if (newCacheCount > 1)
            {
                await connection.ExecuteAsync(@"
                    DELETE FROM RCS_TaskCache
                    WHERE PalletNumber = @PalletNumber
                        AND Status = 0
                        AND Id NOT IN (
                            SELECT TOP 1 Id
                            FROM RCS_TaskCache
                            WHERE PalletNumber = @PalletNumber AND Status = 0
                            ORDER BY CreateTime ASC, Id ASC
                        )",
                    new { PalletNumber = cachedTask.PalletNumber });

                _logger.LogWarning(
                    "检测到重复缓存任务，已自动清理。PalletNumber={PalletNumber}，RequestCode={RequestCode}",
                    cachedTask.PalletNumber,
                    cachedTask.RequestCode);
            }

            await connection.ExecuteAsync(
                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                new
                {
                    RetryCount = cachedTask.RetryCount + 1,
                    LastError = "任务仍处于缓存状态，等待下次重试",
                    Id = cachedTask.Id
                });

            _logger.LogInformation(
                "缓存任务继续保留等待重试。PalletNumber={PalletNumber}，RequestCode={RequestCode}，RetryCount={RetryCount}",
                cachedTask.PalletNumber,
                cachedTask.RequestCode,
                cachedTask.RetryCount + 1);
        }

        private async Task<bool> AreLocationsAvailable(IDbConnection connection, string sourcePosition, string targetPosition)
        {
            try
            {
                var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                    "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                    new { NodeRemark = sourcePosition });

                if (sourceLocation == null || sourceLocation.Lock)
                {
                    return false;
                }

                var destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                    "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                    new { NodeRemark = targetPosition });

                if (destLocation == null || destLocation.Lock)
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查起点和终点可用性时发生异常");
                return false;
            }
        }

        private sealed class LastCompletedTaskContext
        {
            public int? TaskIdentification { get; set; }
            public string ShelvesIdentification { get; set; }
        }
    }
}
