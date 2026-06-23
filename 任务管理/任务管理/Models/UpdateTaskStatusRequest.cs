using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 任务状态更新请求参数
    /// </summary>
    public class UpdateTaskStatusRequest
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        [Required(ErrorMessage = "任务编码不能为空")]
        public string suNum { get; set; }

        /// <summary>
        /// 任务状态
        /// </summary>
        [Required(ErrorMessage = "任务状态不能为空")]
        public string status { get; set; }
    }
} 