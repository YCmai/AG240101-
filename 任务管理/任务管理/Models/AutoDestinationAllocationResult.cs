namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 自动终点分配结果。
    /// </summary>
    public class AutoDestinationAllocationResult
    {
        /// <summary>
        /// 是否成功分配并锁定终点。
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 分配到的终点 NodeRemark。
        /// </summary>
        public string TargetPosition { get; set; }

        /// <summary>
        /// 任务类型对应的库位分组。
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// 分配结果说明，用于日志和缓存原因展示。
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 创建成功结果。
        /// </summary>
        /// <param name="targetPosition">已锁定的终点 NodeRemark。</param>
        /// <param name="groupName">任务类型对应的库位分组。</param>
        /// <param name="message">成功说明。</param>
        /// <returns>自动终点分配成功结果。</returns>
        public static AutoDestinationAllocationResult Successful(string targetPosition, string groupName, string message)
        {
            return new AutoDestinationAllocationResult
            {
                Success = true,
                TargetPosition = targetPosition,
                GroupName = groupName,
                Message = message
            };
        }

        /// <summary>
        /// 创建失败结果。
        /// </summary>
        /// <param name="groupName">任务类型对应的库位分组。</param>
        /// <param name="message">失败说明。</param>
        /// <returns>自动终点分配失败结果。</returns>
        public static AutoDestinationAllocationResult Failed(string groupName, string message)
        {
            return new AutoDestinationAllocationResult
            {
                Success = false,
                GroupName = groupName,
                Message = message
            };
        }
    }
}
