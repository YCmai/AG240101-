using System.Collections.Generic;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 任务列表视图模型
    /// 包含普通任务和缓存任务
    /// </summary>
    public class TaskListViewModel
    {
        /// <summary>
        /// 普通任务分页结果
        /// </summary>
        public PagedResult<RCS_UserTasks> UserTasks { get; set; } = new PagedResult<RCS_UserTasks>();

        /// <summary>
        /// 缓存任务分页结果
        /// </summary>
        public PagedResult<RCS_TaskCache> CachedTasks { get; set; } = new PagedResult<RCS_TaskCache>();
    }
}
