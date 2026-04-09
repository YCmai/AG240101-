using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Db;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Services.Tasks
{
    public class TaskService : ITaskService
    {
        private readonly IDatabaseService _db;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TaskService> _logger;

        public TaskService(
            IDatabaseService db,
            IConfiguration configuration,
            ILogger<TaskService> logger)
        {
            _db = db;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<(List<RCS_UserTasks> Items, int TotalItems)> GetUserTasks(
            int page = 1,
            int pageSize = 10,
            DateTime? filterDate = null,
            DateTime? endDate = null,
            RCS_UserTasks.TaskType? taskType = null,
            string sortColumn = "creatTime",
            string sortDirection = "desc")
        {
            try
            {
                using var conn = _db.CreateConnection();

                // 使用明确的列名而不是SELECT *，确保列名与属性名匹配
                var query = @"SELECT 
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
                FROM RCS_UserTasks WHERE 1=1";

                var countQuery = "SELECT COUNT(*) FROM RCS_UserTasks WHERE 1=1";
                var parameters = new DynamicParameters();

                if (filterDate.HasValue)
                {
                    query += " AND creatTime >= @FilterDate";
                    countQuery += " AND creatTime >= @FilterDate";
                    parameters.Add("@FilterDate", filterDate.Value);
                }

                if (endDate.HasValue)
                {
                    query += " AND creatTime <= @EndDate";
                    countQuery += " AND creatTime <= @EndDate";
                    parameters.Add("@EndDate", endDate.Value.AddDays(1));
                }

                if (taskType.HasValue)
                {
                    query += " AND taskType = @TaskType";
                    countQuery += " AND taskType = @TaskType";
                    parameters.Add("@TaskType", (int)taskType.Value);
                }

                // 添加排序
                if (sortColumn == "priority")
                {
                    // 使用任务优先级进行排序
                    query += " ORDER BY CASE " +
                            "WHEN taskStatus IN (-1, 1, 2, 3, 4, 8) THEN 2 " + // 执行中任务
                            "WHEN DATEDIFF(MINUTE, creatTime, GETDATE()) > 30 THEN 1 " + // 异常任务
                            "ELSE 3 " + // 其他任务
                            "END " + sortDirection;
                }
                else
                {
                    query += $" ORDER BY {sortColumn} {sortDirection}";
                }

                query += " OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
                parameters.Add("@Offset", (page - 1) * pageSize);
                parameters.Add("@PageSize", pageSize);

                var items = await conn.QueryAsync<RCS_UserTasks>(query, parameters);
                var totalItems = await conn.ExecuteScalarAsync<int>(countQuery, parameters);

                // 记录查询到的数据，用于调试
                var itemsList = items.ToList();
                if (itemsList.Any())
                {
                    var firstItem = itemsList.First();
                    //_logger.LogInformation($"查询到任务数据: ID={firstItem.ID}, MaterialCode={firstItem.MaterialCode}, MaterialQuantity={firstItem.MaterialQuantity}, PalletNumber={firstItem.PalletNumber}");
                }
                else
                {
                    _logger.LogWarning("查询结果为空");
                }

                // 计算每个任务是否异常
                foreach (var item in itemsList)
                {
                    item.IsAbnormal = item.CheckIsAbnormal();
                }

                return (itemsList, totalItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务列表失败");
                throw;
            }
        }

        public async Task<(bool success, string message)> CancelTask(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                using var transaction = conn.BeginTransaction();

                try
                {
                    // 检查任务是否存在且可以取消
                    var task = await conn.QueryFirstOrDefaultAsync<RCS_UserTasks>(
                        "SELECT * FROM RCS_UserTasks WHERE Id = @Id",
                        new { Id = id },
                        transaction);

                    if (task == null)
                    {
                        return (false, "任务不存在");
                    }

                    if (task.taskStatus >= TaskStatuEnum.TaskFinish)
                    {
                        return (false, "已完成的任务不能取消");
                    }

                    if (task.IsCancelled)
                    {
                        return (false, "任务已经被取消");
                    }

                    // 检查是否是拆分任务
                    List<RCS_UserTasks> tasksToCancel = new List<RCS_UserTasks>();

                    if (task.IsSplitTask && !string.IsNullOrEmpty(task.TaskGroupId))
                    {
                        // 如果是拆分任务，查找同组的所有任务
                        var groupTasks = await conn.QueryAsync<RCS_UserTasks>(
                            "SELECT * FROM RCS_UserTasks WHERE TaskGroupId = @TaskGroupId",
                            new { TaskGroupId = task.TaskGroupId },
                            transaction);

                        tasksToCancel = groupTasks.Where(t =>
                            t.taskStatus != TaskStatuEnum.TaskFinish &&
                            t.taskStatus != TaskStatuEnum.Canceled &&
                            !t.IsCancelled).ToList();
                    }
                    else
                    {
                        // 普通任务，只取消当前任务
                        tasksToCancel.Add(task);
                    }

                    if (!tasksToCancel.Any())
                    {
                        return (false, "没有可取消的任务");
                    }

                    int cancelledCount = 0;
                    var cancelledTaskIds = new List<int>();

                    // 如果是拆分任务，需要按TaskSequence排序
                    if (task.IsSplitTask && !string.IsNullOrEmpty(task.TaskGroupId))
                    {
                        tasksToCancel = tasksToCancel.OrderBy(t => t.TaskSequence).ToList();
                    }

                    // 取消所有相关任务并解锁储位
                    foreach (var taskToCancel in tasksToCancel)
                    {
                        bool isFirstTask = task.IsSplitTask && taskToCancel.TaskSequence == 1;
                        bool isSecondTask = task.IsSplitTask && taskToCancel.TaskSequence == 2;

                        if (taskToCancel.taskStatus == TaskStatuEnum.None)
                        {
                            // 未执行的任务，直接更新状态为取消
                            var result = await conn.ExecuteAsync(@"
                                UPDATE RCS_UserTasks 
                                SET taskStatus = 30,
                                    executedTime = @ExecutedTime,
                                    endTime = @EndTime
                                WHERE Id = @Id",
                                new
                                {
                                    Id = taskToCancel.ID,
                                    ExecutedTime = DateTime.Now.AddHours(16),
                                    EndTime = DateTime.Now.AddHours(16)
                                },
                                transaction);

                            if (result > 0)
                            {
                                cancelledCount++;
                                cancelledTaskIds.Add(taskToCancel.ID);

                                // 未执行的任务，解锁起点和终点
                                if (!string.IsNullOrEmpty(taskToCancel.sourcePosition))
                                {
                                    await conn.ExecuteAsync(@"
                                        UPDATE RCS_Locations 
                                        SET [Lock] = 0 
                                        WHERE NodeRemark = @NodeRemark",
                                        new { NodeRemark = taskToCancel.sourcePosition },
                                        transaction);
                                    _logger.LogInformation($"已解锁任务 {taskToCancel.ID} 的起点: {taskToCancel.sourcePosition}");
                                }

                                if (!string.IsNullOrEmpty(taskToCancel.targetPosition))
                                {
                                    await conn.ExecuteAsync(@"
                                        UPDATE RCS_Locations 
                                        SET [Lock] = 0 
                                        WHERE NodeRemark = @NodeRemark",
                                        new { NodeRemark = taskToCancel.targetPosition },
                                        transaction);
                                    _logger.LogInformation($"已解锁任务 {taskToCancel.ID} 的终点: {taskToCancel.targetPosition}");
                                }
                            }
                        }
                        else
                        {
                            // 执行中的任务，设置IsCancelled标志
                            var result = await conn.ExecuteAsync(@"
                                UPDATE RCS_UserTasks 
                                SET IsCancelled = 1,
                                    executedTime = @ExecutedTime,
                                    endTime = @EndTime
                                WHERE Id = @Id",
                                new
                                {
                                    Id = taskToCancel.ID,
                                    ExecutedTime = DateTime.Now.AddHours(16),
                                    EndTime = DateTime.Now.AddHours(16)
                                },
                                transaction);

                            if (result > 0)
                            {
                                cancelledCount++;
                                cancelledTaskIds.Add(taskToCancel.ID);

                                // 对于拆分任务，根据任务状态和序号决定解锁哪些储位
                                if (isFirstTask)
                                {
                                    // 第一条任务：如果状态 < PickingUp，解锁终点
                                    if (taskToCancel.taskStatus < TaskStatuEnum.PickingUp)
                                    {
                                        if (!string.IsNullOrEmpty(taskToCancel.targetPosition))
                                        {
                                            await conn.ExecuteAsync(@"
                                                UPDATE RCS_Locations 
                                                SET [Lock] = 0 
                                                WHERE NodeRemark = @NodeRemark",
                                                new { NodeRemark = taskToCancel.targetPosition },
                                                transaction);
                                            _logger.LogInformation($"已解锁第一条任务 {taskToCancel.ID} 的终点: {taskToCancel.targetPosition}");
                                        }
                                    }
                                    // 如果状态 >= PickingUp 且 < TaskFinish，不解锁起点和终点
                                }
                                else if (isSecondTask)
                                {
                                    // 第二条任务：如果还没执行（状态为None），解锁起点和终点
                                    // 注意：这里isSecondTask的任务状态不可能是None，因为上面已经处理了None的情况
                                    // 但为了安全，还是检查一下
                                    if (taskToCancel.taskStatus == TaskStatuEnum.None)
                                    {
                                        if (!string.IsNullOrEmpty(taskToCancel.sourcePosition))
                                        {
                                            await conn.ExecuteAsync(@"
                                                UPDATE RCS_Locations 
                                                SET [Lock] = 0 
                                                WHERE NodeRemark = @NodeRemark",
                                                new { NodeRemark = taskToCancel.sourcePosition },
                                                transaction);
                                            _logger.LogInformation($"已解锁第二条任务 {taskToCancel.ID} 的起点: {taskToCancel.sourcePosition}");
                                        }

                                        if (!string.IsNullOrEmpty(taskToCancel.targetPosition))
                                        {
                                            await conn.ExecuteAsync(@"
                                                UPDATE RCS_Locations 
                                                SET [Lock] = 0 
                                                WHERE NodeRemark = @NodeRemark",
                                                new { NodeRemark = taskToCancel.targetPosition },
                                                transaction);
                                            _logger.LogInformation($"已解锁第二条任务 {taskToCancel.ID} 的终点: {taskToCancel.targetPosition}");
                                        }
                                    }
                                    else
                                    {
                                        // 第二条任务已执行，但还没完成，只解锁终点
                                        if (!string.IsNullOrEmpty(taskToCancel.targetPosition))
                                        {
                                            await conn.ExecuteAsync(@"
                                                UPDATE RCS_Locations 
                                                SET [Lock] = 0 
                                                WHERE NodeRemark = @NodeRemark",
                                                new { NodeRemark = taskToCancel.targetPosition },
                                                transaction);
                                            _logger.LogInformation($"已解锁第二条任务 {taskToCancel.ID} 的终点: {taskToCancel.targetPosition}");
                                        }
                                    }
                                }
                                else
                                {
                                    // 普通任务（非拆分任务）：执行中的任务不解锁储位
                                    // 因为已经取货或正在运输中，储位可能还在使用
                                }
                            }
                        }
                    }

                    if (cancelledCount == 0)
                    {
                        return (false, "任务取消失败");
                    }

                    transaction.Commit();

                    if (task.IsSplitTask)
                    {
                        _logger.LogInformation($"拆分任务分组 {task.TaskGroupId} 已成功取消，共取消 {cancelledCount} 个任务");
                        return (true, $"拆分任务分组已成功取消，共取消 {cancelledCount} 个任务");
                    }
                    else
                    {
                        _logger.LogInformation($"任务 {id} 已成功取消");
                        return (true, "任务已成功取消");
                    }
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"取消任务 {id} 时发生错误");
                return (false, $"取消任务失败：{ex.Message}");
            }
        }

        public async Task<List<RCS_UserTasks>> GetTasksForExport(DateTime? startDate, DateTime? endDate, RCS_UserTasks.TaskType? taskType)
        {
            try
            {
                using var conn = _db.CreateConnection();

                var query = @"SELECT 
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
                FROM RCS_UserTasks WHERE 1=1";
                var parameters = new DynamicParameters();

                if (startDate.HasValue)
                {
                    query += " AND creatTime >= @StartDate";
                    parameters.Add("@StartDate", startDate.Value);
                }

                if (endDate.HasValue)
                {
                    query += " AND creatTime <= @EndDate";
                    parameters.Add("@EndDate", endDate.Value.AddDays(1));
                }

                if (taskType.HasValue)
                {
                    query += " AND taskType = @TaskType";
                    parameters.Add("@TaskType", (int)taskType.Value);
                }

                query += " ORDER BY creatTime DESC";

                var items = await conn.QueryAsync<RCS_UserTasks>(query, parameters);
                return items.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取导出任务列表失败");
                throw;
            }
        }

        public async Task<object> GetTaskStatistics(DateTime startDate, DateTime endDate, string shift)
        {
            try
            {
                using var conn = _db.CreateConnection();

                // 获取所有任务类型的统计，按照新的状态分类
                var query = @"
                    SELECT 
                        taskType,
                        SUM(CASE WHEN taskStatus = -1 THEN 1 ELSE 0 END) as Pending,
                        SUM(CASE WHEN taskStatus > 0 AND taskStatus < 11 THEN 1 ELSE 0 END) as Executing,
                        SUM(CASE WHEN taskStatus = 11 THEN 1 ELSE 0 END) as Completed,
                        SUM(CASE WHEN taskStatus >= 30 THEN 1 ELSE 0 END) as Cancelled,
                        COUNT(*) as Total
                    FROM RCS_UserTasks
                    WHERE creatTime >= @StartDate AND creatTime <= @EndDate
                    GROUP BY taskType
                    ORDER BY taskType";

                var parameters = new DynamicParameters();
                parameters.Add("@StartDate", startDate);
                parameters.Add("@EndDate", endDate.AddDays(1));

                var statistics = await conn.QueryAsync(query, parameters);
                var result = statistics.ToList();

                // 准备返回数据
                var labels = new List<string>();
                var pending = new List<int>();
                var executing = new List<int>();
                var completed = new List<int>();
                var cancelled = new List<int>();
                var total = new List<int>();

                foreach (var stat in result)
                {
                    var taskType = (RCS_UserTasks.TaskType)stat.taskType;
                    // 创建临时对象来获取显示名称
                    var tempTask = new RCS_UserTasks { taskType = taskType };
                    labels.Add(tempTask.TaskTypeDisplayName);
                    pending.Add(stat.Pending);
                    executing.Add(stat.Executing);
                    completed.Add(stat.Completed);
                    cancelled.Add(stat.Cancelled);
                    total.Add(stat.Total);
                }

                return new
                {
                    labels = labels,
                    pending = pending,
                    executing = executing,
                    completed = completed,
                    cancelled = cancelled,
                    values = total
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务统计失败");
                throw;
            }
        }

        public async Task<(bool success, string message)> CreateTask(RCS_UserTasks task)
        {
            try
            {
                // 参数验证
                if (task == null)
                {
                    return (false, "任务信息不能为空");
                }

                if (string.IsNullOrWhiteSpace(task.sourcePosition))
                {
                    return (false, "起始点不能为空");
                }

                if (string.IsNullOrWhiteSpace(task.targetPosition))
                {
                    return (false, "终点不能为空");
                }

                if (task.sourcePosition == task.targetPosition)
                {
                    return (false, "起始点和终点不能相同");
                }

                if (string.IsNullOrWhiteSpace(task.requestCode))
                {
                    return (false, "请求编号不能为空");
                }

                // 验证优先级
                if (task.priority < 1 || task.priority > 3)
                {
                    return (false, "优先级必须在1-3之间");
                }

                // 设置任务默认值
                task.creatTime = DateTime.Now;
                //task.taskStatus = TaskStatuEnum.None;
                task.executed = false;
                task.IsCancelled = false;

                using var conn = _db.CreateConnection();

                // 检查是否已存在相同请求编号的任务
                var checkSql = "SELECT COUNT(1) FROM RCS_UserTasks WHERE requestCode = @requestCode";
                var existingCount = await conn.ExecuteScalarAsync<int>(checkSql, new { task.requestCode });
                if (existingCount > 0)
                {
                    return (false, "已存在相同请求编号的任务");
                }

                // 添加任务
                var sql = @"
                    INSERT INTO RCS_UserTasks (
                        taskStatus, executedTime, runTaskId, startTime, executed, 
                        creatTime, endTime, requestCode, taskType, priority, 
                        robotCode, sourcePosition, targetPosition, IsCancelled,
                        MaterialCode, MaterialQuantity, PalletNumber, SourceType, DestType, ConfirmTime
                    ) VALUES (
                        @taskStatus, @executedTime, @runTaskId, @startTime, @executed,
                        @creatTime, @endTime, @requestCode, @taskType, @priority,
                        @robotCode, @sourcePosition, @targetPosition, @IsCancelled,
                        @MaterialCode, @MaterialQuantity, @PalletNumber, @SourceType, @DestType, @ConfirmTime
                    )";

                var parameters = new DynamicParameters();
                parameters.Add("@taskStatus", task.taskStatus);
                parameters.Add("@executedTime", task.executedTime);
                parameters.Add("@runTaskId", task.runTaskId);
                parameters.Add("@startTime", task.startTime);
                parameters.Add("@executed", task.executed);
                parameters.Add("@creatTime", task.creatTime);
                parameters.Add("@endTime", task.endTime);
                parameters.Add("@requestCode", task.requestCode);
                parameters.Add("@taskType", task.taskType);
                parameters.Add("@priority", task.priority);
                parameters.Add("@robotCode", task.robotCode);
                parameters.Add("@sourcePosition", task.sourcePosition);
                parameters.Add("@targetPosition", task.targetPosition);
                parameters.Add("@IsCancelled", task.IsCancelled);
                parameters.Add("@MaterialCode", task.MaterialCode);
                parameters.Add("@MaterialQuantity", task.MaterialQuantity);
                parameters.Add("@PalletNumber", task.PalletNumber);
                parameters.Add("@SourceType", task.SourceType);
                parameters.Add("@DestType", task.DestType);
                parameters.Add("@ConfirmTime", task.ConfirmTime);

                var result = await conn.ExecuteAsync(sql, parameters);

                if (result > 0)
                {
                    _logger.LogInformation("任务创建成功");
                    return (true, "任务创建成功");
                }
                else
                {
                    _logger.LogWarning("任务创建失败");
                    return (false, "任务创建失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建任务失败");
                return (false, "创建任务失败：" + ex.Message);
            }
        }

        public async Task<List<RCS_Locations>> GetLocations()
        {
            try
            {
                using var conn = _db.CreateConnection();
                var locations = await conn.QueryAsync<RCS_Locations>("SELECT * FROM RCS_Locations WHERE Lock = 0");
                return locations.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取位置列表失败");
                throw;
            }
        }

        public async Task<RCS_TaskCache> GetCachedTaskById(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                var cachedTask = await conn.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                    "SELECT * FROM RCS_TaskCache WHERE Id = @Id",
                    new { Id = id });
                return cachedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缓存任务失败，ID: {Id}", id);
                throw;
            }
        }

        public async Task<(List<RCS_TaskCache> Items, int TotalItems)> GetCachedTasks(
            int page = 1,
            int pageSize = 10,
            RCS_UserTasks.TaskType? taskType = null)
        {
            try
            {
                using var conn = _db.CreateConnection();

                var query = @"SELECT * FROM RCS_TaskCache WHERE 1=1";
                var countQuery = "SELECT COUNT(*) FROM RCS_TaskCache WHERE 1=1";
                var parameters = new DynamicParameters();

                if (taskType.HasValue)
                {
                    query += " AND TaskType = @TaskType";
                    countQuery += " AND TaskType = @TaskType";
                    parameters.Add("@TaskType", (int)taskType.Value);
                }

                query += " ORDER BY CreateTime DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
                parameters.Add("@Offset", (page - 1) * pageSize);
                parameters.Add("@PageSize", pageSize);

                var items = await conn.QueryAsync<RCS_TaskCache>(query, parameters);
                var totalItems = await conn.ExecuteScalarAsync<int>(countQuery, parameters);

                return (items.ToList(), totalItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缓存任务列表失败");
                throw;
            }
        }

        public async Task<(bool success, string message)> RetryCachedTask(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                using var transaction = conn.BeginTransaction();

                try
                {
                    var cachedTask = await conn.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                        "SELECT * FROM RCS_TaskCache WHERE Id = @Id",
                        new { Id = id },
                        transaction);

                    if (cachedTask == null)
                    {
                        return (false, "缓存任务不存在");
                    }

                    await conn.ExecuteAsync(
                        "UPDATE RCS_TaskCache SET RetryCount = @RetryCount WHERE Id = @Id",
                        new { RetryCount = cachedTask.RetryCount + 1, Id = id },
                        transaction);

                    transaction.Commit();
                    return (true, "任务已标记为重试");
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重试缓存任务失败，ID: {Id}", id);
                return (false, $"重试任务发生错误: {ex.Message}");
            }
        }

        public async Task<(bool success, string message)> PrioritizeCachedTask(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                using var transaction = conn.BeginTransaction();

                try
                {
                    var cachedTask = await conn.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                        "SELECT * FROM RCS_TaskCache WHERE Id = @Id",
                        new { Id = id },
                        transaction);

                    if (cachedTask == null)
                    {
                        return (false, "缓存任务不存在");
                    }

                    if (cachedTask.Status != 0)
                    {
                        return (false, "只有待处理的缓存任务才可以设置为优先执行");
                    }

                    if (cachedTask.Priority == 10)
                    {
                        return (true, "该缓存任务已经是优先执行");
                    }

                    await conn.ExecuteAsync(
                        "UPDATE RCS_TaskCache SET Priority = @Priority WHERE Id = @Id",
                        new { Priority = 10, Id = id },
                        transaction);

                    transaction.Commit();
                    _logger.LogInformation("缓存任务 {Id} 已设置为优先执行，优先级: 10", id);
                    return (true, "任务已设置为优先执行");
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置缓存任务优先级失败，ID: {Id}", id);
                return (false, $"设置优先级发生错误: {ex.Message}");
            }
        }
        public async Task<(bool success, string message)> CancelCachedTask(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                using var transaction = conn.BeginTransaction();

                try
                {
                    // 查找缓存任务
                    var cachedTask = await conn.QueryFirstOrDefaultAsync<RCS_TaskCache>(
                        "SELECT * FROM RCS_TaskCache WHERE Id = @Id",
                        new { Id = id },
                        transaction);

                    if (cachedTask == null)
                    {
                        return (false, "缓存任务不存在");
                    }

                    // 更新任务状态为已取消
                    await conn.ExecuteAsync(
                        "UPDATE RCS_TaskCache SET Status = 3 WHERE Id = @Id",
                        new { Id = id },
                        transaction);

                    transaction.Commit();
                    return (true, "任务已取消");
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "取消缓存任务失败，ID: {Id}", id);
                return (false, $"取消任务发生错误: {ex.Message}");
            }
        }

        public async Task<(bool success, string message)> ClearAllCachedTasks()
        {
            try
            {
                using var conn = _db.CreateConnection();
                conn.Open();
                using var transaction = conn.BeginTransaction();

                try
                {
                    // 删除所有缓存任务并获取删除的数量
                    var deletedCount = await conn.ExecuteAsync(
                        "DELETE FROM RCS_TaskCache",
                        transaction: transaction);

                    transaction.Commit();
                    return (true, $"已清空所有缓存任务，共删除 {deletedCount} 个任务");
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空所有缓存任务异常");
                return (false, $"清空缓存任务发生错误: {ex.Message}");
            }
        }
    }
}
