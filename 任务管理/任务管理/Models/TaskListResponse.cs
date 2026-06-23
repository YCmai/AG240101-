using System.Collections.Generic;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 任务清单响应
    /// </summary>
    public class TaskListResponse
    {
        /// <summary>
        /// 返回状态
        /// </summary>
        public string status { get; set; }

        /// <summary>
        /// 任务数据列表
        /// </summary>
        public List<TaskListItem> data { get; set; }

        /// <summary>
        /// 结果提示
        /// </summary>
        public string message { get; set; }
    }

    /// <summary>
    /// 任务清单项
    /// </summary>
    public class TaskListItem
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        public string toNum { get; set; }

        /// <summary>
        /// 卡板单号
        /// </summary>
        public string suNum { get; set; }

        /// <summary>
        /// 物料编码
        /// </summary>
        public string material { get; set; }

        /// <summary>
        /// 物料数量
        /// </summary>
        public int? materialNum { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public string createTime { get; set; }

        /// <summary>
        /// 任务开始时间
        /// </summary>
        public string startTime { get; set; }

        /// <summary>
        /// 任务完成时间
        /// </summary>
        public string finishTime { get; set; }

        /// <summary>
        /// 起始类型
        /// </summary>
        public string sourceType { get; set; }

        /// <summary>
        /// 起始位置
        /// </summary>
        public string sourceBin { get; set; }

        /// <summary>
        /// 目标类型
        /// </summary>
        public string destType { get; set; }

        /// <summary>
        /// 目标位置
        /// </summary>
        public string destBin { get; set; }

        /// <summary>
        /// 任务类型
        /// </summary>
        public string taskType { get; set; }

        /// <summary>
        /// 信息
        /// </summary>
        public string msg { get; set; }

        /// <summary>
        /// 步骤
        /// </summary>
        public string step { get; set; }

        /// <summary>
        /// 执行任务的车
        /// </summary>
        public List<string> agvId { get; set; }
    }
} 