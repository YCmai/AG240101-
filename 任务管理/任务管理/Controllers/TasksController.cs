﻿using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Newtonsoft.Json;

using OfficeOpenXml;

using WarehouseManagementSystem.Models;
using Microsoft.Extensions.Logging;
using Services.Tasks;
using static WarehouseManagementSystem.Models.RCS_UserTasks;
using WarehouseManagementSystem.Attributes;
using WarehouseManagementSystem.Service;

namespace WarehouseManagementSystem.Controllers
{
    [Authorize] // 要求用户必须登录
    public class TasksController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ConnectionStatusService _connectionStatusService;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ITaskService _taskService;
        private readonly IRcsTaskService _rcsTaskService;
        private readonly ILogger<TasksController> _logger;

        public TasksController(ApplicationDbContext context, ConnectionStatusService connectionStatusService, IConfiguration configuration, ITaskService taskService, IRcsTaskService rcsTaskService, ILogger<TasksController> logger)
        {
            _context = context;
            _connectionStatusService = connectionStatusService;
            _httpClient = new HttpClient();
            _configuration = configuration;
            _taskService = taskService;
            _rcsTaskService = rcsTaskService;
            _logger = logger;
        }

        private (string baseUrl, string port, string http) GetConnectionParameters()
        {
            var baseUrl = _configuration["ConnectionStrings:IPAddress"];
            var port = _configuration["ConnectionStrings:Port"];
            var http = _configuration["ConnectionStrings:Http"];
            return (baseUrl, port, http);
        }

        private const int TimeOffset = 8;

        // [TimeZoneFix] 统一使用 UTC+8，避免受海外服务器本地时区影响。
        private static DateTime GetChinaNow()
        {
            return DateTime.UtcNow.AddHours(TimeOffset);
        }

        // [TimeZoneFix] 如果时间本身是 UTC，就补上 8 小时转成中国时间；
        // 如果本身就是未带时区的本地值，则按当前值直接输出，避免重复偏移。
        private static DateTime? NormalizeToChinaTime(DateTime? dateTime)
        {
            if (!dateTime.HasValue)
            {
                return null;
            }

            return dateTime.Value.Kind == DateTimeKind.Utc
                ? dateTime.Value.AddHours(TimeOffset)
                : dateTime.Value;
        }

