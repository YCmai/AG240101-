using System.Collections.Generic;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV当前状态查询响应
    /// </summary>
    public class AgvStateResponse
    {
        /// <summary>
        /// 返回状态
        /// </summary>
        public string status { get; set; }

        /// <summary>
        /// 结果提示
        /// </summary>
        public string message { get; set; }

        /// <summary>
        /// AGV状态数据
        /// </summary>
        public AgvStateData data { get; set; }
    }

    /// <summary>
    /// AGV状态数据
    /// </summary>
    public class AgvStateData
    {
        /// <summary>
        /// 定位x坐标
        /// </summary>
        public float x { get; set; }

        /// <summary>
        /// 定位y坐标
        /// </summary>
        public float y { get; set; }

        /// <summary>
        /// 定位角度
        /// </summary>
        public float angle { get; set; }

        /// <summary>
        /// x方向速度
        /// </summary>
        public float vx { get; set; }

        /// <summary>
        /// y方向速度
        /// </summary>
        public float vy { get; set; }

        /// <summary>
        /// 角速度
        /// </summary>
        public float w { get; set; }

        /// <summary>
        /// 定位置信度
        /// </summary>
        public float confidence { get; set; }

        /// <summary>
        /// 充电
        /// </summary>
        public bool charging { get; set; }

        /// <summary>
        /// 手工充电
        /// </summary>
        public bool manualCharging { get; set; }

        /// <summary>
        /// 电量
        /// </summary>
        public float batteryLevel { get; set; }

        /// <summary>
        /// 电池温度
        /// </summary>
        public float batteryTemp { get; set; }

        /// <summary>
        /// 报警
        /// </summary>
        public Dictionary<string, string> alarms { get; set; }

        /// <summary>
        /// 顶升状态
        /// </summary>
        public bool jackState { get; set; }

        /// <summary>
        /// 顶升高度
        /// </summary>
        public float jackHeight { get; set; }

        /// <summary>
        /// 载货状态
        /// </summary>
        public bool currentShelf { get; set; }

        /// <summary>
        /// CPU使用率
        /// </summary>
        public float cpuRate { get; set; }

        /// <summary>
        /// 内存使用率
        /// </summary>
        public float memRate { get; set; }

        /// <summary>
        /// CPU温度
        /// </summary>
        public float cpuTemp { get; set; }

        /// <summary>
        /// AGVID
        /// </summary>
        public string agvId { get; set; }
    }
} 