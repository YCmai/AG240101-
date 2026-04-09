using System.Threading.Tasks;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service
{
    /// <summary>
    /// RCS任务服务接口
    /// </summary>
    public interface IRcsTaskService
    {
        /// <summary>
        /// 添加任务
        /// </summary>
        /// <param name="request">任务请求参数</param>
        /// <returns>任务添加结果</returns>
        Task<TaskResponse> AddTaskAsync(AddTaskRequest request);

        /// <summary>
        /// 更新任务状态
        /// </summary>
        /// <param name="request">任务状态请求参数</param>
        /// <returns>任务状态更新结果</returns>
        Task<TaskResponse> UpdateTaskStatusAsync(UpdateTaskStatusRequest request);

        /// <summary>
        /// 取消任务
        /// </summary>
        /// <param name="request">取消任务请求参数</param>
        /// <returns>取消任务结果</returns>
        Task<CancelTaskResponse> CancelTaskAsync(CancelTaskRequest request);

        /// <summary>
        /// 获取任务清单
        /// </summary>
        /// <param name="request">获取任务清单请求参数</param>
        /// <returns>任务清单</returns>
        Task<TaskListResponse> GetTaskListAsync(GetTaskListRequest request);

        /// <summary>
        /// 按时间段查询任务
        /// </summary>
        /// <param name="request">按时间段查询任务请求参数</param>
        /// <returns>任务清单</returns>
        Task<TaskListResponse> GetTaskByTimeAsync(GetTaskByTimeRequest request);

        /// <summary>
        /// 根据任务编码查询任务
        /// </summary>
        /// <param name="toNum">任务编码</param>
        /// <returns>任务详情</returns>
        Task<TaskListResponse> GetTaskByIdAsync(string toNum);

        /// <summary>
        /// 查询AGV当前状态
        /// </summary>
        /// <param name="request">AGV状态查询请求参数</param>
        /// <returns>AGV当前状态</returns>
        Task<AgvStateResponse> GetAgvStateAsync(AgvStateRequest request);

        /// <summary>
        /// 查询AGV总状态
        /// </summary>
        /// <param name="request">AGV总状态查询请求参数</param>
        /// <returns>AGV总状态</returns>
        Task<AgvStatusResponse> GetAgvStatusAsync(AgvStatusRequest request);

        /// <summary>
        /// 查询总库位状态
        /// </summary>
        /// <returns>总库位状态</returns>
        Task<BinStatusResponse> GetBinStatusAsync();

        /// <summary>
        /// 物料交收区库位状态更新
        /// </summary>
        /// <param name="request">binId, binStatus</param>
        /// <returns>状态</returns>
        Task<object> BinUpdateAsync(BinUpdateRequest request);
        
      
    }
} 