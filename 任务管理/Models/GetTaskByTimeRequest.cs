using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 按时间段查询任务请求参数
    /// </summary>
    public class GetTaskByTimeRequest
    {
        /// <summary>
        /// 开始时间
        /// </summary>
        [Required(ErrorMessage = "开始时间不能为空")]
        public string createTimeStart { get; set; }

        /// <summary>
        /// 结束时间
        /// </summary>
        [Required(ErrorMessage = "结束时间不能为空")]
        public string createTimeEnd { get; set; }
    }
} 