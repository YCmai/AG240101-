using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [StringLength(50)]
        public string Username { get; set; }

        [Required]
        [StringLength(100)]
        public string Password { get; set; }

        [Required]
        public UserRole Role { get; set; }

        // 允许操作的任务类型（JSON格式存储）
        public string AllowedTaskTypes { get; set; }

        public DateTime CreateTime { get; set; } = DateTime.Now;

        public DateTime? LastLoginTime { get; set; }

        public bool IsActive { get; set; } = true;
    }

    public enum UserRole
    {
        Viewer = 1,        // 等级一：仅查看
        Operator = 2,      // 等级二：特定任务类型操作
        Supervisor = 3,    // 等级三：所有任务类型操作
        Admin = 4          // 等级四：管理员
    }
} 