        // [TimeZoneFix] 这里传的是中国本地时间，所以不要再拼接 Z。
        private static string FormatChinaTime(DateTime? dateTime)
        {
            return NormalizeToChinaTime(dateTime)?.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        // [DispatchStrategy] 人工页面建任务时没有上位机传入这两个字段，
        // 这里按现场约定自动推导：
        // 1. 取起点/终点前两位，能转成数字的视为货架号
        // 2. 起点前两位能转数字：判定为出库（0），货架取起点
        // 3. 否则判定为入库（1），货架取终点
        private static (int? taskIdentification, string shelvesIdentification) BuildManualDispatchFields(string sourcePosition, string targetPosition)
        {
            var sourceShelf = GetShelfPrefix(sourcePosition);
            var targetShelf = GetShelfPrefix(targetPosition);

            if (sourceShelf != null)
            {
                return (0, sourceShelf);
            }

            return (1, targetShelf);
        }

        // [DispatchStrategy] 只取点位前两位；前两位可转数字时，视为货架号。
        private static string GetShelfPrefix(string position)
        {
            if (string.IsNullOrWhiteSpace(position) || position.Length < 2)
            {
                return null;
            }

            var prefix = position.Substring(0, 2);
            return int.TryParse(prefix, out _) ? prefix : null;
        }

        // 所有用户都可以查看任务列表
        public async Task<IActionResult> Index(int page = 1, string dropLocation = "",
            DateTime? filterDate = null, DateTime? endDate = null, string palletId = "",
            TaskType? taskType = null, string sourcePosition = "", string targetPosition = "", string robotCode = "",
            string sortColumn = "creatTime", string sortDirection = "desc",
            int pageSize = 10, string taskFilter = "user")
        {
            try
            {
                // 保存筛选条件到 ViewData
                ViewData["dropLocation"] = dropLocation;
                ViewData["filterDate"] = filterDate?.ToString("yyyy-MM-dd");
                ViewData["endDate"] = endDate?.ToString("yyyy-MM-dd");
                ViewData["palletId"] = palletId;
                ViewData["taskType"] = taskType?.ToString();
                ViewData["sourcePosition"] = sourcePosition;
                ViewData["targetPosition"] = targetPosition;
                ViewData["robotCode"] = robotCode;
                ViewData["sortColumn"] = sortColumn;
                ViewData["sortDirection"] = sortDirection;
                ViewData["taskFilter"] = taskFilter;

                TaskListViewModel viewModel;

                if (taskFilter == "cache")
                {
                    // 只获取缓存任务
                    var (cachedItems, cachedTotalItems) = await _taskService.GetCachedTasks(
                        page,
                        pageSize,
                        taskType,
                        sortColumn,
                        sortDirection);
                    
                    viewModel = new TaskListViewModel
                    {
                        UserTasks = new PagedResult<RCS_UserTasks>
                        {
                            Items = new List<RCS_UserTasks>(),
                            TotalItems = 0,
                            PageNumber = 1,
                            PageSize = pageSize,
                            TotalPages = 0
                        },
                        CachedTasks = new PagedResult<RCS_TaskCache>
                        {
                            Items = cachedItems,
                            TotalItems = cachedTotalItems,
                            PageNumber = page,
                            PageSize = pageSize,
                            TotalPages = (int)Math.Ceiling((double)cachedTotalItems / pageSize)
                        }
                    };
                }
                else
                {
                    // 默认获取普通任务
                    var (items, totalItems) = await _taskService.GetUserTasks(
                        page,
                        pageSize,
                        filterDate,
                        endDate,
                        taskType,
                        sourcePosition,
                        targetPosition,
                        robotCode,
                        sortColumn,
                        sortDirection);
                    
                    viewModel = new TaskListViewModel
                    {
                        UserTasks = new PagedResult<RCS_UserTasks>
                        {
                            Items = items,
                            TotalItems = totalItems,
                            PageNumber = page,
                            PageSize = pageSize,
                            TotalPages = (int)Math.Ceiling((double)totalItems / pageSize)
                        },
                        CachedTasks = new PagedResult<RCS_TaskCache>
                        {
                            Items = new List<RCS_TaskCache>(),
                            TotalItems = 0,
                            PageNumber = 1,
                            PageSize = pageSize,
                            TotalPages = 0
                        }
                    };
                }

                return View(viewModel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务列表失败");
                var viewModel = new TaskListViewModel
                {
                    UserTasks = new PagedResult<RCS_UserTasks>
                    {
                        Items = new List<RCS_UserTasks>(),
                        TotalItems = 0,
                        PageNumber = page,
                        PageSize = pageSize,
                        TotalPages = 0
                    },
                    CachedTasks = new PagedResult<RCS_TaskCache>
                    {
                        Items = new List<RCS_TaskCache>(),
                        TotalItems = 0,
                        PageNumber = page,
                        PageSize = pageSize,
                        TotalPages = 0
                    }
                };
                return View(viewModel);
            }
        }

        // 只有操作员及以上角色可以创建任务
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> Create()
        {
            try
            {
                var locations = await _taskService.GetLocations();
                ViewBag.Locations = locations;
                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取位置列表失败");
                return View();
            }
        }

        // POST: TasksController/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(RCS_UserTasks task)
        {
            try
            {
                var locations = await _taskService.GetLocations();
                ViewBag.Locations = locations;

                if (!ModelState.IsValid)
                {
                    return View(task);
                }

                // 验证起始点和终点是否有效
                var sourceLocation = locations.FirstOrDefault(l => l.Name == task.sourcePosition);
                var targetLocation = locations.FirstOrDefault(l => l.Name == task.targetPosition);

                if (sourceLocation == null)
                {
                    ModelState.AddModelError("sourcePosition", "起始点无效");
                    return View(task);
                }

                if (targetLocation == null)
                {
                    ModelState.AddModelError("targetPosition", "终点无效");
                    return View(task);
                }

                // 设置任务默认值
                task.creatTime = GetChinaNow();
                task.ConfirmTime = GetChinaNow();
                // 如果没有设置任务状态，则默认为None
                if (task.taskStatus == 0)
                {
                    task.taskStatus = TaskStatuEnum.None;
                }
                task.executed = false;
                task.IsCancelled = false;
                task.priority = task.priority;

                // 生成请求编号（如果没有提供）
                if (string.IsNullOrEmpty(task.requestCode))
                {
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    task.requestCode = timestamp.ToString();
                }

                if (string.IsNullOrEmpty(task.PalletNumber))
                {
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    task.PalletNumber = timestamp.ToString();
                }

                var manualDispatchFields = BuildManualDispatchFields(task.sourcePosition, task.targetPosition);

                _logger.LogInformation(
                    "人工建任务自动推导调度字段：起点={SourcePosition}，终点={TargetPosition}，任务出入库标识={TaskIdentification}，货架标识={ShelvesIdentification}",
                    task.sourcePosition,
                    task.targetPosition,
                    manualDispatchFields.taskIdentification,
                    manualDispatchFields.shelvesIdentification);

                // 使用 RcsTaskService 的 AddTaskAsync 方法，这样可以复用拆分逻辑
                var addTaskRequest = new AddTaskRequest
                {
                    toNum = task.requestCode ?? "",
                    taskType = task.taskType.ToString(),
                    sourceBin = task.sourcePosition ?? "",
                    destBin = task.targetPosition ?? "",
                    material = task.MaterialCode ?? "",
                    materialNum = task.MaterialQuantity,
                    suNum = task.PalletNumber ?? "",
                    sourceType = task.SourceType ?? "",
                    destType = task.DestType ?? "",
                    createTime = FormatChinaTime(task.creatTime),
                    confirmTime = FormatChinaTime(task.ConfirmTime),
                    TaskIdentification = manualDispatchFields.taskIdentification,
                    ShelvesIdentification = manualDispatchFields.shelvesIdentification
                };

                var response = await _rcsTaskService.AddTaskAsync(addTaskRequest);

                if (response.status == "Success")
                {
                    // 检查是否是因为任务被缓存而返回成功
                    if (response.message.Contains("已缓存") || response.message.Contains("等待点位"))
                    {
                        _logger.LogInformation($"任务被缓存: {response.message}");
                        TempData["SuccessMessage"] = "任务已缓存，等待点位释放后自动处理";
                    }
                    else
                    {
                        _logger.LogInformation($"任务创建成功: {response.message}");
                        TempData["SuccessMessage"] = "任务创建成功";
                    }
                    return RedirectToAction(nameof(Index));
                }
                else
                {
                    _logger.LogWarning($"任务创建失败: {response.message}");
                    ModelState.AddModelError("", response.message);
                    return View(task);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建任务失败");
                ModelState.AddModelError("", "创建任务失败");
                return View(task);
            }
        }

        // GET: TasksController/Edit/5
        public ActionResult Edit(int id)
        {
            return View();
        }

        // POST: TasksController/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit(int id, IFormCollection collection)
        {
            try
            {
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                return View();
            }
        }

        // GET: TasksController/Delete/5
        public ActionResult Delete(int id)
        {
            return View();
        }

        // POST: TasksController/Delete/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Delete(int id, IFormCollection collection)
        {
            try
            {
                return RedirectToAction(nameof(Index));
            }
            catch
            {
                return View();
            }
        }

        // 只有操作员及以上角色可以取消任务
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        [HttpPost]
        public async Task<IActionResult> Cancel(int id)
        {
            try
            {
                _logger.LogInformation($"接收到取消任务请求，任务ID: {id}");
                var (success, message) = await _taskService.CancelTask(id);
                _logger.LogInformation($"取消任务结果: 成功={success}, 消息={message}");
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"取消任务异常，任务ID: {id}");
                return Json(new { success = false, message = $"取消任务发生错误: {ex.Message}" });
            }
        }

        // 只有操作员及以上角色可以导出任务
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public IActionResult Export()
        {
            return View();
        }

        // 只有操作员及以上角色可以查看任务统计
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public IActionResult Statistics()
        {
            return View();
        }

        // 只有操作员及以上角色可以查看缓存任务
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> CachedTasks(int page = 1, int pageSize = 10)
        {
            try
            {
                var (items, totalItems) = await _taskService.GetCachedTasks(page, pageSize);

                return View(new PagedResult<RCS_TaskCache>
                {
                    Items = items,
                    TotalItems = totalItems,
                    PageNumber = page,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling((double)totalItems / pageSize)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取缓存任务列表失败");
                return View(new PagedResult<RCS_TaskCache>
                {
                    Items = new List<RCS_TaskCache>(),
                    TotalItems = 0,
                    PageNumber = page,
                    PageSize = pageSize,
                    TotalPages = 0
                });
            }
        }

        // 重试缓存任务
        [HttpPost]
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> RetryCachedTask(int id)
        {
            try
            {
                var (success, message) = await _taskService.RetryCachedTask(id);
               
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"重试缓存任务异常，任务ID: {id}");
                return Json(new { success = false, message = $"重试任务发生错误: {ex.Message}" });
            }
        }

        // 设置缓存任务为优先执行
        [HttpPost]
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> PrioritizeCachedTask(int id)
        {
            try
            {
                var (success, message) = await _taskService.PrioritizeCachedTask(id);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"设置缓存任务优先级异常，任务ID: {id}");
                return Json(new { success = false, message = $"设置优先级发生错误: {ex.Message}" });
            }
        }

        // 取消缓存任务
        [HttpPost]
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> CancelCachedTask(int id)
        {
            try
            {
                var (success, message) = await _taskService.CancelCachedTask(id);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"取消缓存任务异常，任务ID: {id}");
                return Json(new { success = false, message = $"取消任务发生错误: {ex.Message}" });
            }
        }

        // 批量取消缓存任务
        [HttpPost]
        [Authorize(new[] { UserRole.Operator, UserRole.Supervisor, UserRole.Admin })]
        public async Task<IActionResult> BatchCancelCachedTasks([FromBody] List<int> ids)
        {
            try
            {
                var currentUser = HttpContext.Items["User"] as User;
                _logger.LogInformation(
                    "接收到批量取消缓存任务请求，操作人={Username}，角色={Role}，任务IDs={TaskIds}",
                    currentUser?.Username ?? "Unknown",
                    currentUser?.Role.ToString() ?? "Unknown",
                    ids == null ? "null" : string.Join(",", ids));

                var (success, message) = await _taskService.CancelCachedTasks(ids);
                _logger.LogInformation(
                    "批量取消缓存任务结果，操作人={Username}，成功={Success}，消息={Message}",
                    currentUser?.Username ?? "Unknown",
                    success,
                    message);

                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量取消缓存任务异常");
                return Json(new { success = false, message = $"批量取消任务发生错误: {ex.Message}" });
            }
        }

        // 清空所有缓存任务
        [HttpPost]
        [Authorize(new[] { UserRole.Admin })]
        public async Task<IActionResult> ClearAllCachedTasks()
        {
            try
            {
                var (success, message) = await _taskService.ClearAllCachedTasks();
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空所有缓存任务异常");
                return Json(new { success = false, message = $"清空缓存任务发生错误: {ex.Message}" });
            }
        }

        // 只有管理员可以设置异常时间
        [Authorize(new[] { UserRole.Admin })]
        public IActionResult SetAbnormalTime()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> SetAbnormalTimeThreshold(int minutes)
        {
            try
            {
                RCS_UserTasks.AbnormalTimeThreshold = minutes;
                return Json(new { success = true, message = "设置成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置异常时间阈值失败");
                return Json(new { success = false, message = "设置失败" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateTask([FromBody] RCS_UserTasks task)
        {
            try
            {
                if (task == null)
                {
                    _logger.LogWarning("接收到的任务数据为空");
                    return Json(new { success = false, message = "任务数据不能为空" });
                }

                if (string.IsNullOrEmpty(task.requestCode))
                {
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string timestampStr = timestamp.ToString();
                    task.requestCode = timestampStr;
                }

                // 记录接收到的数据
                _logger.LogInformation($"接收到的任务数据: TaskType={task.taskType}, Source={task.sourcePosition}, Target={task.targetPosition}, Priority={task.priority}, RequestCode={task.requestCode}");

                var manualDispatchFields = BuildManualDispatchFields(task.sourcePosition, task.targetPosition);

                _logger.LogInformation(
                    "页面接口建任务自动推导调度字段：起点={SourcePosition}，终点={TargetPosition}，任务出入库标识={TaskIdentification}，货架标识={ShelvesIdentification}",
                    task.sourcePosition,
                    task.targetPosition,
                    manualDispatchFields.taskIdentification,
                    manualDispatchFields.shelvesIdentification);

                // 使用 RcsTaskService 的 AddTaskAsync 方法，这样可以复用拆分逻辑
                var addTaskRequest = new AddTaskRequest
                {
                    toNum = task.requestCode ?? "",
                    taskType = task.taskType.ToString(),
                    sourceBin = task.sourcePosition ?? "",
                    destBin = task.targetPosition ?? "",
                    material = task.MaterialCode ?? "",
                    materialNum = task.MaterialQuantity,
                    suNum = task.PalletNumber ?? "",
                    sourceType = task.SourceType ?? "",
                    destType = task.DestType ?? "",
                    createTime = FormatChinaTime(task.creatTime),
                    confirmTime = FormatChinaTime(task.ConfirmTime),
                    TaskIdentification = manualDispatchFields.taskIdentification,
                    ShelvesIdentification = manualDispatchFields.shelvesIdentification
                };

                var response = await _rcsTaskService.AddTaskAsync(addTaskRequest);
                if (response.status != "Success" && string.IsNullOrEmpty(response.message))
                {
                    return Json(new { success = response.status == "Success", message = "任务下发到缓存任务中" });
                }
                return Json(new { success = response.status == "Success", message = response.message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建任务失败");
                return Json(new { success = false, message = "创建任务失败：" + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ExportTasks(DateTime? startDate, DateTime? endDate, TaskType? taskType)
        {
            try
            {
                // 设置EPPlus许可证
                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                var tasks = await _taskService.GetTasksForExport(startDate, endDate, taskType);

                using (var package = new ExcelPackage())
                {
                    var worksheet = package.Workbook.Worksheets.Add("任务列表");

                    // 添加表头
                    worksheet.Cells[1, 1].Value = "任务类型";
                    worksheet.Cells[1, 2].Value = "起始点";
                    worksheet.Cells[1, 3].Value = "终点";
                    worksheet.Cells[1, 4].Value = "任务状态";
                    worksheet.Cells[1, 5].Value = "AGV编号";
                    worksheet.Cells[1, 6].Value = "任务ID";
                    worksheet.Cells[1, 7].Value = "物料编码";
                    worksheet.Cells[1, 8].Value = "物料数量";
                    worksheet.Cells[1, 9].Value = "卡板单号";
                    worksheet.Cells[1, 10].Value = "起始类型";
                    worksheet.Cells[1, 11].Value = "目标类型";
                    worksheet.Cells[1, 12].Value = "创建日期";
                    worksheet.Cells[1, 13].Value = "确认时间";
                    worksheet.Cells[1, 14].Value = "完成日期";

                    // 添加数据
                    int row = 2;
                    foreach (var task in tasks)
                    {
                        worksheet.Cells[row, 1].Value = task.TaskTypeDisplayName;
                        worksheet.Cells[row, 2].Value = task.sourcePosition;
                        worksheet.Cells[row, 3].Value = task.targetPosition;
                        worksheet.Cells[row, 4].Value = task.TaskStatusDisplayName;
                        worksheet.Cells[row, 5].Value = task.robotCode;
                        worksheet.Cells[row, 6].Value = task.runTaskId;
                        worksheet.Cells[row, 7].Value = task.MaterialCode;
                        worksheet.Cells[row, 8].Value = task.MaterialQuantity;
                        worksheet.Cells[row, 9].Value = task.PalletNumber;
                        worksheet.Cells[row, 10].Value = task.SourceType;
                        worksheet.Cells[row, 11].Value = task.DestType;
                        worksheet.Cells[row, 12].Value = task.creatTime?.ToString("yyyy-MM-dd HH:mm:ss");
                        worksheet.Cells[row, 13].Value = task.ConfirmTime?.ToString("yyyy-MM-dd HH:mm:ss");
                        worksheet.Cells[row, 14].Value = task.endTime?.ToString("yyyy-MM-dd HH:mm:ss");
                        row++;
                    }

                    // 设置列宽
                    worksheet.Cells.AutoFitColumns();

                    // 返回文件
                    var content = package.GetAsByteArray();
                    return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"任务列表_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出任务列表失败");
                return Json(new { success = false, message = "导出失败：" + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTaskStatistics(DateTime startDate, DateTime endDate, string shift)
        {
            try
            {
                var statistics = await _taskService.GetTaskStatistics(startDate, endDate, shift);
                return Json(new { success = true, data = statistics });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取任务统计失败");
                return Json(new { success = false, message = "获取统计失败" });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetLocations()
        {
            try
            {
                var locations = await _taskService.GetLocations();
                return Json(new { success = true, data = locations });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取位置列表失败");
                return Json(new { success = false, message = "获取位置列表失败" });
            }
        }

    }
}
