using System;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 获取任务清单请求参数
    /// </summary>
    public class GetTaskListRequest
    {
        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime? startTime { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime? endTime { get; set; }

        /// <summary>
        /// 页码，默认为1
        /// </summary>
        public int pageIndex { get; set; } = 1;

        /// <summary>
        /// 每页记录数，默认为20
        /// </summary>
        public int pageSize { get; set; } = 20;
    }
} 