using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using WarehouseManagementSystem.Attributes;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Collections.Generic;

namespace WarehouseManagementSystem.Controllers
{
    [Authorize(new[] { UserRole.Admin })]
    public class UserController : Controller
    {
        private readonly IUserService _userService;
        private readonly ILogger<UserController> _logger;

        public UserController(IUserService userService, ILogger<UserController> logger)
        {
            _userService = userService;
            _logger = logger;
        }

        public async Task<IActionResult> Index()
        {
            var users = await _userService.GetAllUsers();
            return View(users);
        }

        public IActionResult Create()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            [Bind("Id,Username,Password,Role,IsActive")] User user)
        {
            // --- 在 ModelState.IsValid 检查之前手动处理 AllowedTaskTypes ---

            // 移除 AllowedTaskTypes 相关的 ModelState 错误，因为它将由我们手动处理
            ModelState.Remove("AllowedTaskTypes");

            // 从 Request.Form 中手动获取 taskTypes 的值 (字符串形式的整数)
            var taskTypeStrings = Request.Form["taskTypes"].ToList();

            // 将字符串形式的整数转换为整数列表
            var taskTypeInts = new List<int>();
            if (taskTypeStrings != null)
            {
                foreach (var typeString in taskTypeStrings)
                {
                    if (int.TryParse(typeString, out int typeInt))
                    {
                        taskTypeInts.Add(typeInt);
                    }
                     else
                    {
                        // 如果解析失败，可以记录警告或添加到ModelState错误
                         _logger.LogWarning($"无法将任务类型字符串 '{typeString}' 解析为整数");
                    }
                }
            }

            if (user.Role == UserRole.Operator && taskTypeInts.Any())
            {
                // 将整数列表序列化为 JSON 字符串
                user.AllowedTaskTypes = JsonSerializer.Serialize(taskTypeInts);
            }
            else
            {
                user.AllowedTaskTypes = null; // 如果不是操作员或没有选择任务类型，设置为 null
            }

            // --- 现在可以安全地检查 ModelState.IsValid 了 ---

            if (ModelState.IsValid)
            {
                var result = await _userService.CreateUser(user);
                if (result.Success)
                {
                    return RedirectToAction(nameof(Index));
                }
                ModelState.AddModelError("", result.Message);
            }
            else
            {
                // 添加日志来输出剩余的 ModelState 错误信息
                foreach (var state in ModelState)
                {
                    if (state.Value.Errors.Count > 0)
                    {
                        _logger.LogWarning($"ModelState 验证错误 - 字段: {state.Key}");
                        foreach (var error in state.Value.Errors)
                        {
                            _logger.LogWarning($"  错误消息: {error.ErrorMessage}");
                        }
                    }
                }
                _logger.LogWarning("ModelState 无效，返回创建视图");
            }

            // 如果ModelState无效，并且 AllowedTaskTypes 仍然存在错误，可以手动清除它
            if (ModelState.ContainsKey("AllowedTaskTypes"))
            {
                 ModelState["AllowedTaskTypes"].Errors.Clear();
            }

            return View(user);
        }

        public async Task<IActionResult> Edit(int id)
        {
            var user = await _userService.GetUserById(id);
            if (user == null)
            {
                return NotFound();
            }
            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> Edit(User user)
        {
             // 手动处理 AllowedTaskTypes 字段的绑定，在 ModelState.IsValid 检查之前
            ModelState.Remove("AllowedTaskTypes");
            
            // 如果密码为空，则移除密码验证错误
            if (string.IsNullOrEmpty(user.Password))
            {
                ModelState.Remove("Password");
            }

            // 从 Request.Form 中手动获取 taskTypes 的值 (字符串形式的整数)
            var taskTypeStrings = Request.Form["taskTypes"].ToList();

             // 将字符串形式的整数转换为整数列表
            var taskTypeInts = new List<int>();
            if (taskTypeStrings != null)
            {
                foreach (var typeString in taskTypeStrings)
                {
                    if (int.TryParse(typeString, out int typeInt))
                    {
                        taskTypeInts.Add(typeInt);
                    }
                     else
                    {
                        // 如果解析失败，可以记录警告或添加到ModelState错误
                         _logger.LogWarning($"无法将任务类型字符串 '{typeString}' 解析为整数");
                    }
                }
            }

            if (user.Role == UserRole.Operator && taskTypeInts.Any())
            {
                // 将整数列表序列化为 JSON 字符串
                user.AllowedTaskTypes = JsonSerializer.Serialize(taskTypeInts);
            }
            else
            {
                user.AllowedTaskTypes = null; // 如果不是操作员或没有选择任务类型，设置为 null
            }

            if (ModelState.IsValid)
            {
                var result = await _userService.UpdateUser(user);
                if (result.Success)
                {
                    return RedirectToAction(nameof(Index));
                }
                ModelState.AddModelError("", result.Message);
            }
            else
            {
                 // 添加日志来输出剩余的 ModelState 错误信息
                foreach (var state in ModelState)
                {
                    if (state.Value.Errors.Count > 0)
                    {
                        _logger.LogWarning($"ModelState 验证错误 - 字段: {state.Key}");
                        foreach (var error in state.Value.Errors)
                        {
                            _logger.LogWarning($"  错误消息: {error.ErrorMessage}");
                        }
                    }
                }
                _logger.LogWarning("ModelState 无效，返回编辑视图");
            }

             // 如果ModelState无效，并且 AllowedTaskTypes 仍然存在错误，可以手动清除它
            if (ModelState.ContainsKey("AllowedTaskTypes"))
            {
                 ModelState["AllowedTaskTypes"].Errors.Clear();
            }

            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var result = await _userService.DeleteUser(id);
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var result = await _userService.ToggleUserActive(id);
            return Json(result);
        }
    }
} 