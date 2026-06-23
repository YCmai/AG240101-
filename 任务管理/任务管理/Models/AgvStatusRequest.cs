using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV总状态查询请求参数
    /// </summary>
    public class AgvStatusRequest
    {
        /// <summary>
        /// AGV编号
        /// </summary>
        [Required(ErrorMessage = "AGV编号不能为空")]
        public string agvId { get; set; }
    }
} 