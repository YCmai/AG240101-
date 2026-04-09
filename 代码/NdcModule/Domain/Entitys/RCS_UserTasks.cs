using AciModule.Domain.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Volo.Abp.Domain.Entities;

namespace AciModule.Domain.Entitys
{
    public class RCS_UserTasks:Entity
    {
        public int ID { get; set; }

        /// <summary>
        /// 任务状态
        /// </summary>
        public TaskStatuEnum taskStatus { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime? executedTime { get; set; }

        /// <summary>
        /// 设备管理器任务ID
        /// </summary>
        public string? runTaskId { get; set; }

        /// <summary>
        /// 开始时间
        /// </summary>
        public DateTime? startTime { get; set; }

        /// <summary>
        /// 是否要执行
        /// </summary>
        public bool executed { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime? creatTime { get; set; }



        /// <summary>
        /// 结束时间
        /// </summary>
        public DateTime? endTime { get; set; }

        /// <summary>
        /// 请求编号
        /// </summary>
        public string requestCode { get; set; }

        /// <summary>
        /// 任务类型
        /// </summary>
        public TaskType taskType { get; set; }

        /// <summary>
        /// 优先级
        /// </summary>
        public int priority { get; set; }

        /// <summary>
        /// 执行任务的agv
        /// </summary>
        public string? robotCode { get; set; } = "0";

        /// <summary>
        /// 起点
        /// </summary>
        public string? sourcePosition { get; set; }

        /// <summary>
        /// 目标点
        /// </summary>
        public string? targetPosition { get; set; }

        /// <summary>
        /// 是否取消
        /// </summary>
        public bool IsCancelled { get; set; }

        /// <summary>
        /// 物料编码
        /// </summary>
        public string? MaterialCode { get; set; }

        /// <summary>
        /// 物料数量
        /// </summary>
        public int MaterialQuantity { get; set; }

        /// <summary>
        /// 卡板单号
        /// </summary>
        public string? PalletNumber { get; set; }

        /// <summary>
        /// 起始类型
        /// </summary>
        public string? SourceType { get; set; }

        /// <summary>
        /// 目标类型
        /// </summary>
        public string? DestType { get; set; }

        /// <summary>
        /// 确认时间
        /// </summary>
        public DateTime? ConfirmTime { get; set; }

        /// <summary>
        /// 任务是否异常
        /// </summary>
        public bool IsAbnormal { get; set; }

        /// <summary>
        /// 异常时间阈值（分钟）
        /// </summary>
        public static int AbnormalTimeThreshold { get; set; } = 30;

        /// <summary>
        /// 任务分组ID（用于拆分任务管理）
        /// </summary>
        public string? TaskGroupId { get; set; }

        /// <summary>
        /// 任务序号（在分组中的顺序，1表示第一个任务，2表示第二个任务）
        /// </summary>
        public int TaskSequence { get; set; } = 1;

        /// <summary>
        /// 是否为拆分任务
        /// </summary>
        public bool IsSplitTask { get; set; } = false;


        /// <summary>
        /// 托盘类型
        /// </summary>
        public string? PalletType { get; set; }


        /// <summary>
        /// 原始任务ID（如果是拆分任务，记录原始任务ID）
        /// </summary>
        public int? OriginalTaskId { get; set; }

        public override object[] GetKeys()
        {
            return new object[] { ID };
        }

    }


    /// <summary>
    /// 任务类型枚举
    /// </summary>
    public enum TaskType
    {
        /// <summary>
        /// 收货
        /// </summary>
        [Display(Name = "收货")]
        receiving = 1,

        /// <summary>
        /// 出货外发
        /// </summary>
        [Display(Name = "出货外发")]
        shipmentToOther = 2,

        /// <summary>
        /// 物料交收
        /// </summary>
        [Display(Name = "物料交收")]
        rmHandover = 3,

        /// <summary>
        /// 成品出货
        /// </summary>
        [Display(Name = "成品出货")]
        shipment = 4,

        /// <summary>
        /// 入库
        /// </summary>
        [Display(Name = "入库")]
        stockIn = 5,

        /// <summary>
        /// 库位转移
        /// </summary>
        [Display(Name = "库位转移")]
        binToBin = 6,

        /// <summary>
        /// 废料接货
        /// </summary>
        [Display(Name = "废料接货")]
        scrap = 7,

        /// <summary>
        /// EPMRF接货
        /// </summary>
        [Display(Name = "EPMRF接货")]
        EPMRF = 8
    }

}
