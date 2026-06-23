using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 添加任务请求参数
    /// </summary>
    public class AddTaskRequest
    {
        /// <summary>
        /// 任务编码
        /// </summary>
        [Required(ErrorMessage = "任务编码不能为空")]
        public string toNum { get; set; }

        /// <summary>
        /// 卡板单号
        /// </summary>
        [Required(ErrorMessage = "卡板单号不能为空")]
        public string suNum { get; set; }

        /// <summary>
        /// 物料编码
        /// </summary>
        [Required(ErrorMessage = "物料编码不能为空")]
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
        /// 确认时间
        /// </summary>
        public string confirmTime { get; set; }

        /// <summary>
        /// 起始类型
        /// </summary>
        [Required(ErrorMessage = "起始类型不能为空")]
        public string sourceType { get; set; }

        /// <summary>
        /// 起始位置
        /// </summary>
        [Required(ErrorMessage = "起始位置不能为空")]
        public string sourceBin { get; set; }

        /// <summary>
        /// 目标类型
        /// </summary>
        [Required(ErrorMessage = "目标类型不能为空")]
        public string destType { get; set; }

        /// <summary>
        /// 任务类型
        /// </summary>
        [Required(ErrorMessage = "任务类型不能为空")]
        public string taskType { get; set; }

        /// <summary>
        /// 目标位置
        /// </summary>
        [Required(ErrorMessage = "目标位置不能为空")]
        public string destBin { get; set; }

        /// <summary>
        /// 出入库标识：0-出库，1-入库
        /// </summary>
        public int? TaskIdentification { get; set; }

        /// <summary>
        /// 货架标识，如 D1
        /// </summary>
        public string? ShelvesIdentification { get; set; }

        /// <summary>
        /// 终点是否已由缓存处理器预分配并锁定。
        /// </summary>
        public bool IsDestinationPreAllocated { get; set; }
    }
} 
