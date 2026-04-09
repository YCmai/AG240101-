using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.RazorPages;
using static System.Net.WebRequestMethods;
using Microsoft.Extensions.Logging;
using Dapper;
using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Controllers;
using WarehouseManagementSystem.Services;
using WarehouseManagementSystem.Models;
using OfficeOpenXml;

public class DisplayLocationController : BaseController
{
    private readonly ILocationService _locationService;
    private readonly ILogger<DisplayLocationController> _logger;

    public DisplayLocationController(ILocationService locationService, ILogger<DisplayLocationController> logger, ISystemExpirationService expirationService)
        : base(expirationService)
    {
        _locationService = locationService;
        _logger = logger;
    }

    public async Task<IActionResult> Index(string searchString, int page = 1)
    {
        try
        {
            int pageSize = 5000;
            var (items, totalItems) = await _locationService.GetLocations(searchString, page, pageSize);
            var (available, used) = await _locationService.GetStorageCapacityStats();

            ViewData["StorageCapacityAvailable"] = available;
            ViewData["StorageCapacityUse"] = used;

            return View(new PagedResult<RCS_Locations>
            {
                Items = items.ToList(),
                TotalItems = totalItems,
                PageNumber = page,
                TotalPages = (int)Math.Ceiling((double)totalItems / pageSize)
            });
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
            // 处理可选字段，移除可能导致验证失败的错误
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
    
    // 修改批量操作方法，接受储位ID列表而不仅仅是区域
    [HttpPost]
    public async Task<IActionResult> BatchClearMaterials(List<int> locationIds)
    {
        try
        {
            if (locationIds == null || !locationIds.Any())
            {
                return Json(new { success = false, message = "请选择要操作的储位" });
            }
            
            var (success, message, affectedCount) = await _locationService.BatchClearMaterialsByIds(locationIds);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量清空储位物料失败");
            return Json(new { success = false, message = "批量操作失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchToggleLock(List<int> locationIds, bool lockState)
    {
        try
        {
            if (locationIds == null || !locationIds.Any())
            {
                return Json(new { success = false, message = "请选择要操作的储位" });
            }
            
            var (success, message, affectedCount) = await _locationService.BatchToggleLockByIds(locationIds, lockState);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            string operation = lockState ? "锁定" : "解锁";
            _logger.LogError(ex, $"批量{operation}储位失败");
            return Json(new { success = false, message = $"批量{operation}失败，请稍后再试。" });
        }
    }

    // 保留原有的按区域批量操作方法
    [HttpPost]
    public async Task<IActionResult> BatchClearMaterialsByGroup(string group)
    {
        try
        {
            if (string.IsNullOrEmpty(group))
            {
                return Json(new { success = false, message = "请指定要操作的区域" });
            }
            
            var (success, message, affectedCount) = await _locationService.BatchClearMaterials(group);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量清空区域 {group} 的物料失败");
            return Json(new { success = false, message = "批量操作失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchToggleLockByGroup(string group, bool lockState)
    {
        try
        {
            if (string.IsNullOrEmpty(group))
            {
                return Json(new { success = false, message = "请指定要操作的区域" });
            }
            
            var (success, message, affectedCount) = await _locationService.BatchToggleLock(group, lockState);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            string operation = lockState ? "锁定" : "解锁";
            _logger.LogError(ex, $"批量{operation}区域 {group} 的储位失败");
            return Json(new { success = false, message = $"批量{operation}失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchSetQuantity(string group, bool isSetFull)
    {
        try
        {
            if (string.IsNullOrEmpty(group))
            {
                return Json(new { success = false, message = "请指定要操作的区域" });
            }
            // 这里假设满为100，空为0，如有不同请自行调整
            var targetQuantity = isSetFull ? "满" : "0";
            var (success, message, affectedCount) = await _locationService.BatchSetQuantityByGroup(group, targetQuantity);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量设置区域 {group} 的数量失败");
            return Json(new { success = false, message = "批量操作失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchSetQuantitySelected(List<int> locationIds, bool isSetFull)
    {
        try
        {
            if (locationIds == null || !locationIds.Any())
            {
                return Json(new { success = false, message = "请选择要操作的储位" });
            }
            var targetQuantity = isSetFull ? "满" : "空";
            var (success, message, affectedCount) = await _locationService.BatchSetQuantityByIds(locationIds, targetQuantity);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量设置储位数量失败");
            return Json(new { success = false, message = "批量操作失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchUpdateMaterialCode(List<int> locationIds, string newMaterialCode)
    {
        try
        {
            if (locationIds == null || !locationIds.Any())
            {
                return Json(new { success = false, message = "请选择要操作的储位" });
            }
            
            if (string.IsNullOrEmpty(newMaterialCode))
            {
                return Json(new { success = false, message = "请输入新的物料编号" });
            }
            
            var (success, message, affectedCount) = await _locationService.BatchUpdateMaterialCode(locationIds, newMaterialCode);
            return Json(new { success, message, affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量修改物料编号失败");
            return Json(new { success = false, message = "批量修改失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchUpdateMaterialCodeByGroup(string group, string newMaterialCode)
    {
        try
        {
            if (string.IsNullOrEmpty(group))
            {
                return Json(new { success = false, message = "请选择要操作的分组" });
            }
            
            if (string.IsNullOrEmpty(newMaterialCode))
            {
                return Json(new { success = false, message = "请输入新的物料编号" });
            }
            
            // 获取该分组内的所有储位ID
            var locations = await _locationService.GetLocationsByGroup(group);
            var locationIds = locations.Select(l => l.Id).ToList();
            
            if (!locationIds.Any())
            {
                return Json(new { success = false, message = "所选分组内没有储位" });
            }
            
            var result = await _locationService.BatchUpdateMaterialCode(locationIds, newMaterialCode);
            return Json(new { success = result.success, message = result.message, affectedCount = result.affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量修改分组 {group} 的物料编号失败");
            return Json(new { success = false, message = "批量修改失败，请稍后再试。" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> BatchClearMaterialCodeByGroup(string group)
    {
        try
        {
            if (string.IsNullOrEmpty(group))
            {
                return Json(new { success = false, message = "请选择要操作的分组" });
            }
            
            var result = await _locationService.BatchClearMaterialCodeByGroup(group);
            return Json(new { success = result.success, message = result.message, affectedCount = result.affectedCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"批量清空分组 {group} 的物料编号失败");
            return Json(new { success = false, message = "批量清空物料编号失败，请稍后再试。" });
        }
    }

    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        try
        {
            // 设置EPPlus许可证上下文
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("储位导入模板");

                // 添加表头
                worksheet.Cells[1, 1].Value = "地图节点";
                worksheet.Cells[1, 2].Value = "节点备注";
                worksheet.Cells[1, 3].Value = "操作点";
                worksheet.Cells[1, 4].Value = "分组";
                worksheet.Cells[1, 5].Value = "举升高度";
                worksheet.Cells[1, 6].Value = "卸载高度";

                // 设置列宽
                worksheet.Column(1).Width = 15;
                worksheet.Column(2).Width = 15;
                worksheet.Column(3).Width = 15;
                worksheet.Column(4).Width = 15;
                worksheet.Column(5).Width = 15;
                worksheet.Column(6).Width = 15;

                // 添加示例数据
                worksheet.Cells[2, 1].Value = "1";
                worksheet.Cells[2, 2].Value = "A区-01";
                worksheet.Cells[2, 3].Value = "1";
                worksheet.Cells[2, 4].Value = "A区";
                worksheet.Cells[2, 5].Value = "100";
                worksheet.Cells[2, 6].Value = "50";

                // 设置表头样式
                var headerRange = worksheet.Cells[1, 1, 1, 6];
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);

                // 设置所有单元格的边框
                var dataRange = worksheet.Cells[1, 1, 2, 6];
                dataRange.Style.Border.Top.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                dataRange.Style.Border.Left.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                dataRange.Style.Border.Right.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                dataRange.Style.Border.Bottom.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;

                // 生成文件
                var fileBytes = package.GetAsByteArray();
                return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "储位导入模板.xlsx");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载模板文件失败");
            return Json(new { success = false, message = "下载模板失败，请稍后重试" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> PreviewExcel(IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "请选择要上传的文件" });
            }

            using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream);
                using (var package = new ExcelPackage(stream))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    var rowCount = worksheet.Dimension.Rows;
                    var colCount = worksheet.Dimension.Columns;

                    // 获取表头
                    var headers = new List<string>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        headers.Add(worksheet.Cells[1, col].Text);
                    }

                    // 获取前10行数据作为预览
                    var previewRows = new List<List<string>>();
                    for (int row = 2; row <= Math.Min(11, rowCount); row++)
                    {
                        var rowData = new List<string>();
                        for (int col = 1; col <= colCount; col++)
                        {
                            rowData.Add(worksheet.Cells[row, col].Text);
                        }
                        previewRows.Add(rowData);
                    }

                    return Json(new
                    {
                        success = true,
                        preview = new
                        {
                            headers = headers,
                            rows = previewRows,
                            totalRows = rowCount - 1 // 减去表头行
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "预览Excel文件失败");
            return Json(new { success = false, message = "预览失败，请检查文件格式是否正确" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> ImportExcel(IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return Json(new { success = false, message = "请选择要上传的文件" });
            }

            // 设置EPPlus许可证上下文
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream);
                using (var package = new ExcelPackage(stream))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    var rowCount = worksheet.Dimension.Rows;
                    var colCount = worksheet.Dimension.Columns;

                    // 验证表头
                    var headers = new List<string>();
                    for (int col = 1; col <= colCount; col++)
                    {
                        headers.Add(worksheet.Cells[1, col].Text.Trim());
                    }

                    // 检查必要的列是否存在
                    var requiredColumns = new[] { "地图节点", "节点备注", "操作点", "分组" };
                    var missingColumns = requiredColumns.Where(col => !headers.Contains(col)).ToList();
                    if (missingColumns.Any())
                    {
                        return Json(new { success = false, message = $"模板缺少必要的列: {string.Join(", ", missingColumns)}" });
                    }

                    // 获取列索引
                    var nodeIndex = headers.IndexOf("地图节点") + 1;
                    var nodeRemarkIndex = headers.IndexOf("节点备注") + 1;
                    var operationPointIndex = headers.IndexOf("操作点") + 1;
                    var groupIndex = headers.IndexOf("分组") + 1;

                    // 检查是否有数据行
                    if (rowCount <= 1)
                    {
                        return Json(new { success = false, message = "Excel文件中没有数据行" });
                    }

                    // 获取所有现有的节点和节点备注，用于检查重复
                    var existingLocations = await _locationService.GetLocations("", 1, int.MaxValue);
                    var existingNodes = existingLocations.Items.Select(l => l.Name).ToList();
                    var existingNodeRemarks = existingLocations.Items.Select(l => l.NodeRemark).ToList();

                    var locations = new List<RCS_Locations>();
                    var errors = new List<string>();
                    var duplicateNodes = new HashSet<string>();
                    var duplicateNodeRemarks = new HashSet<string>();

                    // 从第2行开始读取数据（跳过表头）
                    for (int row = 2; row <= rowCount; row++)
                    {
                        try
                        {
                            // 获取并清理数据
                            var node = worksheet.Cells[row, nodeIndex].Text.Trim();
                            var nodeRemark = worksheet.Cells[row, nodeRemarkIndex].Text.Trim();
                            var operationPoint = worksheet.Cells[row, operationPointIndex].Text.Trim();
                            var group = worksheet.Cells[row, groupIndex].Text.Trim();

                            // 获取举升高度和卸载高度（可选字段）
                            var liftingHeightText = headers.Contains("举升高度") ? worksheet.Cells[row, headers.IndexOf("举升高度") + 1].Text?.Trim() : "";
                            var unloadHeightText = headers.Contains("卸载高度") ? worksheet.Cells[row, headers.IndexOf("卸载高度") + 1].Text?.Trim() : "";

                            int liftingHeight = 0;
                            int unloadHeight = 0;
                            int name = 0;
                            int ipPoint = 0;
                            if (!string.IsNullOrEmpty(liftingHeightText) && !int.TryParse(liftingHeightText, out liftingHeight))
                            {
                                errors.Add($"第{row}行：举升高度必须是数字");
                                continue;
                            }

                            if (!string.IsNullOrEmpty(unloadHeightText) && !int.TryParse(unloadHeightText, out unloadHeight))
                            {
                                errors.Add($"第{row}行：卸载高度必须是数字");
                                continue;
                            }

                            // 验证必填字段
                            if (string.IsNullOrEmpty(node))
                            {
                                errors.Add($"第{row}行：地图节点不能为空");
                                continue;
                            }

                            if (!string.IsNullOrEmpty(node) && !int.TryParse(node, out name))
                            {
                                errors.Add($"第{row}行：地图节点必须是数字");
                                continue;
                            }


                            if (string.IsNullOrEmpty(nodeRemark))
                            {
                                errors.Add($"第{row}行：节点备注不能为空");
                                continue;
                            }

                            if (string.IsNullOrEmpty(operationPoint))
                            {
                                errors.Add($"第{row}行：操作点不能为空");
                                continue;
                            }

                            if (!string.IsNullOrEmpty(operationPoint) && !int.TryParse(operationPoint, out ipPoint))
                            {
                                errors.Add($"第{row}行：操作点必须是数字");
                                continue;
                            }


                            if (string.IsNullOrEmpty(group))
                            {
                                errors.Add($"第{row}行：分组不能为空");
                                continue;
                            }

                           // 检查节点和节点备注是否重复
                            //if (existingNodes.Contains(node) || duplicateNodes.Contains(node))
                            //{
                            //    errors.Add($"第{row}行：地图节点 '{node}' 已存在");
                            //    duplicateNodes.Add(node);
                            //    continue;
                            //}

                            //if (existingNodeRemarks.Contains(nodeRemark) || duplicateNodeRemarks.Contains(nodeRemark))
                            //{
                            //    errors.Add($"第{row}行：节点备注 '{nodeRemark}' 已存在");
                            //    duplicateNodeRemarks.Add(nodeRemark);
                            //    continue;
                            //}

                            // 添加到重复检查集合
                            duplicateNodes.Add(node);
                            duplicateNodeRemarks.Add(nodeRemark);

                            // 创建储位对象，设置默认值
                            var location = new RCS_Locations
                            {
                                Name = node,
                                NodeRemark = nodeRemark,
                                WattingNode = operationPoint,  // 设置操作点为WattingNode
                                Group = group,
                                LiftingHeight = liftingHeight,
                                UnloadHeight = unloadHeight,
                                MaterialCode = null,  //默认值为空
                                PalletID = "0",       //默认值为空
                                Weight = "0",         // 默认值为0
                                Quanitity = "0",      // 默认值为0
                                EntryDate = null,     // 默认值为空
                                Lock = false          // 默认值为false
                            };

                            locations.Add(location);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"第{row}行：数据格式错误 - {ex.Message}");
                        }
                    }

                    if (errors.Any())
                    {
                        return Json(new { success = false, message = string.Join("\n", errors) });
                    }

                    // 批量保存储位
                    int successCount = 0;
                    foreach (var location in locations)
                    {
                        var (success, _) = await _locationService.CreateOrUpdateLocation(location);
                        if (success)
                        {
                            successCount++;
                        }
                    }

                    return Json(new
                    {
                        success = true,
                        message = $"成功导入 {successCount} 个储位",
                        count = successCount
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入Excel文件失败");
            return Json(new { success = false, message = "导入失败，请检查文件格式是否正确" });
        }
    }
}
