using System.Data;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service
{
    /// <summary>
    /// 自动终点分配服务。
    /// </summary>
    public interface IAutoDestinationAllocator
    {
        /// <summary>
        /// 判断任务类型是否需要系统自动分配终点。
        /// </summary>
        /// <param name="taskType">任务类型名称。</param>
        /// <returns>需要自动分配终点时返回 true。</returns>
        bool IsAutoDestinationTaskType(string taskType);

        /// <summary>
        /// 尝试为自动分配终点任务选择并锁定一个可用终点。
        /// </summary>
        /// <param name="taskType">任务类型名称。</param>
        /// <param name="connection">当前数据库连接。</param>
        /// <param name="transaction">当前数据库事务。</param>
        /// <returns>自动终点分配结果。</returns>
        Task<AutoDestinationAllocationResult> TryAllocateAsync(
            string taskType,
            IDbConnection connection,
            IDbTransaction transaction);

        /// <summary>
        /// 释放已经预分配但未成功生成正式任务的终点。
        /// </summary>
        /// <param name="targetPosition">要释放的终点 NodeRemark。</param>
        /// <param name="connection">当前数据库连接。</param>
        /// <param name="transaction">当前数据库事务。</param>
        /// <returns>代表异步释放操作的任务。</returns>
        Task ReleaseAsync(
            string targetPosition,
            IDbConnection connection,
            IDbTransaction transaction);
    }
}
