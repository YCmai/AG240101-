using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service
{
    /// <summary>
    /// RCS任务服务实现
    /// </summary>
    public class RcsTaskService : IRcsTaskService
    {
        private readonly IDatabaseService _db;
        private readonly ILogger<RcsTaskService> _logger;
        private const int TimeOffset = 8;

        public RcsTaskService(IDatabaseService db, ILogger<RcsTaskService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// 添加任务
        /// </summary>
        /// <param name="request">任务请求参数</param>
        /// <returns>任务添加结果</returns>
        public async Task<TaskResponse> AddTaskAsync(AddTaskRequest request)
        {
            _logger.LogInformation("开始处理AddTaskAsync请求: PalletNumber={PalletNumber}, RequestCode={RequestCode}, TaskType={TaskType}, 起点={SourceBin}, 终点={DestBin}, 物料={Material}, 数量={MaterialNum}", 
                request.suNum, request.toNum, request.taskType, request.sourceBin, request.destBin, request.material, request.materialNum);
            
            // 如果 PalletNumber 为空，生成时间戳字符串
            if (string.IsNullOrWhiteSpace(request.suNum))
            {
                request.suNum = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                _logger.LogInformation("PalletNumber 为空，已自动生成时间戳: {PalletNumber}", request.suNum);
            }

            var response = new TaskResponse
            {
                suNum = request.suNum,
                callTime = GetChinaNowString()
            };

            // 在外部作用域声明变量，以便catch块可以访问
            string lockedLocationName = null; // 保存已锁定储位的Name，用于解锁
            bool hasAutoAssignedLocation = false; // 标记是否已自动分配并锁定了储位

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 开启事务
                    connection.Open();
                    using var transaction = connection.BeginTransaction();

                    try
                    {
                        // 检查任务是否已存在
                        //var existingTask = await connection.QueryFirstOrDefaultAsync<RCS_UserTasks>(
                        //    "SELECT * FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber",
                        //    new { PalletNumber = request.suNum },
                        //    transaction);

                        //if (existingTask != null)
                        //{
                        //    response.status = "Fail";
                        //    response.failType = 102;
                        //    response.message = "托盘已存在";
                        //    return response;
                        //}

                        // 自动分配TargetPosition逻辑（只有fgHandover和shipment需要自动分配终点）
                        string[] autoAssignTypes = new[] {
                            RCS_UserTasks.TaskType.fgHandover.ToString(),
                            RCS_UserTasks.TaskType.shipment.ToString()
                        };
                        string targetPosition = request.destBin;
                        if (autoAssignTypes.Contains(request.taskType, StringComparer.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("检测到需要自动分配终点的任务类型: {TaskType}", request.taskType);
                            // 根据任务类型映射到对应的Zone名称
                            string groupName = request.taskType.ToLower() switch
                            {
                                "fghandover" => "fghandover",
                                "shipment" => "dispatch",
                                _ => request.taskType.ToLower()
                            };

                            // 查找可用库位
                            var locations = (await connection.QueryAsync<RCS_Locations>(
                                "SELECT * FROM RCS_Locations WHERE [Group] LIKE '%' + @Group + '%' AND [Lock]=0 AND (Quanitity IS NULL OR Quanitity='' OR  Quanitity='0')",
                                new { Group = groupName },
                                transaction)).ToList();
                            if (locations.Any())
                            {
                                _logger.LogInformation("找到 {Count} 个可用库位，准备随机分配", locations.Count);
                                var rand = new Random();
                                var idx = rand.Next(locations.Count);
                                targetPosition = locations[idx].NodeRemark;
                                lockedLocationName = locations[idx].Name; // 保存Name用于解锁

                                // 锁定储位（使用Name字段）
                                var lockSql = "UPDATE RCS_Locations SET [Lock] = 1 WHERE Name = @Name";
                                await connection.ExecuteAsync(lockSql, new { Name = lockedLocationName }, transaction);
                                hasAutoAssignedLocation = true; // 标记已锁定储位
                                request.destBin = targetPosition;
                                _logger.LogInformation($"已锁定储位: Name={lockedLocationName}, NodeRemark={targetPosition}");
                            }
                            else
                            {
                                _logger.LogWarning("未找到可用的目标库位，任务类型: {TaskType}, 分组: {GroupName}", request.taskType, groupName);
                                _logger.LogInformation("准备调用CacheTaskAsync缓存任务");
                                await CacheTaskAsync(request, connection, transaction, $"未找到可用的目标库位，任务类型: {request.taskType}, 分组: {groupName}");
                                _logger.LogInformation("CacheTaskAsync调用完成，任务已缓存");
                                response.status = "Success";
                                response.message = "任务已缓存，等待可用目标库位后自动处理";
                                transaction.Commit();
                                return response;
                            }
                        }

                        // 如果是binToBin（库位转移）任务类型，跳过拆分判断，直接插入数据库
                        bool isBinToBin = string.Equals(request.taskType, RCS_UserTasks.TaskType.binToBin.ToString(), StringComparison.OrdinalIgnoreCase);

                        if (isBinToBin)
                        {
                            // binToBin任务直接跳过拆分逻辑，进入后续的验证和插入流程
                            _logger.LogInformation("检测到binToBin任务类型，跳过拆分判断，直接插入数据库");
                        }
                        // 检查是否需要任务拆分（第一种拆分逻辑：PA001-PA020等）
                        else if (ShouldSplitTaskType1(request.sourceBin))
                        {
                            _logger.LogInformation("检测到需要拆分的任务（类型1），起点: {SourceBin}", request.sourceBin);

                            try
                            {
                                // 执行任务拆分
                                var splitTasks = await SplitTaskType1Async(request, connection, transaction);

                                // 如果拆分返回空列表（任务被缓存），需要解锁已自动分配的储位
                                if (!splitTasks.Any() && hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                                {
                                    var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                    await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                    _logger.LogInformation($"拆分任务被缓存，已解锁之前自动分配的储位: {lockedLocationName}");
                                    hasAutoAssignedLocation = false; // 重置标记
                                }

                                // 如果拆分返回空列表，提交事务并返回
                                if (!splitTasks.Any())
                                {
                                    transaction.Commit();
                                    response.status = "Success";
                                    response.message = "任务已缓存，等待点位释放后自动处理";
                                    return response;
                                }

                                // 插入拆分后的任务
                                var insertSql = @"
                                INSERT INTO RCS_UserTasks (
                                    taskStatus, 
                                    creatTime, 
                                    requestCode, 
                                    taskType, 
                                    sourcePosition, 
                                    targetPosition, 
                                    executed, 
                                    priority,
                                    MaterialCode,
                                    MaterialQuantity,
                                    PalletNumber,
                                    SourceType,
                                    DestType,
                                    ConfirmTime,
                                    TaskGroupId,
                                    TaskSequence,
                                    IsSplitTask,
                                    OriginalTaskId,
                                    TaskIdentification,
                                    ShelvesIdentification
                                ) VALUES (
                                    @TaskStatus, 
                                    @CreateTime, 
                                    @RequestCode, 
                                    @TaskType, 
                                    @SourcePosition, 
                                    @TargetPosition, 
                                    @Executed, 
                                    @Priority,
                                    @MaterialCode,
                                    @MaterialQuantity,
                                    @PalletNumber,
                                    @SourceType,
                                    @DestType,
                                    @ConfirmTime,
                                    @TaskGroupId,
                                    @TaskSequence,
                                    @IsSplitTask,
                                    @OriginalTaskId,
                                    @TaskIdentification,
                                    @ShelvesIdentification
                                )";

                                foreach (var task in splitTasks)
                                {
                                    await connection.ExecuteAsync(insertSql, new
                                    {
                                        TaskStatus = (int)task.taskStatus,
                                        CreateTime = request.createTime,
                                        RequestCode = task.requestCode+ task.PalletNumber,
                                        TaskType = (int)task.taskType,
                                        SourcePosition = task.sourcePosition,
                                        TargetPosition = task.targetPosition,
                                        Executed = task.executed,
                                        Priority = task.priority,
                                        MaterialCode = task.MaterialCode,
                                        MaterialQuantity = task.MaterialQuantity,
                                        PalletNumber = task.PalletNumber,
                                        SourceType = task.SourceType,
                                        DestType = task.DestType,
                                        ConfirmTime = request.confirmTime,
                                        TaskGroupId = task.TaskGroupId,
                                        TaskSequence = task.TaskSequence,
                                        IsSplitTask = task.IsSplitTask,
                                        OriginalTaskId = task.OriginalTaskId,
                                        TaskIdentification = request.TaskIdentification,
                                        ShelvesIdentification = request.ShelvesIdentification
                                    }, transaction);
                                }

                                // 提交事务
                                transaction.Commit();

                                response.status = "Success";
                                response.message = $"任务拆分成功，生成了 {splitTasks.Count} 个子任务";
                                _logger.LogInformation("任务拆分成功，原始任务: {ToNum}，生成了 {Count} 个子任务", request.toNum, splitTasks.Count);
                                return response;
                            }
                            catch (Exception ex)
                            {
                                // 回滚事务
                                transaction.Rollback();
                                _logger.LogError(ex, "任务拆分失败: {Message}", ex.Message);
                                response.status = "Fail";
                                response.failType = 107;
                                response.message = $"任务拆分失败: {ex.Message}";
                                return response;
                            }
                        }
                        // 检查是否需要任务拆分（第二种拆分逻辑：基于图片映射关系）
                        else if (await ShouldSplitTaskType2Async(request.sourceBin, targetPosition, connection, transaction))
                        {
                            _logger.LogInformation("检测到需要拆分的任务（类型2），起点: {SourceBin}", request.sourceBin);

                            try
                            {
                                // 执行任务拆分
                                var splitTasks = await SplitTaskType2Async(request, connection, transaction);

                                // 如果拆分返回空列表（任务被缓存），需要解锁已自动分配的储位
                                if (!splitTasks.Any() && hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                                {
                                    var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                    await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                    _logger.LogInformation($"拆分任务被缓存，已解锁之前自动分配的储位: {lockedLocationName}");
                                    hasAutoAssignedLocation = false; // 重置标记
                                }

                                // 如果拆分返回空列表，提交事务并返回
                                if (!splitTasks.Any())
                                {
                                    transaction.Commit();
                                    response.status = "Success";
                                    response.message = "任务已缓存，等待点位释放后自动处理";
                                    return response;
                                }

                                // 插入拆分后的任务
                                var insertSql = @"
                                INSERT INTO RCS_UserTasks (
                                    taskStatus, 
                                    creatTime, 
                                    requestCode, 
                                    taskType, 
                                    sourcePosition, 
                                    targetPosition, 
                                    executed, 
                                    priority,
                                    MaterialCode,
                                    MaterialQuantity,
                                    PalletNumber,
                                    SourceType,
                                    DestType,
                                    ConfirmTime,
                                    TaskGroupId,
                                    TaskSequence,
                                    IsSplitTask,
                                    OriginalTaskId,
                                    TaskIdentification,
                                    ShelvesIdentification
                                ) VALUES (
                                    @TaskStatus, 
                                    @CreateTime, 
                                    @RequestCode, 
                                    @TaskType, 
                                    @SourcePosition, 
                                    @TargetPosition, 
                                    @Executed, 
                                    @Priority,
                                    @MaterialCode,
                                    @MaterialQuantity,
                                    @PalletNumber,
                                    @SourceType,
                                    @DestType,
                                    @ConfirmTime,
                                    @TaskGroupId,
                                    @TaskSequence,
                                    @IsSplitTask,
                                    @OriginalTaskId,
                                    @TaskIdentification,
                                    @ShelvesIdentification
                                )";

                                foreach (var task in splitTasks)
                                {
                                    await connection.ExecuteAsync(insertSql, new
                                    {
                                        TaskStatus = (int)task.taskStatus,
                                        CreateTime = request.createTime,
                                        RequestCode = task.requestCode + task.PalletNumber,
                                        TaskType = (int)task.taskType,
                                        SourcePosition = task.sourcePosition,
                                        TargetPosition = task.targetPosition,
                                        Executed = task.executed,
                                        Priority = task.priority,
                                        MaterialCode = task.MaterialCode,
                                        MaterialQuantity = task.MaterialQuantity,
                                        PalletNumber = task.PalletNumber,
                                        SourceType = task.SourceType,
                                        DestType = task.DestType,
                                        ConfirmTime = request.confirmTime,
                                        TaskGroupId = task.TaskGroupId,
                                        TaskSequence = task.TaskSequence,
                                        IsSplitTask = task.IsSplitTask,
                                        OriginalTaskId = task.OriginalTaskId,
                                        TaskIdentification = request.TaskIdentification,
                                        ShelvesIdentification = request.ShelvesIdentification
                                    }, transaction);
                                }

                                // 提交事务
                                transaction.Commit();

                                response.status = "Success";
                                response.message = $"任务拆分成功，生成了 {splitTasks.Count} 个子任务";
                                _logger.LogInformation("任务拆分成功，原始任务: {ToNum}，生成了 {Count} 个子任务", request.toNum, splitTasks.Count);
                                return response;
                            }
                            catch (Exception ex)
                            {
                                // 回滚事务
                                transaction.Rollback();
                                _logger.LogError(ex, "任务拆分失败: {Message}", ex.Message);
                                response.status = "Fail";
                                response.failType = 107;
                                response.message = $"任务拆分失败: {ex.Message}";
                                return response;
                            }
                        }

                        // 解析任务类型
                        if (!Enum.TryParse<RCS_UserTasks.TaskType>(request.taskType, true, out var taskType))
                        {
                            // 如果之前已锁定储位，需要解锁
                            if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                            {
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogWarning($"任务类型无效，已解锁自动分配的储位: {lockedLocationName}");
                            }
                            transaction.Rollback();
                            response.status = "Fail";
                            response.failType = 105;
                            response.message = "无效的任务类型";
                            return response;
                        }

                        // 创建时间处理
                        //DateTime createTime = DateTime.Now;
                        //if (!string.IsNullOrEmpty(request.createTime))
                        //{
                        //    if (!DateTime.TryParse(request.createTime, out createTime))
                        //    {
                        //        createTime = DateTime.Now;
                        //    }
                        //}

                        // 确认时间处理
                        //DateTime? confirmTime = null;
                        //if (!string.IsNullOrEmpty(request.confirmTime))
                        //{
                        //    if (DateTime.TryParse(request.confirmTime, out var parsedConfirmTime))
                        //    {
                        //        confirmTime = parsedConfirmTime;
                        //    }
                        //}


                        // 检查起点和终点是否存在于RCS_Locations表中
                        var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                            "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                            new { NodeRemark = request.sourceBin },
                            transaction);

                        if (sourceLocation == null)
                        {
                            // 如果之前已锁定储位，需要解锁
                            if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                            {
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogWarning($"起点不存在，已解锁自动分配的储位: {lockedLocationName}");
                            }
                            transaction.Rollback();
                            response.status = "Fail";
                            response.failType = 106;
                            response.message = $"起点 {request.sourceBin} 不存在库位配置中";
                            return response;
                        }

                        // 检查起点是否被锁定
                        if (sourceLocation.Lock)
                        {
                            // 起点被锁定，缓存任务而不是返回失败
                            // 如果之前自动分配了储位，需要解锁，因为任务没有创建成功
                            if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                            {
                                // 解锁之前自动分配的储位
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogInformation($"起点被锁定，已解锁之前自动分配的储位: {lockedLocationName}");
                            }
                            _logger.LogInformation($"起点 {request.sourceBin} 已被锁定，将任务缓存");
                            _logger.LogInformation("准备调用CacheTaskAsync缓存任务");
                            await CacheTaskAsync(request, connection, transaction, $"起点 {request.sourceBin} 已被锁定");
                            _logger.LogInformation("CacheTaskAsync调用完成，任务已缓存");
                            response.status = "Success";
                            response.message = "任务已缓存，等待点位释放后自动处理";
                            transaction.Commit();
                            return response;
                        }

                        // 检查目标位置是否存在
                        RCS_Locations destLocation;
                        if (autoAssignTypes.Contains(request.taskType, StringComparer.OrdinalIgnoreCase))
                        {
                            // 对于自动分配的任务类型，使用NodeRemark查询（因为targetPosition是NodeRemark值）
                            destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                                new { NodeRemark = targetPosition },
                                transaction);
                        }
                        else
                        {
                            // 对于其他任务类型，targetPosition是NodeRemark值，需要用NodeRemark查询
                            destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                                new { NodeRemark = targetPosition },
                                transaction);
                        }

                        if (destLocation == null)
                        {
                            // 如果之前已锁定储位，需要解锁
                            if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                            {
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogWarning($"终点不存在，已解锁自动分配的储位: {lockedLocationName}");
                            }
                            transaction.Rollback();
                            response.status = "Fail";
                            response.failType = 106;
                            response.message = $"终点 {targetPosition} 不存在库位配置中";
                            return response;
                        }

                        // 检查终点是否被锁定
                        if (destLocation.Lock)
                        {
                            // 终点被锁定，缓存任务而不是返回失败
                            // 如果之前自动分配了储位，需要解锁，因为任务没有创建成功
                            if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                            {
                                // 解锁之前自动分配的储位
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogInformation($"终点被锁定，已解锁之前自动分配的储位: {lockedLocationName}");
                            }
                            _logger.LogInformation($"终点 {targetPosition} 已被锁定，将任务缓存");
                            _logger.LogInformation("准备调用CacheTaskAsync缓存任务");
                            await CacheTaskAsync(request, connection, transaction, $"终点 {targetPosition} 已被锁定");
                            _logger.LogInformation("CacheTaskAsync调用完成，任务已缓存");
                            response.status = "Success";
                            response.message = "任务已缓存，等待点位释放后自动处理";
                            transaction.Commit();
                            return response;
                        }

                        // 确定最终的目标位置（对于自动分配的任务，使用NodeRemark；其他任务使用原值）
                        string finalTargetPosition = autoAssignTypes.Contains(request.taskType, StringComparer.OrdinalIgnoreCase)
                            ? destLocation.NodeRemark
                            : targetPosition;

                        _logger.LogInformation($"任务起点: {sourceLocation.NodeRemark}, 终点: {finalTargetPosition}");

                        // 插入新任务（非拆分任务）
                        _logger.LogInformation("所有验证通过，准备插入新任务到数据库: PalletNumber={PalletNumber}, 起点={SourcePosition}, 终点={TargetPosition}", request.suNum, request.sourceBin, finalTargetPosition);
                        var sql = @"
                        INSERT INTO RCS_UserTasks (
                            taskStatus,
                            creatTime,
                            requestCode,
                            taskType,
                            sourcePosition,
                            targetPosition,
                            executed,
                            priority,
                            MaterialCode,
                            MaterialQuantity,
                            PalletNumber,
                            SourceType,
                            DestType,
                            ConfirmTime,
                            TaskGroupId,
                            TaskSequence,
                            IsSplitTask,
                            OriginalTaskId,
                            TaskIdentification,
                            ShelvesIdentification
                        ) VALUES (
                            @TaskStatus,
                            @CreateTime,
                            @RequestCode,
                            @TaskType,
                            @SourcePosition,
                            @TargetPosition,
                            @Executed,
                            @Priority,
                            @MaterialCode,
                            @MaterialQuantity,
                            @PalletNumber,
                            @SourceType,
                            @DestType,
                            @ConfirmTime,
                            @TaskGroupId,
                            @TaskSequence,
                            @IsSplitTask,
                            @OriginalTaskId,
                            @TaskIdentification,
                            @ShelvesIdentification
                        )";

                        var result = await connection.ExecuteAsync(sql, new
                        {
                            TaskStatus = (int)TaskStatuEnum.None,
                            CreateTime = request.createTime,
                            RequestCode = request.toNum + request.suNum,
                            TaskType = (int)taskType,
                            SourcePosition = request.sourceBin,
                            TargetPosition = finalTargetPosition,
                            Executed = false,
                            Priority = 1,
                            MaterialCode = request.material,
                            MaterialQuantity = request.materialNum ?? 0,
                            PalletNumber = request.suNum,
                            SourceType = request.sourceType,
                            DestType = request.destType,
                            ConfirmTime = request.confirmTime,
                            TaskGroupId = (string)null,
                            TaskSequence = 1,
                            IsSplitTask = false,
                            OriginalTaskId = (int?)null,
                            // [DispatchStrategy] Preserve fields from upper system for cache scheduling.
                            TaskIdentification = request.TaskIdentification,
                            ShelvesIdentification = request.ShelvesIdentification
                        }, transaction);

                        // 提交事务
                        transaction.Commit();
                        _logger.LogInformation("Task created successfully. PalletNumber={PalletNumber}, RequestCode={RequestCode}", request.suNum, request.toNum);
                        response.status = "Success";
                        response.message = "Task created successfully";
                        return response;
                        response.message = "Task created successfully";
                        return response;
                    }
                    catch (Exception ex)
                    {
                        // 如果之前已锁定储位，需要解锁
                        if (hasAutoAssignedLocation && !string.IsNullOrEmpty(lockedLocationName))
                        {
                            try
                            {
                                var unlockSql = "UPDATE RCS_Locations SET [Lock] = 0 WHERE Name = @Name";
                                await connection.ExecuteAsync(unlockSql, new { Name = lockedLocationName }, transaction);
                                _logger.LogWarning($"发生异常，已解锁自动分配的储位: {lockedLocationName}");
                            }
                            catch (Exception unlockEx)
                            {
                                _logger.LogError(unlockEx, $"解锁储位失败: {lockedLocationName}");
                            }
                        }
                        // 回滚事务
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加任务失败: {Message}", ex.Message);
                response.status = "Fail";
                response.failType = 101;
                response.message = $"添加任务失败: {ex.Message}";
                return response;
            }
        }

        /// <summary>
        /// 缓存任务到任务缓存表
        /// </summary>
        /// <param name="request">任务请求参数</param>
        /// <param name="connection">数据库连接</param>
        /// <param name="transaction">事务</param>
        /// <param name="errorMessage">错误信息</param>
        private async Task CacheTaskAsync(AddTaskRequest request, IDbConnection connection, IDbTransaction transaction, string errorMessage)
        {
            _logger.LogInformation("开始执行CacheTaskAsync: PalletNumber={PalletNumber}, RequestCode={RequestCode}, 错误信息={ErrorMessage}", 
                request.suNum, request.toNum, errorMessage);
            try
            {
                // 解析任务类型
                if (!Enum.TryParse<RCS_UserTasks.TaskType>(request.taskType, true, out var taskType))
                {
                    taskType = RCS_UserTasks.TaskType.manualTask; // 默认为人工任务
                }

                // 创建时间处理
                //DateTime createTime = DateTime.Now;
                //if (!string.IsNullOrEmpty(request.createTime))
                //{
                //    if (!DateTime.TryParse(request.createTime, out createTime))
                //    {
                //        createTime = DateTime.Now;
                //    }
                //}

                // 确认时间处理
                //DateTime? confirmTime = null;
                //if (!string.IsNullOrEmpty(request.confirmTime))
                //{
                //    if (DateTime.TryParse(request.confirmTime, out var parsedConfirmTime))
                //    {
                //        confirmTime = parsedConfirmTime;
                //    }
                //}

                // 关键修复：使用 PalletNumber 作为唯一标识检查是否已存在相同的缓存任务（Status = 0 待处理）
                _logger.LogDebug("检查是否已存在相同的缓存任务: PalletNumber={PalletNumber}", request.suNum);
                var existingCache = await connection.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                    "SELECT * FROM RCS_TaskCache WHERE PalletNumber = @PalletNumber AND Status = 0",
                    new { PalletNumber = request.suNum },
                    transaction);

                if (existingCache != null)
                {
                    _logger.LogInformation("发现已存在的缓存任务，将更新而非新增: Id={Id}, PalletNumber={PalletNumber}", existingCache.Id, request.suNum);
                    // 如果已存在，更新错误信息和重试次数，而不是插入新记录
                    await connection.ExecuteAsync(@"
                        UPDATE RCS_TaskCache 
                        SET LastError = @LastError,
                            RetryCount = RetryCount + 1,
                            CreateTime = CASE WHEN CreateTime > @CreateTime THEN CreateTime ELSE @CreateTime END
                        WHERE Id = @Id",
                        new
                        {
                            LastError = errorMessage,
                            CreateTime = request.createTime,
                            Id = existingCache.Id
                        },
                        transaction);

                    _logger.LogInformation("缓存任务已存在，已更新: PalletNumber={PalletNumber}, RequestCode={RequestCode}, 错误信息: {ErrorMessage}",
                        request.suNum, request.toNum, errorMessage);
                    _logger.LogInformation("CacheTaskAsync完成（更新已存在的缓存任务）");
                    return; // 避免重复插入
                }

                // 检查是否已存在该 PalletNumber 的实际任务（RCS_UserTasks）
                _logger.LogDebug("检查是否已存在实际任务: PalletNumber={PalletNumber}", request.suNum);
                var existingTask = await connection.QueryFirstOrDefaultAsync<RCS_UserTasks>(
                    "SELECT TOP 1 * FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber AND taskStatus != 30",
                    new { PalletNumber = request.suNum },
                    transaction);

                if (existingTask != null)
                {
                    // 如果已存在实际任务（非已取消状态），不再缓存
                    _logger.LogInformation("任务已存在，跳过缓存: PalletNumber={PalletNumber}, RequestCode={RequestCode}",
                        request.suNum, request.toNum);
                    _logger.LogInformation("CacheTaskAsync完成（跳过缓存，实际任务已存在）");
                    return;
                }

                // 如果不存在，才插入新记录
                _logger.LogInformation("准备插入新的缓存任务记录: PalletNumber={PalletNumber}, RequestCode={RequestCode}", request.suNum, request.toNum);
                var sql = @"
                INSERT INTO RCS_TaskCache (
                    TaskType, SourcePosition, TargetPosition, MaterialCode, MaterialQuantity,
                    PalletNumber, SourceType, DestType, Priority, RequestCode,
                    CreateTime, ConfirmTime, LastError, Status, RetryCount,
                    TaskIdentification, ShelvesIdentification
                ) VALUES (
                    @TaskType, @SourcePosition, @TargetPosition, @MaterialCode, @MaterialQuantity,
                    @PalletNumber, @SourceType, @DestType, @Priority, @RequestCode,
                    @CreateTime, @ConfirmTime, @LastError, 0, 0,
                    @TaskIdentification, @ShelvesIdentification
                )";

                await connection.ExecuteAsync(sql, new
                {
                    TaskType = (int)taskType,
                    SourcePosition = request.sourceBin,
                    TargetPosition = request.destBin,
                    MaterialCode = request.material,
                    MaterialQuantity = request.materialNum ?? 0,
                    PalletNumber = request.suNum,
                    SourceType = request.sourceType,
                    DestType = request.destType,
                    Priority = 1,
                    RequestCode = request.toNum,
                    CreateTime = request.createTime,
                    ConfirmTime = request.confirmTime,
                    LastError = errorMessage,
                    TaskIdentification = request.TaskIdentification,
                    ShelvesIdentification = request.ShelvesIdentification
                }, transaction);

                _logger.LogInformation("任务已缓存: RequestCode={RequestCode}, PalletNumber={PalletNumber}, 错误信息: {ErrorMessage}", request.toNum, request.suNum, errorMessage);
                _logger.LogInformation("CacheTaskAsync完成（新增缓存任务）");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "缓存任务失败: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        /// <param name="request">任务状态请求参数</param>
        /// <returns>任务状态更新结果</returns>
        public async Task<TaskResponse> UpdateTaskStatusAsync(UpdateTaskStatusRequest request)
        {
            var response = new TaskResponse
            {
                suNum = request.suNum,
                callTime = GetChinaNowString()
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();

                    try
                    {
                        // 解析任务状态
                        if (!int.TryParse(request.status, out int statusValue))
                        {
                            response.status = "Fail";
                            response.message = "无效的任务状态";
                            return response;
                        }

                        // 通过suNum查找所有相关任务（包括拆分任务） - 使用明确的列名
                        var relatedTasks = await connection.QueryAsync<RCS_UserTasks>(
                            @"SELECT 
                                ID, taskStatus, executedTime, runTaskId, startTime, executed, creatTime, endTime,
                                requestCode, taskType, priority, robotCode, sourcePosition, targetPosition,
                                IsCancelled, MaterialCode, MaterialQuantity, PalletNumber, SourceType, DestType,
                                ConfirmTime, TaskGroupId, TaskSequence, IsSplitTask, OriginalTaskId
                              FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber ORDER BY TaskSequence",
                            new { PalletNumber = request.suNum },
                            transaction);

                        var taskList = relatedTasks.ToList();
                        if (!taskList.Any())
                        {
                            response.status = "Fail";
                            response.message = "任务不存在";
                            return response;
                        }

                        // 如果是拆分任务，需要特殊处理
                        if (taskList.Count > 1 && taskList.Any(t => t.IsSplitTask))
                        {
                            // 拆分任务的状态更新逻辑
                            await HandleSplitTaskStatusUpdate(connection, transaction, taskList, statusValue);
                        }
                        else
                        {
                            // 单个任务的状态更新
                            var task = taskList.First();
                            await UpdateSingleTaskStatus(connection, transaction, task, statusValue);
                        }

                        transaction.Commit();

                        response.status = "Success";
                        response.message = "任务状态更新成功";
                        return response;
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新任务状态失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = $"更新任务状态失败: {ex.Message}";
                return response;
            }
        }

        /// <summary>
        /// 处理拆分任务的状态更新
        /// </summary>
        private async Task HandleSplitTaskStatusUpdate(IDbConnection connection, IDbTransaction transaction, List<RCS_UserTasks> taskList, int statusValue)
        {
            // 按任务序号排序
            var sortedTasks = taskList.OrderBy(t => t.TaskSequence).ToList();

            // 检查是否有正在执行的任务
            var executingTask = sortedTasks.FirstOrDefault(t =>
                t.taskStatus != TaskStatuEnum.None &&
                t.taskStatus != TaskStatuEnum.TaskFinish &&
                t.taskStatus != TaskStatuEnum.Canceled);

            if (executingTask != null)
            {
                // 如果有正在执行的任务，只更新该任务的状态
                await UpdateSingleTaskStatus(connection, transaction, executingTask, statusValue);
                _logger.LogInformation("更新拆分任务中正在执行的任务状态: {TaskCode}, 状态: {Status}",
                    executingTask.requestCode, statusValue);
            }
            else
            {
                // 如果两个任务都没执行，根据状态值决定更新哪个任务
                if (statusValue == (int)TaskStatuEnum.TaskStart || statusValue == (int)TaskStatuEnum.Confirm)
                {
                    // 开始执行第一个任务
                    var firstTask = sortedTasks.First();
                    await UpdateSingleTaskStatus(connection, transaction, firstTask, statusValue);
                    _logger.LogInformation("开始执行拆分任务的第一个任务: {TaskCode}, 状态: {Status}",
                        firstTask.requestCode, statusValue);
                }
                else
                {
                    // 其他状态，更新所有未完成的任务
                    foreach (var task in sortedTasks.Where(t => t.taskStatus != TaskStatuEnum.TaskFinish))
                    {
                        await UpdateSingleTaskStatus(connection, transaction, task, statusValue);
                    }
                    _logger.LogInformation("更新拆分任务的所有未完成任务状态: {Status}", statusValue);
                }
            }
        }

        /// <summary>
        /// 更新单个任务的状态
        /// </summary>
        private async Task UpdateSingleTaskStatus(IDbConnection connection, IDbTransaction transaction, RCS_UserTasks task, int statusValue)
        {
            // 更新任务状态
            var updateSql = @"
                UPDATE RCS_UserTasks 
                SET taskStatus = @TaskStatus,
                    executedTime = @ExecutedTime
                WHERE Id = @Id";

            await connection.ExecuteAsync(updateSql, new
            {
                TaskStatus = statusValue,
                ExecutedTime = DateTime.Now,
                Id = task.ID
            }, transaction);

            // 如果状态为已完成，更新结束时间
            if (statusValue == (int)TaskStatuEnum.TaskFinish)
            {
                var updateEndTimeSql = @"
                    UPDATE RCS_UserTasks 
                    SET endTime = @EndTime
                    WHERE Id = @Id";

                await connection.ExecuteAsync(updateEndTimeSql, new
                {
                    EndTime = DateTime.Now,
                    Id = task.ID
                }, transaction);
            }
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        /// <param name="request">取消任务请求参数</param>
        /// <returns>取消任务结果</returns>
        public async Task<CancelTaskResponse> CancelTaskAsync(CancelTaskRequest request)
        {
            var response = new CancelTaskResponse
            {
                //toNum = request.toNum,
                //callTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    connection.Open();
                    using var transaction = connection.BeginTransaction();

                    try
                    {
                        // 通过suNum查找所有相关任务（包括拆分任务） - 使用明确的列名
                        var relatedTasks = await connection.QueryAsync<RCS_UserTasks>(
                            @"SELECT 
                                ID, taskStatus, executedTime, runTaskId, startTime, executed, creatTime, endTime,
                                requestCode, taskType, priority, robotCode, sourcePosition, targetPosition,
                                IsCancelled, MaterialCode, MaterialQuantity, PalletNumber, SourceType, DestType,
                                ConfirmTime, TaskGroupId, TaskSequence, IsSplitTask, OriginalTaskId
                              FROM RCS_UserTasks WHERE PalletNumber = @PalletNumber",
                            new { PalletNumber = request.suNum },
                            transaction);

                        var taskList = relatedTasks.ToList();
                        if (!taskList.Any())
                        {
                            response.status = "Fail";
                            response.message = "任务取消失败,任务不存在";
                            return response;
                        }

                        // 检查是否有拆分任务
                        var splitTasks = taskList.Where(t => t.IsSplitTask).ToList();
                        var taskGroups = new List<List<RCS_UserTasks>>();

                        if (splitTasks.Any())
                        {
                            // 按TaskGroupId分组
                            taskGroups = splitTasks.GroupBy(t => t.TaskGroupId)
                                .Select(g => g.ToList())
                                .ToList();
                        }
                        else
                        {
                            // 没有拆分任务，将单个任务作为一个组
                            taskGroups.Add(taskList);
                        }

                        int cancelledCount = 0;
                        var cancelledTasks = new List<string>();

                        foreach (var taskGroup in taskGroups)
                        {
                            // 检查这个分组中是否有任何任务需要取消
                            var tasksToCancel = taskGroup.Where(t =>
                                t.taskStatus != TaskStatuEnum.TaskFinish &&
                                t.taskStatus != TaskStatuEnum.Canceled).ToList();

                            if (tasksToCancel.Any())
                            {
                                // 取消整个分组的所有未完成任务
                                foreach (var task in tasksToCancel)
                                {
                                    if (task.taskStatus == TaskStatuEnum.None)
                                    {
                                        // 未执行的任务，直接更新状态为取消
                                        await connection.ExecuteAsync(@"
                                            UPDATE RCS_UserTasks 
                                            SET taskStatus = @TaskStatus,
                                                executedTime = @ExecutedTime,
                                                endTime = @EndTime
                                            WHERE Id = @Id",
                                            new
                                            {
                                                TaskStatus = (int)TaskStatuEnum.Canceled,
                                                ExecutedTime = DateTime.Now,
                                                EndTime = DateTime.Now,
                                                Id = task.ID
                                            },
                                            transaction);
                                    }
                                    else
                                    {
                                        // 执行中的任务，设置IsCancelled标志
                                        await connection.ExecuteAsync(@"
                                            UPDATE RCS_UserTasks 
                                            SET IsCancelled = @IsCancelled,
                                                executedTime = @ExecutedTime,
                                                endTime = @EndTime
                                            WHERE Id = @Id",
                                            new
                                            {
                                                IsCancelled = true,
                                                ExecutedTime = DateTime.Now,
                                                EndTime = DateTime.Now,
                                                Id = task.ID
                                            },
                                            transaction);
                                    }

                                    cancelledCount++;
                                    cancelledTasks.Add(task.requestCode);
                                }

                                _logger.LogInformation("取消任务分组: {TaskGroupId}，包含 {TaskCount} 个任务",
                                    taskGroup.First().TaskGroupId, tasksToCancel.Count);
                            }
                        }

                        if (cancelledCount == 0)
                        {
                            response.status = "Fail";
                            response.message = "没有可取消的任务（所有任务已完成或已取消）";
                            return response;
                        }

                        transaction.Commit();

                        response.status = "Success";
                        response.message = $"成功取消 {cancelledCount} 个任务";
                        _logger.LogInformation("取消任务成功，托盘号: {PalletNumber}，取消的任务: {CancelledTasks}",
                            request.suNum, string.Join(", ", cancelledTasks));

                        return response;
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消任务失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = $"任务取消失败: {ex.Message}";
                return response;
            }
        }

        /// <summary>
        /// 获取任务清单
        /// </summary>
        /// <param name="request">获取任务清单请求参数</param>
        /// <returns>任务清单</returns>
        public async Task<TaskListResponse> GetTaskListAsync(GetTaskListRequest request)
        {
            var response = new TaskListResponse
            {
                status = "Success",
                data = new List<TaskListItem>()
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 设置默认时间范围为最近31天
                    var endTime = DateTime.Now;
                    var startTime = endTime.AddDays(-31);

                    if (request.startTime.HasValue)
                    {
                        startTime = request.startTime.Value;
                    }

                    if (request.endTime.HasValue)
                    {
                        endTime = request.endTime.Value;
                    }

                    // 查询所有creatTime在范围内的任务 - 使用明确的列名
                    var sql = @"
                        SELECT 
                            ID,
                            taskStatus,
                            executedTime,
                            runTaskId,
                            startTime,
                            executed,
                            creatTime,
                            endTime,
                            requestCode,
                            taskType,
                            priority,
                            robotCode,
                            sourcePosition,
                            targetPosition,
                            IsCancelled,
                            MaterialCode,
                            MaterialQuantity,
                            PalletNumber,
                            SourceType,
                            DestType,
                            ConfirmTime
                        FROM RCS_UserTasks
                        WHERE creatTime BETWEEN @StartTime AND @EndTime
                        ORDER BY creatTime DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

                    var tasks = await connection.QueryAsync<RCS_UserTasks>(sql, new
                    {
                        StartTime = startTime,
                        EndTime = endTime,
                        Offset = (request.pageIndex - 1) * request.pageSize,
                        PageSize = request.pageSize
                    });

                    foreach (var task in tasks)
                    {
                        var taskItem = new TaskListItem
                        {
                            toNum = task.requestCode,
                            suNum = task.PalletNumber,
                            material = task.MaterialCode,
                            materialNum = task.MaterialQuantity,
                            createTime = FormatDateTime(task.creatTime),
                            startTime = FormatDateTime(task.startTime),
                            finishTime = FormatDateTime(task.endTime),
                            sourceType = task.SourceType,
                            sourceBin = task.sourcePosition,
                            destType = task.DestType,
                            destBin = task.targetPosition,
                            taskType = task.taskType.ToString(),
                            msg = task.taskStatus.ToString(),
                            step = "",
                            agvId = !string.IsNullOrEmpty(task.robotCode) ? task.robotCode.Split(',').ToList() : new List<string>()
                        };
                        response.data.Add(taskItem);
                    }

                    response.message = "请求成功";
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务清单失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 按时间段查询任务
        /// </summary>
        /// <param name="request">按时间段查询任务请求参数</param>
        /// <returns>任务清单</returns>
        public async Task<TaskListResponse> GetTaskByTimeAsync(GetTaskByTimeRequest request)
        {
            var response = new TaskListResponse
            {
                status = "Success",
                data = new List<TaskListItem>()
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 解析时间
                    if (!DateTime.TryParse(request.createTimeStart, out var startTime))
                    {
                        response.status = "Fail";
                        response.message = "无效的开始时间格式";
                        return response;
                    }

                    if (!DateTime.TryParse(request.createTimeEnd, out var endTime))
                    {
                        response.status = "Fail";
                        response.message = "无效的结束时间格式";
                        return response;
                    }

                    // 查询所有creatTime在范围内的任务 - 使用明确的列名
                    var sql = @"
                        SELECT 
                            ID,
                            taskStatus,
                            executedTime,
                            runTaskId,
                            startTime,
                            executed,
                            creatTime,
                            endTime,
                            requestCode,
                            taskType,
                            priority,
                            robotCode,
                            sourcePosition,
                            targetPosition,
                            IsCancelled,
                            MaterialCode,
                            MaterialQuantity,
                            PalletNumber,
                            SourceType,
                            DestType,
                            ConfirmTime
                        FROM RCS_UserTasks
                        WHERE creatTime BETWEEN @StartTime AND @EndTime
                        ORDER BY creatTime DESC";

                    var tasks = await connection.QueryAsync<RCS_UserTasks>(sql, new
                    {
                        StartTime = startTime,
                        EndTime = endTime
                    });

                    foreach (var task in tasks)
                    {
                        var taskItem = new TaskListItem
                        {
                            toNum = task.requestCode,
                            suNum = task.PalletNumber,
                            material = task.MaterialCode,
                            materialNum = task.MaterialQuantity,
                            createTime = FormatDateTime(task.creatTime),
                            startTime = FormatDateTime(task.startTime),
                            finishTime = FormatDateTime(task.endTime),
                            sourceType = task.SourceType,
                            sourceBin = task.sourcePosition,
                            destType = task.DestType,
                            destBin = task.targetPosition,
                            taskType = task.taskType.ToString(),
                            msg = task.taskStatus.ToString(),
                            step = "",
                            agvId = !string.IsNullOrEmpty(task.robotCode) ? task.robotCode.Split(',').ToList() : new List<string>()
                        };
                        response.data.Add(taskItem);
                    }

                    response.message = "请求成功";
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按时间段查询任务失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 根据任务编码查询任务
        /// </summary>
        /// <param name="toNum">任务编码</param>
        /// <returns>任务详情</returns>
        public async Task<TaskListResponse> GetTaskByIdAsync(string toNum)
        {
            var response = new TaskListResponse
            {
                status = "Success",
                data = new List<TaskListItem>()
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 查询指定任务编码的任务 - 使用明确的列名
                    var sql = @"
                        SELECT 
                            ID,
                            taskStatus,
                            executedTime,
                            runTaskId,
                            startTime,
                            executed,
                            creatTime,
                            endTime,
                            requestCode,
                            taskType,
                            priority,
                            robotCode,
                            sourcePosition,
                            targetPosition,
                            IsCancelled,
                            MaterialCode,
                            MaterialQuantity,
                            PalletNumber,
                            SourceType,
                            DestType,
                            ConfirmTime
                        FROM RCS_UserTasks
                        WHERE requestCode = @ToNum
                        ORDER BY creatTime DESC";

                    var tasks = await connection.QueryAsync<RCS_UserTasks>(sql, new { ToNum = toNum });

                    if (!tasks.Any())
                    {
                        response.status = "Fail";
                        response.message = "任务不存在";
                        return response;
                    }

                    foreach (var task in tasks)
                    {
                        var taskItem = new TaskListItem
                        {
                            toNum = task.requestCode,
                            suNum = task.PalletNumber,
                            material = task.MaterialCode,
                            materialNum = task.MaterialQuantity,
                            createTime = FormatDateTime(task.creatTime),
                            startTime = FormatDateTime(task.startTime),
                            finishTime = FormatDateTime(task.endTime),
                            sourceType = task.SourceType,
                            sourceBin = task.sourcePosition,
                            destType = task.DestType,
                            destBin = task.targetPosition,
                            taskType = task.taskType.ToString(),
                            msg = task.taskStatus.ToString(),
                            step = "",
                            agvId = !string.IsNullOrEmpty(task.robotCode) ? task.robotCode.Split(',').ToList() : new List<string>()
                        };
                        response.data.Add(taskItem);
                    }

                    response.message = "请求成功";
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据任务编码查询任务失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 查询AGV当前状态
        /// </summary>
        /// <param name="request">AGV状态查询请求参数</param>
        /// <returns>AGV当前状态</returns>
        public async Task<AgvStateResponse> GetAgvStateAsync(AgvStateRequest request)
        {
            var response = new AgvStateResponse
            {
                status = "Success",
                data = null
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 查询AGV信息
                    var sql = @"
                    SELECT 
                        AgvId,
                        X,
                        Y,
                        Angle,
                        BatteryLevel,
                        BatteryTemp,
                        ChargingStatus,
                        JackStatus,
                        LoadStatus
                    FROM AGV_Info
                    WHERE AgvId = @AgvId";

                    var agvInfo = await connection.QueryFirstOrDefaultAsync<AgvInfo>(sql, new { AgvId = request.agvId });

                    if (agvInfo == null)
                    {
                        response.status = "Fail";
                        response.message = "AGVID不存在";
                        return response;
                    }

                    // 查询最新报警信息
                    var alarmSql = @"
                    SELECT 
                        AlarmCode,
                        AlarmContent
                    FROM AGV_Alarm
                    WHERE AgvId = @AgvId AND ProcessStatus = 0
                    ORDER BY AlarmTime DESC";

                    var alarms = await connection.QueryAsync<AgvAlarm>(alarmSql, new { AgvId = request.agvId });

                    // 构建响应
                    var alarmDict = new Dictionary<string, string>();
                    foreach (var alarm in alarms)
                    {
                        alarmDict[alarm.AlarmCode] = alarm.AlarmContent;
                    }

                    response.data = new AgvStateData
                    {
                        x = agvInfo.X,
                        y = agvInfo.Y,
                        angle = agvInfo.Angle,
                        vx = 0.0f, // 假设这些数据不存储在数据库中
                        vy = 0.0f,
                        w = 0.0f,
                        confidence = 0.99f,
                        charging = agvInfo.ChargingStatus == 1,
                        manualCharging = false, // 假设手工充电信息不存储
                        batteryLevel = agvInfo.BatteryLevel,
                        batteryTemp = agvInfo.BatteryTemp,
                        alarms = alarmDict,
                        jackState = agvInfo.JackStatus == 1,
                        jackHeight = 0.0f, // 假设顶升高度信息不存储
                        currentShelf = agvInfo.LoadStatus == 1,
                        cpuRate = 0.0f, // 假设这些系统信息不存储
                        memRate = 0.0f,
                        cpuTemp = 0.0f,
                        agvId = agvInfo.AgvId
                    };

                    response.message = "请求成功";
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询AGV当前状态失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 查询AGV总状态
        /// </summary>
        /// <param name="request">AGV总状态查询请求参数</param>
        /// <returns>AGV总状态</returns>
        public async Task<AgvStatusResponse> GetAgvStatusAsync(AgvStatusRequest request)
        {
            var response = new AgvStatusResponse
            {
                status = "Success",
                data = null
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 查询AGV信息
                    var sql = @"
                    SELECT 
                        AgvId,
                        TotalRunTime,
                        TotalDistance,
                        ChargeCount,
                        BatteryCircleCount
                    FROM AGV_Info
                    WHERE AgvId = @AgvId";

                    var agvInfo = await connection.QueryFirstOrDefaultAsync<AgvInfo>(sql, new { AgvId = request.agvId });

                    if (agvInfo == null)
                    {
                        response.status = "Fail";
                        response.message = "AGVID不存在";
                        return response;
                    }

                    // 构建响应
                    response.data = new AgvStatusData
                    {
                        time = agvInfo.TotalRunTime,
                        odo = agvInfo.TotalDistance,
                        batteryChargeCount = agvInfo.ChargeCount,
                        batteryCircleCount = agvInfo.BatteryCircleCount,
                        agvId = agvInfo.AgvId
                    };

                    response.message = "请求成功";
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询AGV总状态失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 查询总库位状态
        /// </summary>
        /// <returns>总库位状态</returns>
        public async Task<BinStatusResponse> GetBinStatusAsync()
        {
            var response = new BinStatusResponse
            {
                status = "Success",
                lockBin = new List<string>(),
                stockedBin = new List<string>(),
                availableBin = new List<string>(),
                dispatchZone = new Dictionary<string, BinZoneInfo>(),
                receivingZone = new Dictionary<string, BinZoneInfo>(),
                fgHandoverZone = new Dictionary<string, BinZoneInfo>(),
                rmHandoverZone = new Dictionary<string, BinZoneInfo>(),
                transitZone = new Dictionary<string, BinZoneInfo>(),
                rackingZone01 = new Dictionary<string, BinZoneInfo>(),
                rackingZone02 = new Dictionary<string, BinZoneInfo>(),
                rackingZone03 = new Dictionary<string, BinZoneInfo>(),
            };

            try
            {
                using (var connection = _db.CreateConnection())
                {
                    // 查询所有库位信息
                    var sql = "SELECT * FROM RCS_Locations";
                    var locations = await connection.QueryAsync<RCS_Locations>(sql);

                    if (locations == null || !locations.Any())
                    {
                        response.message = "未找到库位信息";
                        return response;
                    }

                    // 处理锁定、已占用和可用库位
                    foreach (var location in locations)
                    {
                        // 构建库位信息
                        var binInfo = new BinZoneInfo
                        {
                            name = location.Name,
                            nodeRemark = location.NodeRemark,
                            materialCode = location.MaterialCode,
                            palletID = location.PalletID,
                            weight = location.Weight,
                            quantity = location.Quanitity,
                            entryDate = location.EntryDate,
                            group = location.Group,
                            liftingHeight = location.LiftingHeight,
                            Lock = location.Lock,
                            wattingNode = location.WattingNode
                        };

                        // 根据锁定状态分类
                        if (location.Lock)
                        {
                            response.lockBin.Add(location.Name);
                        }
                        // 根据是否有物料判断是否已占用
                        else if (!string.IsNullOrEmpty(location.MaterialCode) && location.MaterialCode != "0")
                        {
                            response.stockedBin.Add(location.Name);
                        }
                        else
                        {
                            response.availableBin.Add(location.Name);
                        }

                        // 根据分组将库位分配到不同区域
                        switch (location.Group.ToLower())
                        {
                            case "dispatch":
                                response.dispatchZone[location.Name] = binInfo;
                                break;
                            case "receiving":
                                response.receivingZone[location.Name] = binInfo;
                                break;
                            case "fghandover":
                                response.fgHandoverZone[location.Name] = binInfo;
                                break;
                            case "rmhandover":
                                response.rmHandoverZone[location.Name] = binInfo;
                                break;
                            case "transit":
                                response.transitZone[location.Name] = binInfo;
                                break;
                            case "rack1":
                                response.rackingZone01[location.Name] = binInfo;
                                break;
                            case "rack2":
                                response.rackingZone02[location.Name] = binInfo;
                                break;
                            case "rack3":
                                response.rackingZone03[location.Name] = binInfo;
                                break;
                        }
                    }

                    // 设置数量统计
                    response.lockBinQty = response.lockBin.Count;
                    response.stockedBinQty = response.stockedBin.Count;
                    response.availableBinQty = response.availableBin.Count;
                    response.message = "请求成功";

                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询总库位状态失败: {Message}", ex.Message);
                response.status = "Fail";
                response.message = "连接失败";
                return response;
            }
        }

        /// <summary>
        /// 物料交收区库位状态更新
        /// </summary>
        public async Task<object> BinUpdateAsync(BinUpdateRequest request)
        {
            try
            {
                using var connection = _db.CreateConnection();
                string sql;
                if (request.binStatus == 1)
                {
                    sql = "UPDATE RCS_Locations SET Quanitity = '满' WHERE Name = @BinId";
                }
                else
                {
                    sql = "UPDATE RCS_Locations SET Quanitity = '空' WHERE Name = @BinId";
                }
                int affected = await connection.ExecuteAsync(sql, new { BinId = request.binId });
                if (affected > 0)
                {
                    return new { status = "Success", binId = request.binId };
                }
                else
                {
                    return new { status = "Fail", binId = request.binId };
                }
            }
            catch
            {
                return new { status = "Fail", binId = request.binId };
            }
        }

        /// <summary>
        /// 检查是否需要任务拆分（类型1：PA001-PA020等）
        /// </summary>
        /// <param name="sourceBin">起点</param>
        /// <returns>是否需要拆分</returns>
        private bool ShouldSplitTaskType1(string sourceBin)
        {
            // 检查起点是否为需要拆分的区域
            // 支持两种格式：1. 完整范围格式如 "PA001-PA020" 2. 单个储位格式如 "PA001"
            var splitSourcePatterns = new[] { "PA001-PA020", "PA021-PA040", "PA041-PA060" };

            // 首先检查是否直接匹配完整范围格式
            if (splitSourcePatterns.Any(pattern => sourceBin.Contains(pattern)))
            {
                return true;
            }

            // 然后检查是否匹配单个储位格式（如PA001在PA001-PA020范围内）
            if (sourceBin.StartsWith("PA"))
            {
                // 提取数字部分
                var numberPart = sourceBin.Substring(2);
                if (int.TryParse(numberPart, out int number))
                {
                    // 检查是否在PA001-PA020范围内 (1-20)
                    if (number >= 1 && number <= 20)
                        return true;

                    // 检查是否在PA021-PA040范围内 (21-40)
                    if (number >= 21 && number <= 40)
                        return true;

                    // 检查是否在PA041-PA060范围内 (41-60)
                    if (number >= 41 && number <= 60)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检查是否需要任务拆分（类型2：基于图片映射关系）
        /// </summary>
        /// <param name="sourceBin">起点（NodeRemark）</param>
        /// <param name="destBin">终点（NodeRemark）</param>
        /// <returns>是否需要拆分</returns>
        private async Task<bool> ShouldSplitTaskType2Async(string sourceBin, string destBin, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            // 通过NodeRemark查询对应的Name值
            var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = sourceBin },
                transaction);

            if (sourceLocation == null)
            {
                return false; // 如果找不到对应的位置，不进行拆分
            }

            // 根据Name值判断起点是否需要拆分（基于图片中的站点范围）
            // 支持两种格式：1. 完整范围格式如 "100-131" 2. 单个站点格式如 "100"
            var splitSourcePatterns = new[] { "100-131", "132-195", "196-259", "260-323", "324-387", "388-419", "420-467", "468-515", "516-563","801-805" };

            // 首先检查是否直接匹配完整范围格式
            if (splitSourcePatterns.Any(pattern => sourceLocation.Name.Contains(pattern)))
            {
                return true;
            }

            // 然后检查是否匹配单个站点格式
            if (int.TryParse(sourceLocation.Name, out int stationNumber))
            {
                // 检查是否在100-131范围内
                if (stationNumber >= 100 && stationNumber <= 131)
                    return true;

                // 检查是否在132-195范围内
                if (stationNumber >= 132 && stationNumber <= 195)
                    return true;

                // 检查是否在196-259范围内
                if (stationNumber >= 196 && stationNumber <= 259)
                    return true;

                // 检查是否在260-323范围内
                if (stationNumber >= 260 && stationNumber <= 323)
                    return true;

                // 检查是否在324-387范围内
                if (stationNumber >= 324 && stationNumber <= 387)
                    return true;

                // 检查是否在388-419范围内
                if (stationNumber >= 388 && stationNumber <= 419)
                    return true;

                // 检查是否在420-467范围内
                if (stationNumber >= 420 && stationNumber <= 467)
                    return true;

                // 检查是否在468-515范围内
                if (stationNumber >= 468 && stationNumber <= 515)
                    return true;

                // 检查是否在516-563范围内
                if (stationNumber >= 516 && stationNumber <= 563)
                    return true;
                if (stationNumber >= 801 && stationNumber <= 805)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 执行任务拆分逻辑（类型1：PA001-PA020等）
        /// </summary>
        /// <param name="request">原始任务请求</param>
        /// <param name="connection">数据库连接</param>
        /// <param name="transaction">事务</param>
        /// <returns>拆分后的任务列表</returns>
        private async Task<List<RCS_UserTasks>> SplitTaskType1Async(AddTaskRequest request, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            var splitTasks = new List<RCS_UserTasks>();
            var taskGroupId = Guid.NewGuid().ToString(); // 生成唯一的分组ID
            var createTime = Convert.ToDateTime(request.createTime);
            var confirmTime = Convert.ToDateTime(request.confirmTime); ;

            // 解析任务类型
            if (!Enum.TryParse<RCS_UserTasks.TaskType>(request.taskType, true, out var taskType))
            {
                throw new ArgumentException("无效的任务类型");
            }

            // 第一步：查找起点对应的RCS_Locations节点
            var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = request.sourceBin },
                transaction);

            if (sourceLocation == null)
            {
                throw new ArgumentException($"起点 {request.sourceBin} 不存在库位配置中");
            }

            if (sourceLocation.Lock)
            {
                // 起点被锁定，缓存任务而不是抛出异常
                _logger.LogInformation($"起点 {request.sourceBin} 已被锁定，将任务缓存");
                await CacheTaskAsync(request, connection, transaction, $"起点 {request.sourceBin} 已被锁定");
                return new List<RCS_UserTasks>(); // 返回空列表表示任务已缓存
            }

            // 第二步：查找终点对应的RCS_Locations节点
            var destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = request.destBin },
                transaction);

            if (destLocation == null)
            {
                throw new ArgumentException($"终点 {request.destBin} 不存在库位配置中");
            }

            // 对于fgHandover和shipment任务类型，终点是自动分配并锁定的，不需要检查锁定状态
            bool isAutoAssignType = string.Equals(request.taskType, RCS_UserTasks.TaskType.fgHandover.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(request.taskType, RCS_UserTasks.TaskType.shipment.ToString(), StringComparison.OrdinalIgnoreCase);

            if (destLocation.Lock && !isAutoAssignType)
            {
                // 终点被锁定，缓存任务而不是抛出异常（自动分配的任务类型除外）
                _logger.LogInformation($"终点 {request.destBin} 已被锁定，将任务缓存");
                await CacheTaskAsync(request, connection, transaction, $"终点 {request.destBin} 已被锁定");
                return new List<RCS_UserTasks>(); // 返回空列表表示任务已缓存
            }

            // 第三步：根据终点站点范围确定对应的小地牛中转点
            var smallAgvTransferPoint = GetSmallAgvTransferPointByStation(destLocation.Name);
            if (string.IsNullOrEmpty(smallAgvTransferPoint))
            {
                throw new ArgumentException($"终点站点 {destLocation.Name} 没有对应的小地牛中转点");
            }

            // 第四步：检查小地牛中转点是否可用
            var smallAgvLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = smallAgvTransferPoint },
                transaction);

            if (smallAgvLocation == null)
            {
                throw new ArgumentException($"小地牛中转点 {smallAgvTransferPoint} 不存在库位配置中");
            }

            if (smallAgvLocation.Lock)
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {smallAgvLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }

            // [Hotfix] 防止人工解锁后，中转点仍被执行中的任务重复占用
            if (await IsLocationOccupiedByActiveTaskAsync(smallAgvLocation.NodeRemark, connection, transaction))
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {smallAgvLocation.NodeRemark} 已被执行中任务占用");
                return new List<RCS_UserTasks>();
            }

            // 第五步：根据小地牛中转点确定对应的三向车中转点
            var threeWayTransferPoint = GetThreeWayTransferPointBySmallAgv(smallAgvTransferPoint);
            var threeWayLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = threeWayTransferPoint },
                transaction);

            if (threeWayLocation == null)
            {
                throw new ArgumentException($"三向车中转点 {threeWayTransferPoint} 不存在库位配置中");
            }

            if (threeWayLocation.Lock)
            {
                throw new ArgumentException($"三向车中转点 {threeWayTransferPoint} 已被锁定，不可以生成任务");
            }

            // [Hotfix] 防止人工解锁后，中转点仍被执行中的任务重复占用
            if (await IsLocationOccupiedByActiveTaskAsync(threeWayLocation.NodeRemark, connection, transaction))
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {threeWayLocation.NodeRemark} 已被执行中任务占用");
                return new List<RCS_UserTasks>();
            }

            // 创建第一个任务：从起点到小地牛中转点
            var firstTask = new RCS_UserTasks
            {
                taskStatus = TaskStatuEnum.None,
                creatTime = createTime,
                requestCode = $"{request.toNum}_1", // 第一个任务编号
                taskType = taskType,
                sourcePosition = request.sourceBin,
                targetPosition = smallAgvLocation.NodeRemark,
                executed = false,
                priority = 1,
                MaterialCode = request.material,
                MaterialQuantity = request.materialNum ?? 0,
                PalletNumber = request.suNum,
                SourceType = request.sourceType,
                DestType = request.destType,
                ConfirmTime = confirmTime,
                TaskGroupId = taskGroupId,
                TaskSequence = 1,
                IsSplitTask = true,
                OriginalTaskId = null
            };

            // 创建第二个任务：从三向车中转点到终点
            var secondTask = new RCS_UserTasks
            {
                taskStatus = TaskStatuEnum.None,
                creatTime = createTime,
                requestCode = $"{request.toNum}_2", // 第二个任务编号
                taskType = taskType,
                sourcePosition = threeWayLocation.NodeRemark,
                targetPosition = request.destBin, // 直接使用destBin作为终点
                executed = false,
                priority = 1,
                MaterialCode = request.material,
                MaterialQuantity = request.materialNum ?? 0,
                PalletNumber = request.suNum,
                SourceType = request.sourceType,
                DestType = request.destType,
                ConfirmTime = confirmTime,
                TaskGroupId = taskGroupId,
                TaskSequence = 2,
                IsSplitTask = true,
                OriginalTaskId = null
            };

            // [Hotfix] 使用条件更新抢占库位，避免并发下多个任务占用同一个中转位
            var lockedNodeRemarks = new List<string>();

            if (!await TryLockLocationByNodeRemarkAsync(request.sourceBin, connection, transaction))
            {
                await CacheTaskAsync(request, connection, transaction, $"起点 {request.sourceBin} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(request.sourceBin);

            if (!await TryLockLocationByNodeRemarkAsync(smallAgvLocation.NodeRemark, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"中转点 {smallAgvLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(smallAgvLocation.NodeRemark);

            if (!await TryLockLocationByNodeRemarkAsync(threeWayLocation.NodeRemark, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"中转点 {threeWayLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(threeWayLocation.NodeRemark);

            if (!await TryLockLocationByNodeRemarkAsync(request.destBin, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"终点 {request.destBin} 已被锁定");
                return new List<RCS_UserTasks>();
            }

            _logger.LogInformation("已锁定任务起点储位: {Name}", request.sourceBin);
            _logger.LogInformation("已锁定小地牛中转点储位: {Name}", smallAgvLocation.NodeRemark);
            _logger.LogInformation("已锁定三向车中转点储位: {Name}", threeWayLocation.NodeRemark);
            _logger.LogInformation("已锁定任务终点储位: {Name}", request.destBin);

            splitTasks.Add(firstTask);
            splitTasks.Add(secondTask);

            _logger.LogInformation("任务拆分完成，分组ID: {TaskGroupId}，第一个任务: {FirstTaskCode}，第二个任务: {SecondTaskCode}", taskGroupId, firstTask.requestCode, secondTask.requestCode);

            return splitTasks;
        }

        /// <summary>
        /// 根据站点范围获取对应的小地牛中转点
        /// </summary>
        /// <param name="stationName">站点名称（如100-131）</param>
        /// <returns>对应的小地牛中转点</returns>
        private string GetSmallAgvTransferPointByStation(string stationName)
        {
            // 根据图片中的映射关系：站点范围 → 小地牛中转点
            var mapping = new Dictionary<string, string>
            {
                { "100-131", "71" },
                { "132-195", "72" },
                { "196-259", "73" },
                { "260-323", "74" },
                { "324-387", "75" },
                { "388-419", "76" },
                { "420-467", "77" },
                { "468-515", "78" },
                { "516-563", "79" }
            };

            // 首先检查是否直接匹配完整范围格式
            if (mapping.ContainsKey(stationName))
            {
                return mapping[stationName];
            }

            // 然后检查是否匹配单个站点格式
            if (int.TryParse(stationName, out int stationNumber))
            {
                // 检查是否在100-131范围内
                if (stationNumber >= 100 && stationNumber <= 131)
                    return "71";

                // 检查是否在132-195范围内
                if (stationNumber >= 132 && stationNumber <= 195)
                    return "72";

                // 检查是否在196-259范围内
                if (stationNumber >= 196 && stationNumber <= 259)
                    return "73";

                // 检查是否在260-323范围内
                if (stationNumber >= 260 && stationNumber <= 323)
                    return "74";

                // 检查是否在324-387范围内
                if (stationNumber >= 324 && stationNumber <= 387)
                    return "75";

                // 检查是否在388-419范围内
                if (stationNumber >= 388 && stationNumber <= 419)
                    return "76";

                // 检查是否在420-467范围内
                if (stationNumber >= 420 && stationNumber <= 467)
                    return "77";

                // 检查是否在468-515范围内
                if (stationNumber >= 468 && stationNumber <= 515)
                    return "78";

                // 检查是否在516-563范围内
                if (stationNumber >= 516 && stationNumber <= 563)
                    return "79";
            }

            return null; // 没有找到对应的映射
        }

        /// <summary>
        /// 根据小地牛中转点获取对应的三向车中转点
        /// </summary>
        /// <param name="smallAgvPoint">小地牛中转点</param>
        /// <returns>对应的三向车中转点</returns>
        private string GetThreeWayTransferPointBySmallAgv(string smallAgvPoint)
        {
            // 根据图片中的映射关系：小地牛中转点 → 三向车中转点
            var mapping = new Dictionary<string, string>
            {
                { "71", "596" },
                { "72", "597" },
                { "73", "598" },
                { "74", "599" },
                { "75", "600" },
                { "76", "601" },
                { "77", "602" },
                { "78", "603" },
                { "79", "604" }
            };

            return mapping.ContainsKey(smallAgvPoint) ? mapping[smallAgvPoint] : null;
        }

        /// <summary>
        /// 根据站点范围获取对应的三向车中转点
        /// </summary>
        /// <param name="stationName">站点名称（如100-131）</param>
        /// <returns>对应的三向车中转点</returns>
        private string GetThreeWayTransferPointByStation(string stationName)
        {
            // 根据图片中的映射关系：站点范围 → 三向车中转点
            var mapping = new Dictionary<string, string>
            {
                { "100-131", "596" },
                { "132-195", "597" },
                { "196-259", "598" },
                { "260-323", "599" },
                { "324-387", "600" },
                { "388-419", "601" },
                { "420-467", "602" },
                { "468-515", "603" },
                { "516-563", "604" },
                { "801", "604" },
                { "802", "604" },
                { "803", "602" },
                { "804", "602" },
                { "805", "601" }
            };

            // 首先检查是否直接匹配完整范围格式
            if (mapping.ContainsKey(stationName))
            {
                return mapping[stationName];
            }

            // 然后检查是否匹配单个站点格式
            if (int.TryParse(stationName, out int stationNumber))
            {
                // 检查是否在100-131范围内
                if (stationNumber >= 100 && stationNumber <= 131)
                    return "596";

                // 检查是否在132-195范围内
                if (stationNumber >= 132 && stationNumber <= 195)
                    return "597";

                // 检查是否在196-259范围内
                if (stationNumber >= 196 && stationNumber <= 259)
                    return "598";

                // 检查是否在260-323范围内
                if (stationNumber >= 260 && stationNumber <= 323)
                    return "599";

                // 检查是否在324-387范围内
                if (stationNumber >= 324 && stationNumber <= 387)
                    return "600";

                // 检查是否在388-419范围内
                if (stationNumber >= 388 && stationNumber <= 419)
                    return "601";

                // 检查是否在420-467范围内
                if (stationNumber >= 420 && stationNumber <= 467)
                    return "602";

                // 检查是否在468-515范围内
                if (stationNumber >= 468 && stationNumber <= 515)
                    return "603";

                // 检查是否在516-563范围内
                if (stationNumber >= 516 && stationNumber <= 563)
                    return "604";

                // 检查是否为801或802（对应604）
                if (stationNumber == 801 || stationNumber == 802)
                    return "604";

                // 检查是否为803或804（对应602）
                if (stationNumber == 803 || stationNumber == 804)
                    return "602";

                // 检查是否为805（对应601）
                if (stationNumber == 805)
                    return "601";
            }

            return null; // 没有找到对应的映射
        }

        /// <summary>
        /// 根据三向车中转点获取对应的小地牛中转点
        /// </summary>
        /// <param name="threeWayPoint">三向车中转点</param>
        /// <returns>对应的小地牛中转点</returns>
        private string GetSmallAgvTransferPointByThreeWay(string threeWayPoint)
        {
            // 根据图片中的映射关系：三向车中转点 → 小地牛中转点
            var mapping = new Dictionary<string, string>
            {
                { "596", "71" },
                { "597", "72" },
                { "598", "73" },
                { "599", "74" },
                { "600", "75" },
                { "601", "76" },
                { "602", "77" },
                { "603", "78" },
                { "604", "79" }
            };

            return mapping.ContainsKey(threeWayPoint) ? mapping[threeWayPoint] : null;
        }

        /// <summary>
        /// 判断是否需要自动分配终点
        /// </summary>
        /// <param name="destBin">终点</param>
        /// <returns>是否需要自动分配</returns>
        private bool NeedsAutoAssignDest(string destBin)
        {
            // 需要自动分配的范围：PD001-PD019和PA021-PA040
            var autoAssignPatterns = new[] { "PD001-PD019", "PA021-PA040" };

            // 首先检查是否直接匹配完整范围格式
            if (autoAssignPatterns.Any(pattern => destBin.Contains(pattern)))
            {
                return true;
            }

            // 然后检查是否匹配单个储位格式
            if (destBin.StartsWith("PD"))
            {
                var numberPart = destBin.Substring(2);
                if (int.TryParse(numberPart, out int number))
                {
                    // 检查是否在PD001-PD019范围内 (1-19)
                    if (number >= 1 && number <= 19)
                        return true;
                }
            }
            else if (destBin.StartsWith("PA"))
            {
                var numberPart = destBin.Substring(2);
                if (int.TryParse(numberPart, out int number))
                {
                    // 检查是否在PA021-PA040范围内 (21-40)
                    if (number >= 21 && number <= 40)
                        return true;
                }
            }

            return false; // 不需要自动分配，直接使用destBin
        }

        /// <summary>
        /// 执行任务拆分逻辑（类型2：基于图片映射关系）
        /// </summary>
        /// <param name="request">原始任务请求</param>
        /// <param name="connection">数据库连接</param>
        /// <param name="transaction">事务</param>
        /// <returns>拆分后的任务列表</returns>
        private async Task<List<RCS_UserTasks>> SplitTaskType2Async(AddTaskRequest request, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            var splitTasks = new List<RCS_UserTasks>();
            var taskGroupId = Guid.NewGuid().ToString(); // 生成唯一的分组ID
            var createTime = DateTime.Now;
            var confirmTime = !string.IsNullOrEmpty(request.confirmTime) && DateTime.TryParse(request.confirmTime, out var parsedConfirmTime)
                ? parsedConfirmTime : (DateTime?)null;

            // 解析任务类型
            if (!Enum.TryParse<RCS_UserTasks.TaskType>(request.taskType, true, out var taskType))
            {
                throw new ArgumentException("无效的任务类型");
            }

            // 第一步：查找起点对应的RCS_Locations节点
            var sourceLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = request.sourceBin },
                transaction);

            if (sourceLocation == null)
            {
                throw new ArgumentException($"起点 {request.sourceBin} 不存在库位配置中");
            }

            if (sourceLocation.Lock)
            {
                // 起点被锁定，缓存任务而不是抛出异常
                _logger.LogInformation($"起点 {request.sourceBin} 已被锁定，将任务缓存");
                await CacheTaskAsync(request, connection, transaction, $"起点 {request.sourceBin} 已被锁定");
                return new List<RCS_UserTasks>(); // 返回空列表表示任务已缓存
            }

            // 第二步：查找终点对应的RCS_Locations节点
            var destLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = request.destBin },
                transaction);

            if (destLocation == null)
            {
                throw new ArgumentException($"终点 {request.destBin} 不存在库位配置中");
            }

            // 对于fgHandover和shipment任务类型，终点是自动分配并锁定的，不需要检查锁定状态
            bool isAutoAssignType = string.Equals(request.taskType, RCS_UserTasks.TaskType.fgHandover.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(request.taskType, RCS_UserTasks.TaskType.shipment.ToString(), StringComparison.OrdinalIgnoreCase);

            if (destLocation.Lock && !isAutoAssignType)
            {
                // 终点被锁定，缓存任务而不是抛出异常（自动分配的任务类型除外）
                _logger.LogInformation($"终点 {request.destBin} 已被锁定，将任务缓存");
                await CacheTaskAsync(request, connection, transaction, $"终点 {request.destBin} 已被锁定");
                return new List<RCS_UserTasks>(); // 返回空列表表示任务已缓存
            }

            // 第三步：根据第一个任务的起点站点范围确定对应的三向车中转点
            var threeWayTransferPoint = GetThreeWayTransferPointByStation(sourceLocation.Name);
            if (string.IsNullOrEmpty(threeWayTransferPoint))
            {
                throw new ArgumentException($"起点站点 {sourceLocation.Name} 没有对应的三向车中转点");
            }

            // 第四步：检查三向车中转点是否可用
            var threeWayLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = threeWayTransferPoint },
                transaction);

            if (threeWayLocation == null)
            {
                throw new ArgumentException($"三向车中转点 {threeWayTransferPoint} 不存在库位配置中");
            }

            if (threeWayLocation.Lock)
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {threeWayLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }

            // [Hotfix] 防止人工解锁后，中转点仍被执行中的任务重复占用
            if (await IsLocationOccupiedByActiveTaskAsync(threeWayLocation.NodeRemark, connection, transaction))
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {threeWayLocation.NodeRemark} 已被执行中任务占用");
                return new List<RCS_UserTasks>();
            }

            // 第五步：根据三向车中转点确定对应的小地牛中转点
            var smallAgvTransferPoint = GetSmallAgvTransferPointByThreeWay(threeWayTransferPoint);
            if (string.IsNullOrEmpty(smallAgvTransferPoint))
            {
                throw new ArgumentException($"三向车中转点 {threeWayTransferPoint} 没有对应的小地牛中转点");
            }

            // 第六步：检查小地牛中转点是否可用
            var smallAgvLocation = await connection.QueryFirstOrDefaultAsync<RCS_Locations>(
                "SELECT * FROM RCS_Locations WHERE Name = @Name",
                new { Name = smallAgvTransferPoint },
                transaction);

            if (smallAgvLocation == null)
            {
                throw new ArgumentException($"小地牛中转点 {smallAgvTransferPoint} 不存在库位配置中");
            }

            if (smallAgvLocation.Lock)
            {
                throw new ArgumentException($"小地牛中转点 {smallAgvTransferPoint} 已被锁定，不可以生成任务");
            }

            // [Hotfix] 防止人工解锁后，中转点仍被执行中的任务重复占用
            if (await IsLocationOccupiedByActiveTaskAsync(smallAgvLocation.NodeRemark, connection, transaction))
            {
                await CacheTaskAsync(request, connection, transaction, $"中转点 {smallAgvLocation.NodeRemark} 已被执行中任务占用");
                return new List<RCS_UserTasks>();
            }

            // 第七步：确定第二个任务的终点
            string secondTaskDest;
            bool needsAutoAssign = NeedsAutoAssignDest(request.destBin);

            if (request.taskType == RCS_UserTasks.TaskType.fgHandover.ToString() || request.taskType == RCS_UserTasks.TaskType.shipment.ToString())
            {

                secondTaskDest = request.destBin;


            }
            else if (needsAutoAssign)
            {
                // 需要自动分配：查找PD001-PD019和PA021-PA040范围内的空闲储位
                var autoAssignRanges = new List<string>();

                // PD001-PD019
                for (int i = 1; i <= 19; i++)
                {
                    autoAssignRanges.Add($"PD{i:D3}");
                }

                // PA021-PA040
                for (int i = 21; i <= 40; i++)
                {
                    autoAssignRanges.Add($"PA{i:D3}");
                }

                var autoAssignDestinations = await connection.QueryAsync<RCS_Locations>(
                    "SELECT * FROM RCS_Locations WHERE NodeRemark IN @NodeRemarks AND [Lock] = 0 AND (Quanitity IS NULL OR Quanitity = '' OR Quanitity = '0') ORDER BY NodeRemark",
                    new { NodeRemarks = autoAssignRanges },
                    transaction);

                var autoAssignDestList = autoAssignDestinations.ToList();
                if (!autoAssignDestList.Any())
                {
                    throw new InvalidOperationException("PD001-PD019和PA021-PA040范围内没有可用的空闲储位");
                }

                // [Hotfix] 逐个尝试原子锁定，避免并发下多个任务选中同一个自动分配终点
                secondTaskDest = null;
                foreach (var candidate in autoAssignDestList)
                {
                    if (await TryLockLocationByNodeRemarkAsync(candidate.NodeRemark, connection, transaction))
                    {
                        secondTaskDest = candidate.NodeRemark;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(secondTaskDest))
                {
                    await CacheTaskAsync(request, connection, transaction, "自动分配终点已被其他任务占用");
                    return new List<RCS_UserTasks>();
                }
            }
            else
            {
                // 直接使用destBin作为终点
                secondTaskDest = request.destBin;
            }

            // 创建第一个任务：从起点到三向车中转点
            var firstTask = new RCS_UserTasks
            {
                taskStatus = TaskStatuEnum.None,
                creatTime = createTime,
                requestCode = $"{request.toNum}_1", // 第一个任务编号
                taskType = taskType,
                sourcePosition = request.sourceBin,
                targetPosition = threeWayLocation.NodeRemark, // 第一个任务终点是三向车中转点
                executed = false,
                priority = 1,
                MaterialCode = request.material,
                MaterialQuantity = request.materialNum ?? 0,
                PalletNumber = request.suNum,
                SourceType = request.sourceType,
                DestType = request.destType,
                ConfirmTime = confirmTime,
                TaskGroupId = taskGroupId,
                TaskSequence = 1,
                IsSplitTask = true,
                OriginalTaskId = null
            };

            // 创建第二个任务：从小地牛中转点到终点
            var secondTask = new RCS_UserTasks
            {
                taskStatus = TaskStatuEnum.None,
                creatTime = createTime,
                requestCode = $"{request.toNum}_2", // 第二个任务编号
                taskType = taskType,
                sourcePosition = smallAgvLocation.NodeRemark, // 第二个任务起点是小地牛中转点
                targetPosition = secondTaskDest,
                executed = false,
                priority = 1,
                MaterialCode = request.material,
                MaterialQuantity = request.materialNum ?? 0,
                PalletNumber = request.suNum,
                SourceType = request.sourceType,
                DestType = request.destType,
                ConfirmTime = confirmTime,
                TaskGroupId = taskGroupId,
                TaskSequence = 2,
                IsSplitTask = true,
                OriginalTaskId = null
            };

            // [Hotfix] 使用条件更新抢占库位，避免并发下多个任务占用同一个中转位
            var lockedNodeRemarks = new List<string>();
            if (needsAutoAssign && !string.IsNullOrEmpty(secondTaskDest))
            {
                lockedNodeRemarks.Add(secondTaskDest);
            }

            if (!await TryLockLocationByNodeRemarkAsync(request.sourceBin, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"起点 {request.sourceBin} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(request.sourceBin);

            if (!await TryLockLocationByNodeRemarkAsync(smallAgvLocation.NodeRemark, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"中转点 {smallAgvLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(smallAgvLocation.NodeRemark);

            if (!await TryLockLocationByNodeRemarkAsync(threeWayLocation.NodeRemark, connection, transaction))
            {
                await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                await CacheTaskAsync(request, connection, transaction, $"中转点 {threeWayLocation.NodeRemark} 已被锁定");
                return new List<RCS_UserTasks>();
            }
            lockedNodeRemarks.Add(threeWayLocation.NodeRemark);

            if (!needsAutoAssign)
            {
                if (!await TryLockLocationByNodeRemarkAsync(secondTaskDest, connection, transaction))
                {
                    await UnlockLocationsByNodeRemarkAsync(lockedNodeRemarks, connection, transaction);
                    await CacheTaskAsync(request, connection, transaction, $"终点 {secondTaskDest} 已被锁定");
                    return new List<RCS_UserTasks>();
                }
            }

            _logger.LogInformation("已锁定任务起点储位: {Name}", request.sourceBin);
            _logger.LogInformation("已锁定小地牛中转点储位: {Name}", smallAgvLocation.NodeRemark);
            _logger.LogInformation("已锁定三向车中转点储位: {Name}", threeWayLocation.NodeRemark);
            _logger.LogInformation("已锁定任务终点储位: {Name}", secondTaskDest);

            splitTasks.Add(firstTask);
            splitTasks.Add(secondTask);

            _logger.LogInformation("任务拆分完成（类型2），分组ID: {TaskGroupId}，第一个任务: {FirstTaskCode}，第二个任务: {SecondTaskCode}",
                taskGroupId, firstTask.requestCode, secondTask.requestCode);

            return splitTasks;
        }

        /// <summary>
        /// 根据起点节点的Name值获取对应的三向车中转点（596-604）
        /// </summary>
        /// <param name="sourceNodeName">起点节点的Name值（如100-131）</param>
        /// <returns>三向车中转点</returns>
        private string GetThreeWayTransferPoint(string sourceNodeName)
        {
            // 根据图片中的映射关系：站点范围 → 三向车中转点
            var mapping = new Dictionary<string, string>
            {
                { "100-131", "596" },
                { "132-195", "597" },
                { "196-259", "598" },
                { "260-323", "599" },
                { "324-387", "600" },
                { "388-419", "601" },
                { "420-467", "602" },
                { "468-515", "603" },
                { "516-563", "604" }
            };

            // 如果没有找到具体映射，返回默认值
            return mapping.ContainsKey(sourceNodeName) ? mapping[sourceNodeName] : "596";
        }

        /// <summary>
        /// 根据三向车中转点获取对应的小地牛中转点（71-79）
        /// </summary>
        /// <param name="threeWayPoint">三向车中转点</param>
        /// <returns>小地牛中转点</returns>
        private string GetSmallAgvTransferPoint(string threeWayPoint)
        {
            // 根据图片中的映射关系：三向车中转点 → 小地牛中转点
            var mapping = new Dictionary<string, string>
            {
                { "596", "71" },
                { "597", "72" },
                { "598", "73" },
                { "599", "74" },
                { "600", "75" },
                { "601", "76" },
                { "602", "77" },
                { "603", "78" },
                { "604", "79" }
            };

            return mapping.ContainsKey(threeWayPoint) ? mapping[threeWayPoint] : "71";
        }

        // [Hotfix] 通过条件更新实现原子锁定，避免“先查未锁定、后更新”产生并发抢占问题
        private async Task<bool> TryLockLocationByNodeRemarkAsync(string nodeRemark, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            var affectedRows = await connection.ExecuteAsync(
                "UPDATE RCS_Locations SET [Lock] = 1 WHERE NodeRemark = @NodeRemark AND [Lock] = 0",
                new { NodeRemark = nodeRemark },
                transaction);

            return affectedRows > 0;
        }

        // [Hotfix] 拆分任务在中途抢锁失败时，释放本次事务里已抢到的点位，避免误锁
        private async Task UnlockLocationsByNodeRemarkAsync(IEnumerable<string> nodeRemarks, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            var validNodeRemarks = nodeRemarks?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            if (validNodeRemarks == null || !validNodeRemarks.Any())
            {
                return;
            }

            await connection.ExecuteAsync(
                "UPDATE RCS_Locations SET [Lock] = 0 WHERE NodeRemark IN @NodeRemarks",
                new { NodeRemarks = validNodeRemarks },
                transaction);
        }

        // [Hotfix] 防止人工解锁库位后，执行中的任务仍重复占用中转点
        private async Task<bool> IsLocationOccupiedByActiveTaskAsync(string nodeRemark, System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
        {
            var occupiedCount = await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(1)
                  FROM RCS_UserTasks
                  WHERE (sourcePosition = @NodeRemark OR targetPosition = @NodeRemark)
                    AND taskStatus < @TaskFinish",
                new
                {
                    NodeRemark = nodeRemark,
                    TaskFinish = (int)TaskStatuEnum.TaskFinish
                },
                transaction);

            return occupiedCount > 0;
        }


        /// <summary>
        /// 获取当前中国时间字符串
        /// </summary>
        private string GetChinaNowString()
        {
            return DateTime.UtcNow.AddHours(TimeOffset).ToString("yyyy-MM-ddTHH:mm:ss");
        }

        /// <summary>
        /// 格式化日期时间
        /// </summary>
        private string FormatDateTime(DateTime? dateTime)
        {
            if (!dateTime.HasValue)
            {
                return null;
            }

            var value = dateTime.Value.Kind == DateTimeKind.Utc
                ? dateTime.Value.AddHours(TimeOffset)
                : dateTime.Value;

            return value.ToString("yyyy-MM-ddTHH:mm:ss");
        }
    }
}
