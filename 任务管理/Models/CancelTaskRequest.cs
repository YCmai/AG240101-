using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 取消任务请求参数
    /// </summary>
    public class CancelTaskRequest
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        //[Required(ErrorMessage = "任务编码不能为空")]
        //public string toNum { get; set; }

        /// <summary>
        /// 托盘编号（用于查找相关任务）
        /// </summary>
        [Required(ErrorMessage = "托盘编号不能为空")]
        public string suNum { get; set; }
    }
}