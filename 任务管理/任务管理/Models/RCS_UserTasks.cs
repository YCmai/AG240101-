using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace WarehouseManagementSystem.Models
{
    public class RCS_UserTasks
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
        public string? MaterialCode{ get; set; }

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
        /// 原始任务ID（如果是拆分任务，记录原始任务ID）
        /// </summary>
        public int? OriginalTaskId { get; set; }

        /// <summary>
        /// 异常时间阈值（分钟）
        /// </summary>
        public static int AbnormalTimeThreshold { get; set; } = 30;

        /// <summary>
        /// 托盘类型
        /// </summary>
        public string PalletType { get; set; }

        /// <summary>
        /// 出入库标识：0-出库，1-入库
        /// </summary>
        public int? TaskIdentification { get; set; }

        /// <summary>
        /// 货架标识，如 D1
        /// </summary>
        public string ShelvesIdentification { get; set; }

        /// <summary>
        /// 检查任务是否异常
        /// </summary>
        public bool CheckIsAbnormal()
        {
            if (taskStatus != TaskStatuEnum.None && taskStatus < TaskStatuEnum.TaskFinish)
            {
                if (creatTime.HasValue)
                {
                    var timeSpan = DateTime.Now - creatTime.Value;
                    return timeSpan.TotalMinutes > AbnormalTimeThreshold;
                }
            }
            return false;
        }

        /// <summary>
        /// 获取任务优先级
        /// </summary>
        public int GetTaskPriority()
        {
            if (IsAbnormal) return 3; // 异常任务最高优先级
            if (taskStatus == TaskStatuEnum.TaskStart ||
                taskStatus == TaskStatuEnum.Confirm ||
                taskStatus == TaskStatuEnum.ConfirmCar ||
                taskStatus == TaskStatuEnum.PickingUp ||
                taskStatus == TaskStatuEnum.Unloading)
                return 2; // 执行中任务次优先级
            return 1; // 其他任务最低优先级
        }

        public string TaskStatusDisplayName
        {
            get
            {
                var fieldInfo = taskStatus.GetType().GetField(taskStatus.ToString());
                var attribute = (DisplayAttribute)fieldInfo.GetCustomAttribute(typeof(DisplayAttribute));
                return attribute != null ? attribute.Name : taskStatus.ToString();
            }
        }

        public string TaskTypeDisplayName
        {
            get
            {
                var fieldInfo = typeof(TaskType).GetField(taskType.ToString());
                if (fieldInfo != null)
                {
                    var attribute = (DisplayAttribute)fieldInfo.GetCustomAttribute(typeof(DisplayAttribute));
                    return attribute?.Name ?? taskType.ToString();
                }
                return taskType.ToString();
            }
        }

        /// <summary>
        /// 任务类型枚举
        /// </summary>
        public enum TaskType
        {
            /// <summary>
            /// 人工任务
            /// </summary>
            [Display(Name = "人工任务")]
            manualTask = 0,

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
            /// 出货
            /// </summary>
            [Display(Name = "出货")]
            shipment = 4,

            /// <summary>
            /// 成品交收
            /// </summary>
            [Display(Name = "物料交收")]
            fgHandover = 9,

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

    public enum TaskStatuEnum
    {
        /// <summary>
        /// 未执行
        /// </summary>
        None = -1,
        /// <summary>
        /// 不存在的任务号发生的洗车,当前可能是人工叉货并且把当前agv接入系统的情况下发生
        /// </summary>
        CarWash = 0,
        /// <summary>
        /// 任务开始
        /// </summary>
        TaskStart = 1,
        /// <summary>
        /// 进行参数反馈确认
        /// </summary>
        Confirm = 2,
        /// <summary>
        /// 确认执行agv
        /// </summary>
        ConfirmCar = 3,
        /// <summary>
        /// 取货中
        /// </summary>
        PickingUp = 4,
        /// <summary>
        /// 取货完成
        /// </summary>
        PickDown = 6,
        /// <summary>
        /// 卸货中
        /// </summary>
        Unloading = 8,
        /// <summary>
        /// 卸货完成
        /// </summary>
        UnloadDown = 10,
        /// <summary>
        /// 正常任务结束
        /// </summary>
        TaskFinish = 11,
        /// <summary>
        /// 任务被人为主动取消,当前车还没有取到货，任务直接取消
        /// </summary>
        Canceled = 30,
        /// <summary>
        /// 任务被人为主动取消，但当前agv已载货，触发一个洗车任务
        /// </summary>
        CanceledWashing = 31,
        /// <summary>
        /// 被人为触发的洗车任务AGV已执行完成
        /// </summary>
        CanceledWashFinish = 32,
        /// <summary>
        /// 到达取货口时，agv发现取货路线异常，取货失败--agv主动发起取消，当前还没有取到货，任务直接取消
        /// </summary>
        RedirectRequest = 33,
        /// <summary>
        /// 任务中有无效取货点 --系统主动发起取消当前任务，还没派车
        /// </summary>
        InvalidUp = 49,
        /// <summary>
        /// 任务中有无效卸货点无效  --系统主动发起取消当前任务，还没派车
        /// </summary>
        InvalidDown = 50,
        /// <summary>
        /// 到达卸货口时，agv发现卸货路线异常，卸货失败，主动请求洗车，转卸到别的点 --avg主动发起取消
        /// </summary>
        OrderAgv = 52,
        /// <summary>
        /// 卸货口路线异常任务执行结束
        /// </summary>
        OrderAgvFinish = 53
    }
}


