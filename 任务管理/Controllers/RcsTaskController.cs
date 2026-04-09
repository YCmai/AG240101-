using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Service;
using Microsoft.Extensions.Logging;

namespace WarehouseManagementSystem.Controllers
{
    /// <summary>
    /// RCS任务管理控制器-负责接收上位机的任务
    /// </summary>
    [ApiController]
    [Route("api/rcs")]
    public class RcsTaskController : ControllerBase
    {
        private readonly IRcsTaskService _rcsTaskService;
        private readonly ILogger<RcsTaskController> _logger;

        public RcsTaskController(IRcsTaskService rcsTaskService, ILogger<RcsTaskController> logger)
        {
            _rcsTaskService = rcsTaskService;
            _logger = logger;
        } 

        /// <summary>
        /// 添加任务
        /// </summary>
        /// <param name="request">任务请求参数</param>
        /// <returns>任务添加结果</returns>
        [HttpPost("addTask")]
        public async Task<IActionResult> AddTask([FromBody] AddTaskRequest request)
        {
            if (request == null)
            {
                _logger.LogWarning("AddTask请求参数为空");
                return BadRequest(new TaskResponse
                {
                    suNum = null,
                    callTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    status = "Fail",
                    failType = 104,
                    message = "请求参数为空"
                });
            }

            // [DispatchStrategy] 记录上位机新传入的出入库标识和货架标识，方便调度策略排查
            _logger.LogInformation(
                "调用AddTask接口: RequestCode={RequestCode}, PalletNumber={PalletNumber}, TaskType={TaskType}, SourceBin={SourceBin}, DestBin={DestBin}, TaskIdentification={TaskIdentification}, ShelvesIdentification={ShelvesIdentification}",
                request.toNum,
                request.suNum,
                request.taskType,
                request.sourceBin,
                request.destBin,
                request.TaskIdentification,
                request.ShelvesIdentification);

            try
            {
                var result = await _rcsTaskService.AddTaskAsync(request);
                _logger.LogInformation("AddTask接口调用成功，返回：{@Result}", result);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddTask接口调用异常");
                throw;
            }
        }

        /// <summary>
        /// 任务状态回传
        /// </summary>
        /// <param name="request">任务状态请求</param>
        /// <returns>任务状态更新结果</returns>
        [HttpPost("updateTaskStatus")]
        public async Task<IActionResult> UpdateTaskStatus([FromBody] UpdateTaskStatusRequest request)
        {
            if (request == null)
            {
                return BadRequest(new TaskResponse
                {
                    suNum = request.suNum,
                    callTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    status = "Fail",
                    message = "请求参数为空"
                });
            }

            var result = await _rcsTaskService.UpdateTaskStatusAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 取消任务
        /// </summary>
        /// <param name="request">取消任务请求</param>
        /// <returns>取消任务结果</returns>
        [HttpPost("cancelTask")]
        public async Task<IActionResult> CancelTask([FromBody] CancelTaskRequest request)
        {
            _logger.LogInformation("调用CancelTask接口，请求参数：{@Request}", request);

            if (request == null)
            {
                _logger.LogWarning("CancelTask请求参数为空");
                return BadRequest(new TaskResponse
                {
                    suNum = request.suNum,
                    message = "请求参数为空"
                });
            }

            try
            {
                var result = await _rcsTaskService.CancelTaskAsync(request);
                _logger.LogInformation("CancelTask接口调用成功，返回：{@Result}", result);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CancelTask接口调用异常");
                throw;
            }
        }

        /// <summary>
        /// 获取任务清单
        /// </summary>
        /// <param name="startTime">开始时间</param>
        /// <param name="endTime">结束时间</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">每页记录数</param>
        /// <returns>任务清单</returns>
        [HttpGet("getTaskList")]
        public async Task<IActionResult> GetTaskList(
            [FromQuery] DateTime? startTime = null, 
            [FromQuery] DateTime? endTime = null, 
            [FromQuery] int pageIndex = 1, 
            [FromQuery] int pageSize = 20)
        {
            var request = new GetTaskListRequest
            {
                startTime = startTime,
                endTime = endTime,
                pageIndex = pageIndex < 1 ? 1 : pageIndex,
                pageSize = pageSize < 1 ? 20 : pageSize
            };

            var result = await _rcsTaskService.GetTaskListAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 按时间段查询任务
        /// </summary>
        /// <param name="request">按时间段查询任务请求</param>
        /// <returns>任务清单</returns>
        [HttpPost("timeGetTask")]
        public async Task<IActionResult> GetTaskByTime([FromBody] GetTaskByTimeRequest request)
        {
            if (request == null)
            {
                return BadRequest(new TaskListResponse
                {
                    status = "Fail",
                    message = "请求参数为空"
                });
            }

            var result = await _rcsTaskService.GetTaskByTimeAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 根据任务编码查询任务
        /// </summary>
        /// <param name="toNum">任务编码</param>
        /// <returns>任务详情</returns>
        [HttpGet("toGetTask")]
        public async Task<IActionResult> GetTaskById([FromQuery] string toNum)
        {
            if (string.IsNullOrEmpty(toNum))
            {
                return BadRequest(new TaskListResponse
                {
                    status = "Fail",
                    message = "任务编码不能为空"
                });
            }

            var result = await _rcsTaskService.GetTaskByIdAsync(toNum);
            return Ok(result);
        }

        /// <summary>
        /// AGV当前状态查询
        /// </summary>
        /// <param name="request">AGV状态查询请求</param>
        /// <returns>AGV当前状态</returns>
        [HttpPost("agvStatue")]
        public async Task<IActionResult> GetAgvState([FromBody] AgvStateRequest request)
        {
            if (request == null)
            {
                return BadRequest(new AgvStateResponse
                {
                    status = "Fail",
                    message = "请求参数为空"
                });
            }

            var result = await _rcsTaskService.GetAgvStateAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// AGV总状态查询
        /// </summary>
        /// <param name="request">AGV总状态查询请求</param>
        /// <returns>AGV总状态</returns>
        [HttpPost("agvStatus")]
        public async Task<IActionResult> GetAgvStatus([FromBody] AgvStatusRequest request)
        {
            if (request == null)
            {
                return BadRequest(new AgvStatusResponse
                {
                    status = "Fail",
                    message = "请求参数为空"
                });
            }

            var result = await _rcsTaskService.GetAgvStatusAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// 总库位状态查询
        /// </summary>
        /// <returns>总库位状态</returns>
        [HttpGet("binStatus")]
        public async Task<IActionResult> GetBinStatus()
        {
            var result = await _rcsTaskService.GetBinStatusAsync();
            return Ok(result);
        }

        /// <summary>
        /// 物料交收区库位状态更新
        /// </summary>
        /// <param name="request">binId, binStatus</param>
        /// <returns>状态</returns>
        [HttpPost("binUpdate")]
        public async Task<IActionResult> BinUpdate([FromBody] BinUpdateRequest request)
        {
            _logger.LogInformation("调用BinUpdate接口，请求参数：{@Request}", request);

            if (request == null || string.IsNullOrEmpty(request.binId))
            {
                _logger.LogWarning("BinUpdate请求参数为空或BinId为空");
                return Ok(new { status = "Fail", binId = request?.binId });
            }

            try
            {
                var result = await _rcsTaskService.BinUpdateAsync(request);
                _logger.LogInformation("BinUpdate接口调用成功，返回：{@Result}", result);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BinUpdate接口调用异常");
                throw;
            }
        }
    }
} 
