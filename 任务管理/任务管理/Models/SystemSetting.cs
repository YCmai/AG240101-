using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 系统设置模型
    /// </summary>
    public class SystemSetting
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        public int Id { get; set; }

        /// <summary>
        /// 设置键名
        /// </summary>
        [Required]
        [StringLength(100)]
        public string SettingKey { get; set; }

        /// <summary>
        /// 设置值
        /// </summary>
        [Required]
        [StringLength(500)]
        public string SettingValue { get; set; }

        /// <summary>
        /// 设置描述
        /// </summary>
        [StringLength(200)]
        public string? Description { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}












