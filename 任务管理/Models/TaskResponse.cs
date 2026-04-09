using System;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 任务响应参数
    /// </summary>
    public class TaskResponse
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        public string suNum { get; set; }

        /// <summary>
        /// 调用时间
        /// </summary>
        public string callTime { get; set; }

        /// <summary>
        /// 返回状态
        /// </summary>
        public string status { get; set; }

        /// <summary>
        /// 失败类型
        /// </summary>
        public int? failType { get; set; }

        /// <summary>
        /// 结果提示
        /// </summary>
        public string message { get; set; }
    }


    public class CancelTaskResponse
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        //public string toNum { get; set; }

        ///// <summary>
        ///// 调用时间
        ///// </summary>
        //public string callTime { get; set; }

        /// <summary>
        /// 返回状态
        /// </summary>
        public string status { get; set; }

        /// <summary>
        /// 结果提示
        /// </summary>
        public string message { get; set; }
    }
}