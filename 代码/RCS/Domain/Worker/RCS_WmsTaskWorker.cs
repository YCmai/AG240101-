﻿using AciModule.Domain.Entitys;
using AciModule.Domain.Shared;
using AciModule.Domain.Worker;
using Spark.Domain.Entitys;
using Newtonsoft.Json;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Spark.Application;
using WMS.LineCallInputModule.Domain;
using Volo.Abp.Uow;
using static AciModule.Domain.Entitys.RCS_UserTasks;
using Microsoft.CodeAnalysis;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Spark.Domain.Worker
{
    /// <summary>
    /// 中间表数据
    /// </summary>
    public class RCS_WmsTaskWorker : RepeatBackgroundWorkerBase, ITransientDependency
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private IRepository<NdcTask_Moves, Guid> _ndcTaskRepos = default!;
        private IRepository<RCS_WmsTask> _wmmTasks = default!;
        private IRepository<RCS_UserTasks> _userTasks = default!;
        private IRepository<RCS_Locations> _locations = default!;
        private IRepository<RCS_ApiTask> _apiTask = default!;
        private IRepository<RCS_IOAGV_Tasks> _rcs_IOAGV_Tasks = default!;
        private LoggerManager _loggerManager = default!;
        private IConfiguration _configuration = default!;
        private const int TimeOffset = 8; // 北京时区偏移量（UTC+8）。无论服务器在哪个时区，使用 UtcNow + 8 都能得到统一的中国时间。

        public RCS_WmsTaskWorker(IServiceScopeFactory serviceScopeFactory) : base(1)
        {
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        public override async Task Execute(IJobExecutionContext context)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            InitializeScopedServices(scope.ServiceProvider);

            var unitOfWorkManager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            using var uow = unitOfWorkManager.Begin(requiresNew: true, isTransactional: false);

            await SafeExecute(CreateNewTasks, nameof(CreateNewTasks));
            await SafeExecute(UpdateWmsTasks, nameof(UpdateWmsTasks));
            await SafeExecute(CancleTaslk, nameof(CancleTaslk));

            await uow.CompleteAsync();
        }

        private void InitializeScopedServices(IServiceProvider serviceProvider)
        {
            _ndcTaskRepos = serviceProvider.GetRequiredService<IRepository<NdcTask_Moves, Guid>>();
            _wmmTasks = serviceProvider.GetRequiredService<IRepository<RCS_WmsTask>>();
            _userTasks = serviceProvider.GetRequiredService<IRepository<RCS_UserTasks>>();
            _locations = serviceProvider.GetRequiredService<IRepository<RCS_Locations>>();
            _apiTask = serviceProvider.GetRequiredService<IRepository<RCS_ApiTask>>();
            _rcs_IOAGV_Tasks = serviceProvider.GetRequiredService<IRepository<RCS_IOAGV_Tasks>>();
            _loggerManager = serviceProvider.GetRequiredService<LoggerManager>();
            _configuration = serviceProvider.GetRequiredService<IConfiguration>();
        }


        /// <summary>
        /// 封装任务执行方法，确保每个方法报错后不影响其他方法执行
        /// </summary>
        private async Task SafeExecute(Func<Task> taskFunc, string taskName)
        {
            try
            {
                await taskFunc();
            }
            catch (Exception ex)
            {
                await _loggerManager.LogAndLogError($"{taskName} 执行时发生异常: {ex.Message}");
            }
        }



        private async Task CancleTaslk()
        {
            var cancelTasks = await _userTasks.GetListAsync(x => x.taskStatus < TaskStatuEnum.TaskFinish && x.IsCancelled);

            foreach (var cancelTask in cancelTasks)
            {
                var task = await _ndcTaskRepos.FindAsync(x => x.SchedulTaskNo == cancelTask.requestCode);

                if (task != null)
                {
                    //要把储位的分配状态也去掉
                    if (task.TaskStatus == TaskStatuEnum.None || task.TaskStatus == TaskStatuEnum.CarWash)
                    {
                        task.SetStatus(TaskStatuEnum.Canceled);
                        await _ndcTaskRepos.UpdateAsync(task);
                        
                    }
                    else
                    {
                        if (!task.CancelTask)
                        {
                            task.CancelTask = true;
                            await _ndcTaskRepos.UpdateAsync(task);
                        }
                    }
                }
                else
                {
                    cancelTask.taskStatus = TaskStatuEnum.Canceled;
                    await _userTasks.UpdateAsync(cancelTask);
                }
            }

        }

        /// <summary>
        /// 更新任务状态
        /// </summary>
        /// <returns></returns>
        private async Task UpdateWmsTasks()
        {
            // 获取未完成的任务
            var taskStatusesToExclude = new List<TaskStatuEnum> { TaskStatuEnum.Canceled, TaskStatuEnum.TaskFinish };
            var tasksToUpdate = await _userTasks.GetListAsync(i => !taskStatusesToExclude.Contains(i.taskStatus));
            var taskCodesToUpdate = tasksToUpdate.Select(i => i.requestCode).Distinct();

            // 获取对应的 NDC 任务
            var ndcTasks = await _ndcTaskRepos.GetListAsync(x => taskCodesToUpdate.Contains(x.SchedulTaskNo));

            foreach (var userTask in tasksToUpdate)
            {
                var ndcTask = ndcTasks.FirstOrDefault(x => x.SchedulTaskNo == userTask.requestCode);
                if (ndcTask == null || ndcTask.TaskStatus == userTask.taskStatus) continue;

                // 更新任务状态
                userTask.taskStatus = ndcTask.TaskStatus;
                userTask.robotCode = ndcTask.AgvId.ToString();
                
                // 处理储位状态
                if (ndcTask.TaskStatus == TaskStatuEnum.TaskFinish)
                {
                    await HandleTaskFinish(userTask);
                }
                else if (ndcTask.TaskStatus >= TaskStatuEnum.PickDown &&
                         ndcTask.TaskStatus < TaskStatuEnum.TaskFinish)
                {
                    await HandleTaskReadyForReuse(userTask, ndcTask.TaskStatus);
                }
                else if (ndcTask.TaskStatus == TaskStatuEnum.Canceled)
                {
                    await HandleTaskCancel(userTask);

                }else if (ndcTask.TaskStatus == TaskStatuEnum.TaskStart || ndcTask.TaskStatus == TaskStatuEnum.Confirm)
                {
                    var now = DateTime.UtcNow.AddHours(TimeOffset);
                    userTask.startTime = now;
                    if (userTask.ConfirmTime == null)
                    {
                        userTask.ConfirmTime = now;
                    }
                }

                // 更新用户任务
                await _userTasks.UpdateAsync(userTask);
                
                await _loggerManager.LogAndLogCritical(
                    $"更新任务 {userTask.requestCode} 状态为 {userTask.taskStatus}，" +
                    $"任务类型：{userTask.taskType}，" +
                    $"起点：{userTask.sourcePosition}，" +
                    $"终点：{userTask.targetPosition}");
            }
        }

        /// <summary>
        /// 处理任务完成状态
        /// </summary>
        private async Task HandleTaskFinish(RCS_UserTasks userTask)
        {
            if (userTask.IsSplitTask && !string.IsNullOrEmpty(userTask.TaskGroupId))
            {
                // 拆分任务的处理逻辑
                await HandleSplitTaskFinish(userTask);
            }
            else
            {
                // 普通任务的处理逻辑
                await HandleNormalTaskFinish(userTask);
            }

            userTask.endTime = DateTime.UtcNow.AddHours(TimeOffset);


            await _userTasks.UpdateAsync(userTask);

            // 发送任务完成状态
            if (userTask.IsSplitTask)
            {
                // 拆分任务：只有最后一个任务完成才发送完成状态
                var groupTasks = await _userTasks.GetListAsync(x => x.TaskGroupId == userTask.TaskGroupId);
                var maxSequence = groupTasks.Max(t => t.TaskSequence);
                if (userTask.TaskSequence == maxSequence)
                {
                    await SendTaskStatusToHost(userTask.PalletNumber ?? userTask.requestCode, "3");
                }
            }
            else
            {
                await SendTaskStatusToHost(userTask.requestCode, "3");
            }
        }

        /// <summary>
        /// 处理拆分任务第二段进入可复用阶段。
        /// </summary>
        /// <remarks>
        /// 拆分任务第二段在到达 PickDown 及之后、但尚未完成前，
        /// 说明中转位上的货物通常已经被AGV取走，允许后续任务尽快复用该中转位。
        /// 这里放宽判断，兼容现场状态直接跳过 PickDown 的情况。
        /// </remarks>
        private async Task HandleTaskReadyForReuse(RCS_UserTasks userTask, TaskStatuEnum currentStatus)
        {
            if (!userTask.IsSplitTask || userTask.TaskSequence <= 1)
            {
                return;
            }

            var groupTasks = await _userTasks.GetListAsync(x => x.TaskGroupId == userTask.TaskGroupId);
            var firstTask = groupTasks.FirstOrDefault(t => t.TaskSequence == 1);
            var locationsToRelease = new List<RCS_Locations>();

            if (firstTask != null)
            {
                var firstTaskTargetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == firstTask.targetPosition);
                if (firstTaskTargetLocation != null)
                {
                    locationsToRelease.Add(firstTaskTargetLocation);
                }
            }

            var secondTaskSourceLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.sourcePosition);
            if (secondTaskSourceLocation != null)
            {
                if (locationsToRelease.All(l => l.Id != secondTaskSourceLocation.Id))
                {
                    locationsToRelease.Add(secondTaskSourceLocation);
                }
            }

            if (!locationsToRelease.Any())
            {
                await _loggerManager.LogAndLogError(
                    $"拆分任务第二段提前放行失败，未找到可释放的中转位：{userTask.requestCode}，当前状态：{currentStatus}，第一段终点：{firstTask?.targetPosition}，第二段起点：{userTask.sourcePosition}");
                return;
            }

            foreach (var location in locationsToRelease)
            {
                location.Lock = false;
                location.MaterialCode = null;
                location.PalletID = null;
                location.Weight = "0";
                location.Quanitity = "0";
                await _locations.UpdateAsync(location);
            }

            var releasedNodeRemarks = string.Join("、", locationsToRelease.Select(l => l.NodeRemark ?? l.Name));

            await _loggerManager.LogAndLogCritical(
                $"拆分任务第二段进入可复用阶段，提前释放成对中转位：{userTask.requestCode}，释放点位：{releasedNodeRemarks}，当前状态：{currentStatus}");
        }

        /// <summary>
        /// 处理拆分任务完成
        /// </summary>
        private async Task HandleSplitTaskFinish(RCS_UserTasks userTask)
        {
            var groupTasks = await _userTasks.GetListAsync(x => x.TaskGroupId == userTask.TaskGroupId);
            var sortedGroupTasks = groupTasks.OrderBy(t => t.TaskSequence).ToList();

            if (userTask.TaskSequence == 1)
            {
                // 第一个任务完成：只解锁取货点，物料移动到第一个任务的终点
                var sourceLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.sourcePosition);
                var targetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.targetPosition);

                if (sourceLocation != null)
                {
                    sourceLocation.Lock = false; // 解锁取货点
                    await _locations.UpdateAsync(sourceLocation);
                }

                if (targetLocation != null)
                {
                    // 物料移动到第一个任务的终点（小地牛中转点）
                    targetLocation.MaterialCode = userTask.MaterialCode;
                    targetLocation.PalletID = userTask.PalletNumber;
                    targetLocation.Weight = "0";
                    targetLocation.Quanitity = "满";
                    await _locations.UpdateAsync(targetLocation);
                }

                // 同时更新对应的三向车中转点（第二个任务的起点）
                var correspondingThreeWayLocation = GetCorrespondingThreeWayLocation(userTask.targetPosition);
                if (correspondingThreeWayLocation != null)
                {
                    var threeWayLocation = await _locations.FirstOrDefaultAsync(l => l.Name == correspondingThreeWayLocation);
                    if (threeWayLocation != null)
                    {
                        // 三向车中转点也设置为有物料状态
                        threeWayLocation.MaterialCode = userTask.MaterialCode;
                        threeWayLocation.PalletID = userTask.PalletNumber;
                        threeWayLocation.Weight = "0";
                        threeWayLocation.Quanitity = "满";
                        await _locations.UpdateAsync(threeWayLocation);
                    }
                }

                await _loggerManager.LogAndLogCritical(
                    $"拆分任务第一个任务完成：{userTask.requestCode}，物料已移动到中转点：{userTask.targetPosition}，对应三向车中转点：{correspondingThreeWayLocation}");
            }
            else
            {
                // 第二个任务完成：解锁所有相关储位，处理物料状态
                var firstTask = sortedGroupTasks.FirstOrDefault(t => t.TaskSequence == 1);
                if (firstTask != null)
                {
                    // 解锁第一个任务的终点（中转点）
                    var firstTaskTargetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == firstTask.targetPosition);
                    if (firstTaskTargetLocation != null)
                    {
                        firstTaskTargetLocation.Lock = false;
                        firstTaskTargetLocation.MaterialCode = null;
                        firstTaskTargetLocation.PalletID = null;
                        firstTaskTargetLocation.Weight = "0";
                        firstTaskTargetLocation.Quanitity = "0";
                        await _locations.UpdateAsync(firstTaskTargetLocation);
                    }
                }

                // 解锁第二个任务的起点（中转点）
                var secondTaskSourceLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.sourcePosition);
                if (secondTaskSourceLocation != null)
                {
                    secondTaskSourceLocation.Lock = false;
                    secondTaskSourceLocation.MaterialCode = null;
                    secondTaskSourceLocation.PalletID = null;
                    secondTaskSourceLocation.Weight = "0";
                    secondTaskSourceLocation.Quanitity = "0";
                    await _locations.UpdateAsync(secondTaskSourceLocation);
                }

                // 处理第二个任务的终点
                var secondTaskTargetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.targetPosition);
                if (secondTaskTargetLocation != null)
                {
                    secondTaskTargetLocation.Lock = false;
                    secondTaskTargetLocation.MaterialCode = userTask.MaterialCode;
                    secondTaskTargetLocation.PalletID = userTask.PalletNumber;
                    secondTaskTargetLocation.Weight = "0";
                    secondTaskTargetLocation.Quanitity = "满";
                    await _locations.UpdateAsync(secondTaskTargetLocation);
                }

                await _loggerManager.LogAndLogCritical(
                    $"拆分任务第二个任务完成：{userTask.requestCode}，物料已移动到最终目标：{userTask.targetPosition}");
            }
        }

        /// <summary>
        /// 处理普通任务完成
        /// </summary>
        private async Task HandleNormalTaskFinish(RCS_UserTasks userTask)
        {
           
            // 其他任务类型：解锁源储位和目标储位
            var sourceLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.sourcePosition);
            var targetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == userTask.targetPosition);

            if (sourceLocation != null)
            {
                sourceLocation.Lock = false;
                await _locations.UpdateAsync(sourceLocation);
            }

            if (targetLocation != null)
            {
                targetLocation.Lock = false;
                targetLocation.MaterialCode = userTask.MaterialCode;
                targetLocation.PalletID = userTask.PalletNumber;
                targetLocation.Weight = "0";
                targetLocation.Quanitity = "满";
                await _locations.UpdateAsync(targetLocation);
            }
            
        }

        /// <summary>
        /// 处理任务取消状态
        /// </summary>
        private async Task HandleTaskCancel(RCS_UserTasks userTask)
        {
            if (userTask.IsSplitTask && !string.IsNullOrEmpty(userTask.TaskGroupId))
            {
                // 拆分任务取消：取消整个分组
                var groupTasks = await _userTasks.GetListAsync(x => x.TaskGroupId == userTask.TaskGroupId);
                foreach (var groupTask in groupTasks)
                {
                    await UnlockTaskLocations(groupTask);
                }
            }
            else
            {
                // 普通任务取消
                await UnlockTaskLocations(userTask);
            }

            userTask.endTime = DateTime.UtcNow.AddHours(TimeOffset);

            await _userTasks.UpdateAsync(userTask);


            // 发送任务取消状态
            if (userTask.IsSplitTask)
            {
                await SendTaskStatusToHost(userTask.PalletNumber ?? userTask.requestCode, "4");
            }
            else
            {
                await SendTaskStatusToHost(userTask.requestCode, "4");
            }
        }

        /// <summary>
        /// 解锁任务相关的储位
        /// </summary>
        private async Task UnlockTaskLocations(RCS_UserTasks task)
        {
            // 获取源储位和目标储位
            var sourceLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == task.sourcePosition);
            var targetLocation = await _locations.FirstOrDefaultAsync(l => l.NodeRemark == task.targetPosition);

            // 解锁源储位
            if (sourceLocation != null)
            {
                sourceLocation.Lock = false;
                await _locations.UpdateAsync(sourceLocation);
            }

            // 解锁目标储位
            if (targetLocation != null)
            {
                targetLocation.Lock = false;
                await _locations.UpdateAsync(targetLocation);
            }
            
        }

        /// <summary>
        /// 获取对应的小地牛中转点（71-79）
        /// </summary>
        /// <param name="threeWayLocationName">三向车中转点名称（596-604）</param>
        /// <returns>对应的小地牛中转点名称</returns>
        private string? GetCorrespondingSmallAgvLocation(string threeWayLocationName)
        {
            if (int.TryParse(threeWayLocationName, out int threeWayNum) && threeWayNum >= 596 && threeWayNum <= 604)
            {
                int correspondingSmallAgv = threeWayNum - 525; // 596 - 525 = 71, 597 - 525 = 72, ...
                return correspondingSmallAgv.ToString();
            }
            return null;
        }

        /// <summary>
        /// 获取对应的三向车中转点（596-604）
        /// </summary>
        /// <param name="smallAgvLocationName">小地牛中转点名称（71-79）</param>
        /// <returns>对应的三向车中转点名称</returns>
        private string? GetCorrespondingThreeWayLocation(string smallAgvLocationName)
        {
            if (int.TryParse(smallAgvLocationName, out int smallAgvNum) && smallAgvNum >= 71 && smallAgvNum <= 79)
            {
                int correspondingThreeWay = smallAgvNum + 525; // 71 + 525 = 596, 72 + 525 = 597, ...
                return correspondingThreeWay.ToString();
            }
            return null;
        }


        /// <summary>
        /// 创建新的NDC任务
        /// </summary>
        /// <returns></returns>
        private async Task CreateNewTasks()
        {
            try
            {
                // 获取状态为 None 的任务
                var carWashTasks = await _userTasks.GetListAsync(x => x.taskStatus == TaskStatuEnum.None);
                if (!carWashTasks.Any()) return;

                // 获取所有位置信息
                var locations = await _locations.GetListAsync();

                // 获取未完成的 NDC 任务
                var unfinishedNdcTasks = await _ndcTaskRepos.GetListAsync(
                    x => x.TaskStatus != TaskStatuEnum.TaskFinish &&
                         x.TaskStatus != TaskStatuEnum.Canceled);

                // 按分组和任务序号排序，确保拆分任务按顺序执行
                var sortedTasks = carWashTasks
                    .OrderBy(t => t.TaskGroupId ?? t.requestCode) // 先按分组排序
                    .ThenBy(t => t.TaskSequence) // 再按任务序号排序
                    .ToList();

                // 预加载拆分任务分组，避免循环内重复查询
                var splitGroupIds = sortedTasks
                    .Where(t => t.IsSplitTask && !string.IsNullOrEmpty(t.TaskGroupId))
                    .Select(t => t.TaskGroupId!)
                    .Distinct()
                    .ToList();

                var groupTaskMap = new Dictionary<string, List<RCS_UserTasks>>();
                if (splitGroupIds.Any())
                {
                    var allGroupTasks = await _userTasks.GetListAsync(x => !string.IsNullOrEmpty(x.TaskGroupId) && splitGroupIds.Contains(x.TaskGroupId));
                    groupTaskMap = allGroupTasks
                        .GroupBy(t => t.TaskGroupId!)
                        .ToDictionary(g => g.Key, g => g.OrderBy(t => t.TaskSequence).ToList());
                }

                foreach (var task in sortedTasks)
                {
                    // 检查是否已存在相同的未完成任务
                    if (unfinishedNdcTasks.Any(e => e.SchedulTaskNo == task.requestCode))
                        continue;

                    // 如果是拆分任务，检查前一个任务是否已完成
                    if (task.IsSplitTask && !string.IsNullOrEmpty(task.TaskGroupId))
                    {
                        if (!groupTaskMap.TryGetValue(task.TaskGroupId, out var sortedGroupTasks))
                        {
                            await _loggerManager.LogAndLogError($"拆分任务 {task.requestCode} 未找到分组信息，TaskGroupId: {task.TaskGroupId}");
                            continue;
                        }
                        
                        // 检查当前任务是否是第一个任务
                        var isFirstTask = task.TaskSequence == 1;
                        
                        if (!isFirstTask)
                        {
                            // 不是第一个任务，前一个任务到达“卸货完成”后才允许下发
                            var previousTask = sortedGroupTasks.FirstOrDefault(t => t.TaskSequence == task.TaskSequence - 1);
                            var previousTaskReady = previousTask != null &&
                                                    (previousTask.taskStatus == TaskStatuEnum.PickDown ||
                                                     previousTask.taskStatus == TaskStatuEnum.TaskFinish);

                            if (!previousTaskReady)
                            {
                                var previousStatus = previousTask == null ? "null" : previousTask.taskStatus.ToString();
                                //await _loggerManager.LogAndLogCritical(
                                //    $"拆分任务 {task.requestCode} 等待前一个任务卸货完成，当前序号：{task.TaskSequence}，前序状态：{previousStatus}");
                                continue;
                            }
                        }
                    }

                    // 获取起点和终点位置信息
                    var pickupLocation = locations.FirstOrDefault(l => l.NodeRemark == task.sourcePosition);
                    var unloadLocation = locations.FirstOrDefault(l => l.NodeRemark == task.targetPosition);

                    if (pickupLocation == null || unloadLocation == null)
                    {
                        await _loggerManager.LogAndLogError(
                            $"任务 {task.requestCode} 的起点或终点位置无效。起点：{task.sourcePosition}，终点：{task.targetPosition}");
                        task.taskStatus = TaskStatuEnum.Canceled;
                        task.endTime = DateTime.UtcNow.AddHours(TimeOffset);
                        await _userTasks.UpdateAsync(task);
                        continue;
                    }

                    var ndcModel = await _ndcTaskRepos.FirstOrDefaultAsync(x => x.SchedulTaskNo == task.requestCode);

                    if (ndcModel != null) { continue; }

                    // 创建新的 NDC 任务，注意类型转换
                    var newTask = new NdcTask_Moves(
                        Guid.NewGuid(),                    // Id: Guid
                        Guid.NewGuid(),                    // TenantId: Guid
                        task.taskType.ToString(),          // TaskName: string
                        0,                                 // TaskNo: int
                        task.requestCode,                     // SchedulTaskNo: string
                        Convert.ToInt32(task.taskType),               // TaskType: int，从枚举转换为int
                        "K",                               // TaskMode: string
                        Convert.ToInt32(pickupLocation.Name),       // PickupPoint: string
                        Convert.ToInt32(pickupLocation.LiftingHeight),  // PickupHeight: int
                        Convert.ToInt32(unloadLocation.Name),       // UnloadPoint: string
                        Convert.ToInt32(unloadLocation.UnloadHeight),  // UnloadHeight: int
                        0     // Priority: int
                    );

                    await _ndcTaskRepos.InsertAsync(newTask);
                    await _loggerManager.LogAndLogCritical(
                        $"下发任务号 {task.requestCode}, 取料点: {task.sourcePosition}, 卸料点: {task.targetPosition} 任务成功！");

                    // 发送任务开始状态（只有拆分任务的第一个任务或普通任务才发送）
                    if (!task.IsSplitTask || task.TaskSequence == 1)
                    {
                        await SendTaskStatusToHost(task.PalletNumber ?? task.requestCode, "0");
                    }

                    break;
                }
            }
            catch (Exception ex)
            {

                await _loggerManager.LogAndLogError($"创建任务失败{ex.Message}");
            }
        }

        /// <summary>
        /// 发送任务状态到上位机
        /// </summary>
        /// <param name="taskCode">任务编码</param>
        /// <param name="status">状态：0-初始化，1-已接收，2-已接盘，3-任务完成，4-任务取消</param>
        /// <returns></returns>
        private async Task SendTaskStatusToHost(string taskCode, string status)
        {
            try
            {
                var apiUrl = _configuration["MyConfig:QhUrl"];
                if (string.IsNullOrEmpty(apiUrl))
                {
                    await _loggerManager.LogAndLogError("MyConfig:QhUrl，无法发送任务状态");
                    return;
                }

                using (var client = new HttpClient())
                {
                    // 构建请求参数
                    var requestData = new
                    {
                        toNum = taskCode,
                        status = status
                    };

                    // 序列化请求数据
                    var jsonContent = JsonConvert.SerializeObject(requestData);
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    // 发送请求
                    var response = await client.PostAsync(apiUrl, content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        await _loggerManager.LogAndLogCritical($"任务状态上传成功. 任务编码: {taskCode}, 状态: {status}, 响应: {responseContent}");
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        await _loggerManager.LogAndLogError($"任务状态上传失败. 任务编码: {taskCode}, 状态: {status}, HTTP状态码: {response.StatusCode}, 错误: {errorContent}");
                    }
                }
            }
            catch (Exception ex)
            {
                await _loggerManager.LogAndLogError($"发送任务状态异常. 任务编码: {taskCode}, 状态: {status}, 异常: {ex.Message}");
            }
        }
    }

}
