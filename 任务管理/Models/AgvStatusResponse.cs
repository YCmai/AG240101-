namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV总状态查询响应
    /// </summary>
    public class AgvStatusResponse
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
        public AgvStatusData data { get; set; }
    }

    /// <summary>
    /// AGV总状态数据
    /// </summary>
    public class AgvStatusData
    {
        /// <summary>
        /// 运行总时间
        /// </summary>
        public double time { get; set; }

        /// <summary>
        /// 运行总里程
        /// </summary>
        public double odo { get; set; }

        /// <summary>
        /// 充电次数
        /// </summary>
        public int batteryChargeCount { get; set; }

        /// <summary>
        /// 电池循环次数
        /// </summary>
        public float batteryCircleCount { get; set; }

        /// <summary>
        /// AGVID
        /// </summary>
        public string agvId { get; set; }
    }
} 