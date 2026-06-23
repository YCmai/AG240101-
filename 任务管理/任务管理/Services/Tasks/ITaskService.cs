using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;

namespace Services.Tasks
{
public interface ITaskService
{
    Task<(List<RCS_UserTasks> Items, int TotalItems)> GetUserTasks(
        int page = 1, 
        int pageSize = 10, 
        DateTime? filterDate = null, 
        DateTime? endDate = null,
        RCS_UserTasks.TaskType? taskType = null,
        string sourcePosition = "",
        string targetPosition = "",
        string robotCode = "",
        string sortColumn = "creatTime",
        string sortDirection = "desc");
    Task<(bool success, string message)> CancelTask(int id);
        Task<List<RCS_UserTasks>> GetTasksForExport(DateTime? startDate, DateTime? endDate, RCS_UserTasks.TaskType? taskType);
        Task<object> GetTaskStatistics(DateTime startDate, DateTime endDate, string shift);
        Task<(bool success, string message)> CreateTask(RCS_UserTasks task);
        Task<List<RCS_Locations>> GetLocations();
        
        // 缓存任务相关方法
        Task<(List<RCS_TaskCache> Items, int TotalItems)> GetCachedTasks(
            int page = 1, 
            int pageSize = 10,
            RCS_UserTasks.TaskType? taskType = null,
            string sortColumn = "CreateTime",
            string sortDirection = "desc");
        Task<(bool success, string message)> RetryCachedTask(int id);
        Task<(bool success, string message)> PrioritizeCachedTask(int id);
        Task<(bool success, string message)> CancelCachedTask(int id);
        Task<(bool success, string message)> CancelCachedTasks(List<int> ids);
        Task<(bool success, string message)> ClearAllCachedTasks();
        Task<RCS_TaskCache> GetCachedTaskById(int id);
    }
}
