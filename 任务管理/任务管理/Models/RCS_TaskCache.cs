using System;
using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 任务缓存模型
    /// 用于存储因点位被锁定而无法立即执行的任务
    /// </summary>
    public class RCS_TaskCache
    {
        public int Id { get; set; }

        /// <summary>
        /// 任务类型
        /// </summary>
        public RCS_UserTasks.TaskType TaskType { get; set; }

        /// <summary>
        /// 起点
        /// </summary>
        public string SourcePosition { get; set; }

        /// <summary>
        /// 终点
        /// </summary>
        public string TargetPosition { get; set; }

        /// <summary>
        /// 物料编码
        /// </summary>
        public string MaterialCode { get; set; }

        /// <summary>
        /// 物料数量
        /// </summary>
        public int MaterialQuantity { get; set; }

        /// <summary>
        /// 卡板单号
        /// </summary>
        public string PalletNumber { get; set; }

        /// <summary>
        /// 起始类型
        /// </summary>
        public string SourceType { get; set; }

        /// <summary>
        /// 目标类型
        /// </summary>
        public string DestType { get; set; }

        /// <summary>
        /// 优先级
        /// </summary>
        public int Priority { get; set; }

        /// <summary>
        /// 请求编号
        /// </summary>
        public string RequestCode { get; set; }

        /// <summary>
        /// 出入库标识：0-出库，1-入库
        /// </summary>
        public int? TaskIdentification { get; set; }

        /// <summary>
        /// 货架标识，如 D1
        /// </summary>
        public string ShelvesIdentification { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreateTime { get; set; }

        /// <summary>
        /// 确认时间
        /// </summary>
        public DateTime? ConfirmTime { get; set; }

        /// <summary>
        /// 重试次数
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// 最后错误信息
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// 状态：0-待处理，1-处理中，2-已完成，3-已取消
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 任务分组ID（用于拆分任务）
        /// </summary>
        public string TaskGroupId { get; set; }

        /// <summary>
        /// 任务序号
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
        /// 获取任务类型显示名称
        /// </summary>
        public string TaskTypeDisplayName
        {
            get
            {
                var tempTask = new RCS_UserTasks { taskType = TaskType };
                return tempTask.TaskTypeDisplayName;
            }
        }

        /// <summary>
        /// 获取任务状态显示名称
        /// </summary>
        public string StatusDisplayName
        {
            get
            {
                return Status switch
                {
                    0 => "待处理",
                    1 => "处理中",
                    2 => "已完成",
                    3 => "已取消",
                    _ => "未知"
                };
            }
        }
    }
}
