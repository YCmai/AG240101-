using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV报警信息实体
    /// </summary>
    [Table("AGV_Alarm")]
    public class AgvAlarm
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// AGV编号
        /// </summary>
        [Required]
        [StringLength(50)]
        public string AgvId { get; set; }

        /// <summary>
        /// 报警编码
        /// </summary>
        [Required]
        [StringLength(50)]
        public string AlarmCode { get; set; }

        /// <summary>
        /// 报警内容
        /// </summary>
        [StringLength(500)]
        public string AlarmContent { get; set; }

        /// <summary>
        /// 报警级别 (1:提示, 2:警告, 3:错误, 4:严重错误)
        /// </summary>
        public int AlarmLevel { get; set; }

        /// <summary>
        /// 报警时间
        /// </summary>
        public DateTime AlarmTime { get; set; }

        /// <summary>
        /// 处理状态 (0:未处理, 1:已处理)
        /// </summary>
        public int ProcessStatus { get; set; }

        /// <summary>
        /// 处理时间
        /// </summary>
        public DateTime? ProcessTime { get; set; }

        /// <summary>
        /// 处理人
        /// </summary>
        [StringLength(50)]
        public string Processor { get; set; }

        /// <summary>
        /// 处理备注
        /// </summary>
        [StringLength(500)]
        public string ProcessRemarks { get; set; }
    }
} 