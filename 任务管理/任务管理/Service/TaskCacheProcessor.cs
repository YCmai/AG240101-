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
        private const int RegularTaskScanBatchSize = 200;
        private const int AutoDestinationTaskBatchSize = 3;
        private const int PrioritizedCachePriority = 10;
        private const int FastScanSeconds = 3;
        private const int IdleScanSeconds = 20;

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
                    var hasPendingTasks = await ProcessCachedTasks(stoppingToken);
                    var delaySeconds = hasPendingTasks ? FastScanSeconds : IdleScanSeconds;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "处理缓存任务时发生异常");
                    await Task.Delay(TimeSpan.FromSeconds(FastScanSeconds), stoppingToken);
                }
            }

            _logger.LogInformation("缓存任务处理器已停止");
        }

        private async Task<bool> ProcessCachedTasks(CancellationToken stoppingToken)
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
                    return false;
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

                var autoDestinationTasks = await ProcessAutoDestinationCachedTasksAsync(connection);
                var dispatchContext = await GetLatestCompletedTaskContextAsync(connection);
                var cachedTaskList = (await GetCandidateCachedTasksAsync(connection, dispatchContext)).ToList();

                _logger.LogInformation(
                    "本轮专项处理{AutoDestinationCount}条自动终点缓存任务，普通通道选出{Count}条缓存任务。最近一条已完成正式任务上下文：TaskIdentification={TaskIdentification}，ShelvesIdentification={ShelvesIdentification}",
                    autoDestinationTasks,
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

                        if (await AreLocationsAvailable(connection, cachedTask))
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
                                    await RefreshCachedTaskSnapshotAsync(connection, cachedTask);
                                    await HandleStillCachedTaskAsync(connection, cachedTask, response.message);
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
                            var unavailableReason = BuildUnavailableReason(cachedTask);
                            await connection.ExecuteAsync(
                                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                                new
                                {
                                    RetryCount = cachedTask.RetryCount + 1,
                                    LastError = unavailableReason,
                                    Id = cachedTask.Id
                                });

                            _logger.LogWarning(
                                "缓存任务本轮跳过，原因是起点或终点不可用。RequestCode={RequestCode}，SourcePosition={SourcePosition}，TargetPosition={TargetPosition}，RetryCount={RetryCount}，原因={Reason}",
                                cachedTask.RequestCode,
                                cachedTask.SourcePosition,
                                cachedTask.TargetPosition,
                                cachedTask.RetryCount + 1,
                                unavailableReason);
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

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量处理缓存任务时发生异常");
                return true;
            }
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

        /// <summary>
        /// 专项处理需要系统自动分配终点的缓存任务。
        /// </summary>
        /// <param name="connection">缓存处理器当前数据库连接。</param>
        /// <returns>本轮专项通道处理的缓存任务数量。</returns>
        private async Task<int> ProcessAutoDestinationCachedTasksAsync(IDbConnection connection)
        {
            var autoDestinationCandidates = (await connection.QueryAsync<RCS_TaskCache>(@"
                SELECT TOP (@BatchSize) *
                FROM RCS_TaskCache WITH (READPAST)
                WHERE Status = 0
                    AND TaskType IN (@FgHandoverTaskType, @ShipmentTaskType)
                ORDER BY DATEADD(second, ISNULL(RetryCount, 0) * 30, CreateTime) ASC",
                new
                {
                    BatchSize = AutoDestinationTaskBatchSize,
                    FgHandoverTaskType = (int)RCS_UserTasks.TaskType.fgHandover,
                    ShipmentTaskType = (int)RCS_UserTasks.TaskType.shipment
                })).ToList();

            if (!autoDestinationCandidates.Any())
            {
                return 0;
            }

            _logger.LogInformation(
                "自动终点专项通道本轮发现{Count}条fgHandover/shipment缓存任务。",
                autoDestinationCandidates.Count);

            var processedCount = 0;
            foreach (var cachedTask in autoDestinationCandidates)
            {
                processedCount++;
                await ProcessSingleAutoDestinationCachedTaskAsync(cachedTask);
            }

            return processedCount;
        }

        /// <summary>
        /// 处理单条 fgHandover/shipment 缓存任务。
        /// </summary>
        /// <param name="cachedTask">待处理的缓存任务。</param>
        /// <returns>代表异步处理操作的任务。</returns>
        private async Task ProcessSingleAutoDestinationCachedTaskAsync(RCS_TaskCache cachedTask)
        {
            using var scope = _serviceProvider.CreateScope();
            var rcsTaskService = scope.ServiceProvider.GetRequiredService<IRcsTaskService>();
            var autoDestinationAllocator = scope.ServiceProvider.GetRequiredService<IAutoDestinationAllocator>();

            using var connection = _db.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var allocatedTargetPosition = default(string);
            var transactionCompleted = false;
            try
            {
                _logger.LogInformation(
                    "自动终点专项通道开始处理缓存任务：Id={Id}，TaskType={TaskType}，RequestCode={RequestCode}，PalletNumber={PalletNumber}，Source={SourcePosition}，RetryCount={RetryCount}",
                    cachedTask.Id,
                    cachedTask.TaskType,
                    cachedTask.RequestCode,
                    cachedTask.PalletNumber,
                    cachedTask.SourcePosition,
                    cachedTask.RetryCount);

                var existingTask = await connection.QueryFirstOrDefaultAsync<RCS_UserTasks>(
                    "SELECT TOP 1 * FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber AND taskStatus != 30",
                    new { cachedTask.PalletNumber },
                    transaction);

                if (existingTask != null)
                {
                    await connection.ExecuteAsync(
                        "DELETE FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber",
                        new { cachedTask.PalletNumber },
                        transaction);
                    transaction.Commit();
                    transactionCompleted = true;

                    _logger.LogInformation(
                        "自动终点专项通道发现托盘已存在正式任务，已删除缓存：PalletNumber={PalletNumber}",
                        cachedTask.PalletNumber);
                    return;
                }

                var sourceAvailability = await GetAutoDestinationSourceAvailabilityAsync(connection, transaction, cachedTask);
                if (!sourceAvailability.IsAvailable)
                {
                    await UpdateCachedTaskRetryAsync(connection, transaction, cachedTask, sourceAvailability.UnavailableReason);
                    transaction.Commit();
                    transactionCompleted = true;

                    _logger.LogWarning(
                        "自动终点专项通道跳过缓存任务，起点不可用：RequestCode={RequestCode}，Reason={Reason}",
                        cachedTask.RequestCode,
                        sourceAvailability.UnavailableReason);
                    return;
                }

                var allocationResult = await autoDestinationAllocator.TryAllocateAsync(
                    cachedTask.TaskType.ToString(),
                    connection,
                    transaction);

                if (!allocationResult.Success)
                {
                    await UpdateCachedTaskRetryAsync(connection, transaction, cachedTask, allocationResult.Message);
                    transaction.Commit();
                    transactionCompleted = true;

                    _logger.LogWarning(
                        "自动终点专项通道未分配到终点，缓存任务继续等待：RequestCode={RequestCode}，Reason={Reason}",
                        cachedTask.RequestCode,
                        allocationResult.Message);
                    return;
                }

                allocatedTargetPosition = allocationResult.TargetPosition;
                await UpdateCachedTaskAllocatedTargetAsync(connection, transaction, cachedTask, allocationResult);
                transaction.Commit();
                transactionCompleted = true;

                var request = BuildAddTaskRequest(cachedTask);
                request.destBin = allocatedTargetPosition;
                request.IsDestinationPreAllocated = true;

                var response = await rcsTaskService.AddTaskAsync(request);
                _logger.LogInformation(
                    "自动终点专项通道调用AddTaskAsync返回：Status={Status}，Message={Message}，FailType={FailType}，RequestCode={RequestCode}，AllocatedTarget={AllocatedTarget}",
                    response.status,
                    response.message,
                    response.failType,
                    cachedTask.RequestCode,
                    allocatedTargetPosition);

                if (response.status == "Success" &&
                    (string.IsNullOrWhiteSpace(response.message) ||
                     (!response.message.Contains("缓存") && !response.message.Contains("已缓存"))))
                {
                    using var cleanupConnection = _db.CreateConnection();
                    cleanupConnection.Open();
                    await cleanupConnection.ExecuteAsync(
                        "DELETE FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber",
                        new { cachedTask.PalletNumber });

                    _logger.LogInformation(
                        "自动终点专项通道下发成功并删除缓存：PalletNumber={PalletNumber}，RequestCode={RequestCode}，Target={Target}",
                        cachedTask.PalletNumber,
                        cachedTask.RequestCode,
                        allocatedTargetPosition);
                    return;
                }

                await ReleaseAutoDestinationAfterAddTaskFailureAsync(
                    autoDestinationAllocator,
                    allocatedTargetPosition,
                    cachedTask,
                    response.message);
            }
            catch (Exception ex)
            {
                try
                {
                    if (!transactionCompleted && transaction.Connection != null)
                    {
                        transaction.Rollback();
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "自动终点专项通道事务回滚失败");
                }

                if (!string.IsNullOrWhiteSpace(allocatedTargetPosition))
                {
                    await ReleaseAutoDestinationAfterAddTaskFailureAsync(
                        autoDestinationAllocator,
                        allocatedTargetPosition,
                        cachedTask,
                        $"自动终点专项通道异常：{ex.Message}");
                }
                else
                {
                    await UpdateCachedTaskRetryWithoutTransactionAsync(cachedTask, $"自动终点专项通道异常：{ex.Message}");
                }

                _logger.LogError(
                    ex,
                    "自动终点专项通道处理缓存任务异常：Id={Id}，RequestCode={RequestCode}",
                    cachedTask.Id,
                    cachedTask.RequestCode);
            }
        }

        /// <summary>
        /// 只检查自动终点任务的起点是否可用。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <param name="transaction">数据库事务。</param>
        /// <param name="cachedTask">待检查缓存任务。</param>
        /// <returns>起点可用性结果。</returns>
        private async Task<CachedTaskLocationAvailability> GetAutoDestinationSourceAvailabilityAsync(
            IDbConnection connection,
            IDbTransaction transaction,
            RCS_TaskCache cachedTask)
        {
            var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = cachedTask.SourcePosition },
                transaction);

            if (sourceLocation == null)
            {
                return CachedTaskLocationAvailability.Unavailable($"起点 {cachedTask.SourcePosition} 不存在库位配置中");
            }

            if (sourceLocation.Lock)
            {
                return CachedTaskLocationAvailability.Unavailable($"起点 {cachedTask.SourcePosition} 已被锁定，未进入自动终点分配");
            }

            return CachedTaskLocationAvailability.Available();
        }

        /// <summary>
        /// 将专项通道预分配的终点写回缓存表，便于页面和日志看到本轮分配结果。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <param name="transaction">数据库事务。</param>
        /// <param name="cachedTask">当前缓存任务。</param>
        /// <param name="allocationResult">自动终点分配结果。</param>
        /// <returns>代表异步更新操作的任务。</returns>
        private async Task UpdateCachedTaskAllocatedTargetAsync(
            IDbConnection connection,
            IDbTransaction transaction,
            RCS_TaskCache cachedTask,
            AutoDestinationAllocationResult allocationResult)
        {
            await connection.ExecuteAsync(@"
                UPDATE RCS_TaskCache
                SET TargetPosition = @TargetPosition,
                    LastError = @LastError
                WHERE Id = @Id",
                new
                {
                    TargetPosition = allocationResult.TargetPosition,
                    LastError = allocationResult.Message,
                    cachedTask.Id
                },
                transaction);
        }

        /// <summary>
        /// 释放预分配终点并更新缓存原因。
        /// </summary>
        /// <param name="autoDestinationAllocator">自动终点分配服务。</param>
        /// <param name="allocatedTargetPosition">已预分配终点。</param>
        /// <param name="cachedTask">当前缓存任务。</param>
        /// <param name="reason">失败原因。</param>
        /// <returns>代表异步释放与更新操作的任务。</returns>
        private async Task ReleaseAutoDestinationAfterAddTaskFailureAsync(
            IAutoDestinationAllocator autoDestinationAllocator,
            string allocatedTargetPosition,
            RCS_TaskCache cachedTask,
            string reason)
        {
            using var connection = _db.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            try
            {
                await autoDestinationAllocator.ReleaseAsync(allocatedTargetPosition, connection, transaction);
                var latestReason = string.IsNullOrWhiteSpace(reason)
                    ? "AddTaskAsync未生成正式任务，已释放自动预分配终点"
                    : $"自动分配终点 {allocatedTargetPosition} 后未生成正式任务：{reason}，已释放预分配终点";

                await UpdateCachedTaskRetryAsync(connection, transaction, cachedTask, latestReason);
                transaction.Commit();

                _logger.LogWarning(
                    "自动终点专项通道已释放预分配终点并保留缓存：RequestCode={RequestCode}，Target={Target}，Reason={Reason}",
                    cachedTask.RequestCode,
                    allocatedTargetPosition,
                    latestReason);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// 更新缓存任务重试次数和缓存原因。
        /// </summary>
        /// <param name="connection">数据库连接。</param>
        /// <param name="transaction">数据库事务。</param>
        /// <param name="cachedTask">当前缓存任务。</param>
        /// <param name="reason">缓存原因。</param>
        /// <returns>代表异步更新操作的任务。</returns>
        private static async Task UpdateCachedTaskRetryAsync(
            IDbConnection connection,
            IDbTransaction transaction,
            RCS_TaskCache cachedTask,
            string reason)
        {
            await connection.ExecuteAsync(
                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                new
                {
                    RetryCount = cachedTask.RetryCount + 1,
                    LastError = string.IsNullOrWhiteSpace(reason) ? "自动终点专项通道等待下次重试" : reason,
                    cachedTask.Id
                },
                transaction);
        }

        private async Task UpdateCachedTaskRetryWithoutTransactionAsync(RCS_TaskCache cachedTask, string reason)
        {
            using var connection = _db.CreateConnection();
            connection.Open();
            await connection.ExecuteAsync(
                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                new
                {
                    RetryCount = cachedTask.RetryCount + 1,
                    LastError = reason,
                    cachedTask.Id
                });
        }

        private static AddTaskRequest BuildAddTaskRequest(RCS_TaskCache cachedTask)
        {
            return new AddTaskRequest
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
        }

        private async Task<IEnumerable<RCS_TaskCache>> GetCandidateCachedTasksAsync(IDbConnection connection, LastCompletedTaskContext dispatchContext)
        {
            var busyLaneKeys = await GetBusyLaneKeysAsync(connection);

            var prioritizedCandidates = (await connection.QueryAsync<RCS_TaskCache>(@"
                SELECT TOP (@BatchSize) *
                FROM RCS_TaskCache WITH (READPAST)
                WHERE Status = 0
                    AND Priority = @PrioritizedPriority
                    AND TaskType NOT IN (@FgHandoverTaskType, @ShipmentTaskType)
                ORDER BY
                    DATEADD(second, ISNULL(RetryCount, 0) * 30, CreateTime) ASC",
                new
                {
                    BatchSize = DispatchCandidateBatchSize,
                    PrioritizedPriority = PrioritizedCachePriority,
                    FgHandoverTaskType = (int)RCS_UserTasks.TaskType.fgHandover,
                    ShipmentTaskType = (int)RCS_UserTasks.TaskType.shipment
                })).ToList();

            var prioritizedTasks = SelectPrioritizedTasksByLane(prioritizedCandidates, busyLaneKeys).ToList();

            var remainingSlots = DispatchCandidateBatchSize - prioritizedTasks.Count;
            if (remainingSlots <= 0)
            {
                return prioritizedTasks;
            }

            var prioritizedLaneKeys = GetDispatchLaneKeys(prioritizedTasks);

            var regularTaskCandidates = (await connection.QueryAsync<RCS_TaskCache>(@"
                SELECT TOP (@ScanBatchSize) *
                FROM RCS_TaskCache WITH (READPAST)
                WHERE Status = 0
                    AND (Priority IS NULL OR Priority <> @PrioritizedPriority)
                    AND TaskType NOT IN (@FgHandoverTaskType, @ShipmentTaskType)
                ORDER BY
                    DATEADD(second, ISNULL(RetryCount, 0) * 30, CreateTime) ASC",
                new
                {
                    ScanBatchSize = RegularTaskScanBatchSize,
                    PrioritizedPriority = PrioritizedCachePriority,
                    FgHandoverTaskType = (int)RCS_UserTasks.TaskType.fgHandover,
                    ShipmentTaskType = (int)RCS_UserTasks.TaskType.shipment
                })).ToList();

            var regularTasks = (await SelectRegularTasksByLaneAsync(
                    regularTaskCandidates,
                    dispatchContext,
                    connection,
                    busyLaneKeys,
                    prioritizedLaneKeys,
                    remainingSlots))
                .ToList();

            return prioritizedTasks.Concat(regularTasks);
        }

        private static HashSet<string> GetDispatchLaneKeys(IEnumerable<RCS_TaskCache> tasks)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in tasks)
            {
                foreach (var key in GetDispatchLaneKeys(task))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private static IEnumerable<string> GetDispatchLaneKeys(RCS_TaskCache task)
        {
            if (task == null)
            {
                yield break;
            }

            var shelfKey = GetDispatchLaneKey(task.ShelvesIdentification);
            if (!string.IsNullOrWhiteSpace(shelfKey))
            {
                yield return shelfKey;
            }
        }

        private static bool BelongsToAnyDispatchLane(RCS_TaskCache task, HashSet<string> prioritizedLaneKeys)
        {
            if (prioritizedLaneKeys == null || prioritizedLaneKeys.Count == 0)
            {
                return false;
            }

            foreach (var key in GetDispatchLaneKeys(task))
            {
                if (prioritizedLaneKeys.Contains(key))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeDispatchLaneKey(string lane)
        {
            return string.IsNullOrWhiteSpace(lane)
                ? null
                : $"LANE-{lane.Trim()}";
        }

        private async Task HandleStillCachedTaskAsync(IDbConnection connection, RCS_TaskCache cachedTask, string latestMessage)
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
                            ORDER BY
                                CASE WHEN ISNULL(Priority, 0) = @PrioritizedPriority THEN 0 ELSE 1 END ASC,
                                ISNULL(Priority, 0) DESC,
                                CreateTime ASC,
                                Id ASC
                        )",
                    new
                    {
                        PalletNumber = cachedTask.PalletNumber,
                        PrioritizedPriority = PrioritizedCachePriority
                    });

                _logger.LogWarning(
                    "检测到重复缓存任务，已自动清理。PalletNumber={PalletNumber}，RequestCode={RequestCode}",
                    cachedTask.PalletNumber,
                    cachedTask.RequestCode);
            }

            await connection.ExecuteAsync(
                @"UPDATE RCS_TaskCache
                  SET RetryCount = @RetryCount,
                      LastError = @LastError
                  WHERE Id = @Id",
                new
                {
                    RetryCount = cachedTask.RetryCount + 1,
                    LastError = string.IsNullOrWhiteSpace(latestMessage)
                        ? "任务仍处于缓存状态，等待下次重试"
                        : latestMessage,
                    Id = cachedTask.Id
                });

            _logger.LogInformation(
                "缓存任务继续保留等待重试。PalletNumber={PalletNumber}，RequestCode={RequestCode}，RetryCount={RetryCount}",
                cachedTask.PalletNumber,
                cachedTask.RequestCode,
                cachedTask.RetryCount + 1);
        }

        private async Task<bool> AreLocationsAvailable(IDbConnection connection, RCS_TaskCache cachedTask)
        {
            var availability = await GetCachedTaskLocationAvailabilityAsync(connection, cachedTask);
            return availability.IsAvailable;
        }

        /// <summary>
        /// 在将缓存任务发送到 AddTaskAsync 之前，检查其是否可以重试。
        /// </summary>
        /// <param name="connection">用于读取当前库位状态的数据库连接。</param>
        /// <param name="cachedTask">要检查的缓存任务。</param>
        /// <returns>可用性结果，包含任务应继续保持缓存状态时的原因。</returns>
        private async Task<CachedTaskLocationAvailability> GetCachedTaskLocationAvailabilityAsync(IDbConnection connection, RCS_TaskCache cachedTask)
        {
            try
            {
                if (cachedTask == null)
                {
                    return CachedTaskLocationAvailability.Unavailable("缓存任务数据为空，等待下次重试");
                }

                var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                    "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                    new { NodeRemark = cachedTask.SourcePosition });

                if (sourceLocation == null)
                {
                    return CachedTaskLocationAvailability.Unavailable($"起点 {cachedTask.SourcePosition} 不存在库位配置中");
                }

                if (sourceLocation.Lock)
                {
                    return CachedTaskLocationAvailability.Unavailable($"起点 {cachedTask.SourcePosition} 仍被锁定，等待下次重试");
                }

                if (ShouldSkipTargetAvailabilityCheck(cachedTask))
                {
                    return CachedTaskLocationAvailability.Available();
                }

                var destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                    "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                    new { NodeRemark = cachedTask.TargetPosition });

                if (destLocation == null)
                {
                    return CachedTaskLocationAvailability.Unavailable($"终点 {cachedTask.TargetPosition} 不存在库位配置中");
                }

                if (destLocation.Lock)
                {
                    return CachedTaskLocationAvailability.Unavailable($"终点 {cachedTask.TargetPosition} 仍被锁定，等待下次重试");
                }

                if (ShouldCheckTargetOccupancy(cachedTask) && IsLocationOccupied(destLocation))
                {
                    return CachedTaskLocationAvailability.Unavailable($"终点 {cachedTask.TargetPosition} 已被占用，等待下次重试");
                }

                return CachedTaskLocationAvailability.Available();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查起点和终点可用性时发生异常");
                return CachedTaskLocationAvailability.Unavailable("检查起点和终点可用性时发生异常，等待下次重试");
            }
        }

        /// <summary>
        /// 记录在同巷道备选任务选择期间，缓存任务被跳过的原因。
        /// </summary>
        /// <param name="connection">用于更新缓存行数据的数据库连接。</param>
        /// <param name="cachedTask">当前不可派发的缓存任务。</param>
        /// <param name="unavailableReason">可用性预检检测到的不可用原因。</param>
        /// <returns>代表异步更新操作的任务。</returns>
        private async Task MarkCachedTaskUnavailableAsync(
            IDbConnection connection,
            RCS_TaskCache cachedTask,
            string unavailableReason)
        {
            if (cachedTask == null)
            {
                return;
            }

            var latestReason = string.IsNullOrWhiteSpace(unavailableReason)
                ? BuildUnavailableReason(cachedTask)
                : unavailableReason;

            await connection.ExecuteAsync(
                "UPDATE RCS_TaskCache SET RetryCount = @RetryCount, LastError = @LastError WHERE Id = @Id",
                new
                {
                    RetryCount = cachedTask.RetryCount + 1,
                    LastError = latestReason,
                    Id = cachedTask.Id
                });

            _logger.LogInformation(
                "普通缓存任务在同巷道补选前被跳过：RequestCode={RequestCode}，起点={SourcePosition}，终点={TargetPosition}，原因={Reason}，RetryCount={RetryCount}",
                cachedTask.RequestCode,
                cachedTask.SourcePosition,
                cachedTask.TargetPosition,
                latestReason,
                cachedTask.RetryCount + 1);
        }

        private async Task RefreshCachedTaskSnapshotAsync(IDbConnection connection, RCS_TaskCache cachedTask)
        {
            if (cachedTask == null)
            {
                return;
            }

            var latestCachedTask = await connection.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                "SELECT TOP 1 * FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber AND Status = 0 ORDER BY Id DESC",
                new { PalletNumber = cachedTask.PalletNumber });

            if (latestCachedTask == null)
            {
                return;
            }

            cachedTask.TargetPosition = latestCachedTask.TargetPosition;
            cachedTask.LastError = latestCachedTask.LastError;
            cachedTask.SourcePosition = latestCachedTask.SourcePosition;
            cachedTask.RequestCode = latestCachedTask.RequestCode;
        }

        private static bool ShouldSkipTargetAvailabilityCheck(RCS_TaskCache cachedTask)
        {
            if (cachedTask == null)
            {
                return false;
            }

            return cachedTask.TaskType == RCS_UserTasks.TaskType.fgHandover
                || cachedTask.TaskType == RCS_UserTasks.TaskType.shipment;
        }

        /// <summary>
        /// 确定目标库位占用是否应阻止派发。
        /// </summary>
        /// <param name="cachedTask">要评估的缓存任务。</param>
        /// <returns>对于入库任务返回 false，因为故意放宽了其目标库位的占用校验规则。</returns>
        private static bool ShouldCheckTargetOccupancy(RCS_TaskCache cachedTask)
        {
            return cachedTask?.TaskIdentification != 1;
        }

        private static bool IsLocationOccupied(RCS_Locations location)
        {
            if (location == null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(location.Quanitity)
                && location.Quanitity.Trim() != "0";
        }

        private static string BuildUnavailableReason(RCS_TaskCache cachedTask)
        {
            if (cachedTask == null)
            {
                return "起点或终点仍被锁定，等待下次重试";
            }

            return ShouldSkipTargetAvailabilityCheck(cachedTask)
                ? $"起点 {cachedTask.SourcePosition} 仍不可用，等待下次重试"
                : "起点或终点仍被锁定，等待下次重试";
        }

        private async Task<HashSet<string>> GetBusyLaneKeysAsync(IDbConnection connection)
        {
            var activeTasks = await connection.QueryAsync<RCS_UserTasks>(@"
                SELECT
                    requestCode,
                    sourcePosition,
                    targetPosition,
                    ShelvesIdentification,
                    TaskSequence,
                    IsSplitTask,
                    taskStatus
                FROM RCS_UserTasks WITH (READPAST)
                WHERE taskStatus < @TaskFinish
                    AND taskStatus <> @Canceled",
                new
                {
                    TaskFinish = (int)TaskStatuEnum.TaskFinish,
                    Canceled = (int)TaskStatuEnum.Canceled
                });

            var busyLaneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in activeTasks)
            {
                // 拆分任务第二段在到达 PickDown 及之后、但尚未完成前，中转位已经可复用，不再占用巷道放行名额。
                if (task.IsSplitTask &&
                    task.TaskSequence > 1 &&
                    task.taskStatus >= TaskStatuEnum.PickDown &&
                    task.taskStatus < TaskStatuEnum.TaskFinish)
                {
                    _logger.LogInformation(
                        "巷道忙碌判断主动放行拆分任务第二段：RequestCode={RequestCode}，起点={SourcePosition}，终点={TargetPosition}，巷道={ShelvesIdentification}，状态={TaskStatus}",
                        task.requestCode,
                        task.sourcePosition,
                        task.targetPosition,
                        task.ShelvesIdentification,
                        task.taskStatus);
                    continue;
                }

                var laneKey = GetDispatchLaneKey(task.ShelvesIdentification);
                if (!string.IsNullOrWhiteSpace(laneKey))
                {
                    busyLaneKeys.Add(laneKey);
                }
            }

            return busyLaneKeys;
        }

        private IEnumerable<RCS_TaskCache> SelectPrioritizedTasksByLane(
            IEnumerable<RCS_TaskCache> candidates,
            HashSet<string> busyLaneKeys)
        {
            if (candidates == null)
            {
                yield break;
            }

            busyLaneKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedLaneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in candidates)
            {
                var laneKey = GetDispatchLaneKey(task.ShelvesIdentification);
                if (string.IsNullOrWhiteSpace(laneKey))
                {
                    continue;
                }

                if (busyLaneKeys.Contains(laneKey) || selectedLaneKeys.Contains(laneKey))
                {
                    continue;
                }

                selectedLaneKeys.Add(laneKey);
                yield return task;
            }
        }

        /// <summary>
        /// 按巷道选择可派发的普通缓存任务。
        /// </summary>
        /// <param name="candidates">扫描窗口中的普通缓存候选任务。</param>
        /// <param name="dispatchContext">用于评分计算的最新完成任务上下文。</param>
        /// <param name="connection">用于起点和终点可用性预检的数据库连接。</param>
        /// <param name="busyLaneKeys">已被活动任务占用的巷道。</param>
        /// <param name="prioritizedLaneKeys">已被抢单缓存任务保留的巷道。</param>
        /// <param name="remainingSlots">本轮允许返回的普通任务的最大数量。</param>
        /// <returns>通过了巷道和库位校验的普通缓存任务。</returns>
        private async Task<IEnumerable<RCS_TaskCache>> SelectRegularTasksByLaneAsync(
            IEnumerable<RCS_TaskCache> candidates,
            LastCompletedTaskContext dispatchContext,
            IDbConnection connection,
            HashSet<string> busyLaneKeys,
            HashSet<string> prioritizedLaneKeys,
            int remainingSlots)
        {
            if (candidates == null || remainingSlots <= 0)
            {
                return Enumerable.Empty<RCS_TaskCache>();
            }

            prioritizedLaneKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            busyLaneKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var selected = new List<RCS_TaskCache>();
            var selectedLaneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var laneGroup in candidates
                .Where(task => task != null)
                .GroupBy(task => GetDispatchLaneKey(task.ShelvesIdentification))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .OrderBy(group => GetBestLaneScore(group, dispatchContext))
                .ThenBy(group => group.Min(task => task.CreateTime.AddSeconds(task.RetryCount * 30))))
            {
                var laneKey = laneGroup.Key;
                if (prioritizedLaneKeys.Contains(laneKey) || busyLaneKeys.Contains(laneKey) || selectedLaneKeys.Contains(laneKey))
                {
                    continue;
                }

                var bestTask = default(RCS_TaskCache);
                foreach (var task in laneGroup
                    .OrderBy(task => CalculateRegularTaskScore(task, dispatchContext))
                    .ThenBy(task => task.CreateTime.AddSeconds(task.RetryCount * 30)))
                {
                    var availability = await GetCachedTaskLocationAvailabilityAsync(connection, task);
                    if (availability.IsAvailable)
                    {
                        bestTask = task;
                        break;
                    }

                    // 即使评分最高的缓存任务因其自身的起点/终点被阻塞，该巷道也可能是空闲的。
                    // 在这种情况下，保持该任务在缓存中，并继续处理同一巷道中的下一个评分候选任务。
                    // 此处不会绕过抢单任务和中转点等待，因此业务级别的阻塞仍会继续等待。
                    await MarkCachedTaskUnavailableAsync(connection, task, availability.UnavailableReason);
                }

                if (bestTask == null)
                {
                    continue;
                }

                selected.Add(bestTask);
                selectedLaneKeys.Add(laneKey);

                if (selected.Count >= remainingSlots)
                {
                    break;
                }
            }

            return selected;
        }

        private static int GetBestLaneScore(IGrouping<string, RCS_TaskCache> laneGroup, LastCompletedTaskContext dispatchContext)
        {
            return laneGroup.Min(task => CalculateRegularTaskScore(task, dispatchContext));
        }

        private static int CalculateRegularTaskScore(RCS_TaskCache task, LastCompletedTaskContext dispatchContext)
        {
            if (task == null)
            {
                return int.MaxValue;
            }

            var taskLaneKey = GetDispatchLaneKey(task.ShelvesIdentification);
            var lastLaneKey = GetDispatchLaneKey(dispatchContext?.ShelvesIdentification);
            var sameLane = !string.IsNullOrWhiteSpace(taskLaneKey)
                && !string.IsNullOrWhiteSpace(lastLaneKey)
                && string.Equals(taskLaneKey, lastLaneKey, StringComparison.OrdinalIgnoreCase);

            var oppositeType = dispatchContext?.TaskIdentification.HasValue == true
                && task.TaskIdentification.HasValue
                && task.TaskIdentification.Value != dispatchContext.TaskIdentification.Value;

            if (sameLane)
            {
                return oppositeType ? 0 : 1;
            }

            return oppositeType ? 2 : 3;
        }

        private static string GetDispatchLaneKey(string shelvesIdentification)
        {
            return NormalizeDispatchLaneKey(shelvesIdentification);
        }

        private sealed class LastCompletedTaskContext
        {
            public int? TaskIdentification { get; set; }
            public string ShelvesIdentification { get; set; }
        }

        private sealed class CachedTaskLocationAvailability
        {
            public bool IsAvailable { get; private set; }
            public string UnavailableReason { get; private set; }

            public static CachedTaskLocationAvailability Available()
            {
                return new CachedTaskLocationAvailability { IsAvailable = true };
            }

            public static CachedTaskLocationAvailability Unavailable(string reason)
            {
                return new CachedTaskLocationAvailability
                {
                    IsAvailable = false,
                    UnavailableReason = reason
                };
            }
        }
    }
}
