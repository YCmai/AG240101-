using Dapper;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service
{
    /// <summary>
    /// fgHandover 和 shipment 的自动终点分配服务。
    /// </summary>
    public class AutoDestinationAllocator : IAutoDestinationAllocator
    {
        private readonly ILogger<AutoDestinationAllocator> _logger;

        public AutoDestinationAllocator(ILogger<AutoDestinationAllocator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 判断任务类型是否需要系统自动分配终点。
        /// </summary>
        /// <param name="taskType">任务类型名称。</param>
        /// <returns>fgHandover 或 shipment 返回 true。</returns>
        public bool IsAutoDestinationTaskType(string taskType)
        {
            return string.Equals(taskType, RCS_UserTasks.TaskType.fgHandover.ToString(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(taskType, RCS_UserTasks.TaskType.shipment.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 尝试选择并锁定自动分配终点。
        /// </summary>
        /// <param name="taskType">任务类型名称。</param>
        /// <param name="connection">当前数据库连接。</param>
        /// <param name="transaction">当前数据库事务。</param>
        /// <returns>自动终点分配结果。</returns>
        public async Task<AutoDestinationAllocationResult> TryAllocateAsync(
            string taskType,
            IDbConnection connection,
            IDbTransaction transaction)
        {
            var groupName = ResolveGroupName(taskType);
            if (string.IsNullOrWhiteSpace(groupName))
            {
                return AutoDestinationAllocationResult.Failed(
                    null,
                    $"任务类型 {taskType} 不需要自动分配终点");
            }

            var locations = (await connection.QueryAsync<RCS_Locations>(@"
                SELECT *
                FROM RCS_Locations
                WHERE [Group] LIKE '%' + @GroupName + '%'
                    AND ([Lock] = 0 OR [Lock] IS NULL)
                    AND (Quanitity IS NULL OR Quanitity = '' OR Quanitity = '0')
                ORDER BY NEWID()",
                new { GroupName = groupName },
                transaction)).ToList();

            if (!locations.Any())
            {
                var message = $"自动分配终点失败：任务类型 {taskType} 对应分组 {groupName} 暂无空闲未锁定储位";
                _logger.LogWarning(message);
                return AutoDestinationAllocationResult.Failed(groupName, message);
            }

            foreach (var location in locations)
            {
                // 这里必须使用 NodeRemark 做条件更新，避免同一个 Name 下多层储位被一起锁定。
                var affectedRows = await connection.ExecuteAsync(@"
                    UPDATE RCS_Locations
                    SET [Lock] = 1
                    WHERE NodeRemark = @NodeRemark
                        AND ([Lock] = 0 OR [Lock] IS NULL)",
                    new { location.NodeRemark },
                    transaction);

                if (affectedRows > 0)
                {
                    var message = $"自动分配终点成功：任务类型 {taskType}，分组 {groupName}，终点 {location.NodeRemark}";
                    _logger.LogInformation(
                        "自动分配终点成功：TaskType={TaskType}，GroupName={GroupName}，Name={Name}，NodeRemark={NodeRemark}",
                        taskType,
                        groupName,
                        location.Name,
                        location.NodeRemark);

                    return AutoDestinationAllocationResult.Successful(location.NodeRemark, groupName, message);
                }

                _logger.LogInformation(
                    "自动分配终点候选抢锁失败，继续尝试下一个候选：TaskType={TaskType}，GroupName={GroupName}，Name={Name}，NodeRemark={NodeRemark}",
                    taskType,
                    groupName,
                    location.Name,
                    location.NodeRemark);
            }

            var lockFailedMessage = $"自动分配终点失败：任务类型 {taskType} 对应分组 {groupName} 的候选储位均被并发任务抢占";
            _logger.LogWarning(lockFailedMessage);
            return AutoDestinationAllocationResult.Failed(groupName, lockFailedMessage);
        }

        /// <summary>
        /// 释放已经预分配但未成功生成正式任务的终点。
        /// </summary>
        /// <param name="targetPosition">要释放的终点 NodeRemark。</param>
        /// <param name="connection">当前数据库连接。</param>
        /// <param name="transaction">当前数据库事务。</param>
        /// <returns>代表异步释放操作的任务。</returns>
        public async Task ReleaseAsync(
            string targetPosition,
            IDbConnection connection,
            IDbTransaction transaction)
        {
            if (string.IsNullOrWhiteSpace(targetPosition))
            {
                return;
            }

            await connection.ExecuteAsync(
                "UPDATE RCS_Locations SET [Lock] = 0 WHERE NodeRemark = @NodeRemark",
                new { NodeRemark = targetPosition },
                transaction);

            _logger.LogInformation("已释放自动预分配终点：NodeRemark={NodeRemark}", targetPosition);
        }

        private static string ResolveGroupName(string taskType)
        {
            if (string.Equals(taskType, RCS_UserTasks.TaskType.fgHandover.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return "fghandover";
            }

            if (string.Equals(taskType, RCS_UserTasks.TaskType.shipment.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return "dispatch";
            }

            return null;
        }
    }
}
