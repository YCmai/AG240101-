using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static System.Net.WebRequestMethods;
using WarehouseManagementSystem.Controllers;
using WarehouseManagementSystem.Services;
using WarehouseManagementSystem.Models;

public class LocationController : BaseController
{
    private readonly ApplicationDbContext _context;

    private readonly ConnectionStatusService _connectionStatusService;

    private readonly HttpClient _httpClient;

    private readonly ILocationService _locationService;

    private readonly IConfiguration _configuration;

    private readonly ILogger<LocationController> _logger;

    public LocationController(ApplicationDbContext context, ILogger<LocationController> logger, 
        ILocationService locationService, ConnectionStatusService connectionStatusService, 
        IConfiguration configuration, ISystemExpirationService expirationService)
        : base(expirationService)
    {
        _context = context;
        _connectionStatusService = connectionStatusService;
        _httpClient = new HttpClient();
        _configuration = configuration;
        _locationService = locationService;
        _logger = logger;
    }

    private (string baseUrl, string port, string http) GetConnectionParameters()
    {
        var baseUrl = _configuration["ConnectionStrings:IPAddress"];
        var port = _configuration["ConnectionStrings:Port"];
        var http = _configuration["ConnectionStrings:Http"];
        return (baseUrl, port, http);
    }




    public async Task<IActionResult> Index(string searchString, int page = 1)
    {
        try
        {
            int pageSize = 20; // 或者其他适合的页面大小
            
            // 保存搜索条件到ViewData
            ViewData["searchString"] = searchString;

            var (items, totalItems) = await _locationService.GetSearchLocations(searchString, page, pageSize);

            var model = new PagedResult<RCS_Locations>
            {
                Items = items.ToList(),
                TotalItems = totalItems,
                PageNumber = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling((double)totalItems / pageSize)
            };

            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取库位列表失败");
            return View(new PagedResult<RCS_Locations>());
        }
    }

    public async Task<IActionResult> CreateEdit(int? id)
    {
        try
        {
            if (id == null)
            {
                return View(new RCS_Locations());
            }

            var location = await _locationService.GetLocationById(id.Value);
            if (location == null)
            {
                return NotFound();
            }

            return View(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取库位信息失败");
            TempData["Message"] = "获取库位信息失败！请稍后重试。";
            TempData["MessageType"] = "danger";
            return View(new RCS_Locations());
        }
    }


    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEdit(RCS_Locations location)
    {
        try
        {
            // 处理可选字段
            HandleOptionalFields(location);
            
            // 检查模型验证状态
            if (!ModelState.IsValid)
            {
                _logger.LogWarning($"表单验证失败: {string.Join(", ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))}");
                TempData["Message"] = "表单验证失败，请检查输入内容。";
                TempData["MessageType"] = "danger";
                return View(location);
            }

            var (success, message) = await _locationService.CreateOrUpdateLocation(location);

            TempData["Message"] = message;
            TempData["MessageType"] = success ? "success" : "danger";
            if (success)
            {
                TempData["RedirectAfterDelay"] = true;
            }

            return View(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存库位信息失败");
            TempData["Message"] = "保存失败，请稍后再试。";
            TempData["MessageType"] = "danger";
            return View(location);
        }
    }


    [HttpPost]
    public async Task<IActionResult> DeleteConfirmed(int id, int type)
    {
        try
        {
            var (success, message) = await _locationService.HandleLocationOperation(id, type);
            return Json(new { success, message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "操作失败");
            return Json(new { success = false, message = "操作失败，请稍后再试。" });
        }
    }

    private void HandleOptionalFields(RCS_Locations location)
    {
        // 手动移除可选字段的错误
        if (string.IsNullOrEmpty(location.MaterialCode))
        {
            ModelState.Remove(nameof(location.MaterialCode));
        }
        if (string.IsNullOrEmpty(location.PalletID))
        {
            ModelState.Remove(nameof(location.PalletID));
        }
        if (string.IsNullOrEmpty(location.Weight))
        {
            ModelState.Remove(nameof(location.Weight));
        }
        if (string.IsNullOrEmpty(location.Quanitity))
        {
            ModelState.Remove(nameof(location.Quanitity));
        }
        if (string.IsNullOrEmpty(location.EntryDate))
        {
            ModelState.Remove(nameof(location.EntryDate));
        }
       

    }


}
