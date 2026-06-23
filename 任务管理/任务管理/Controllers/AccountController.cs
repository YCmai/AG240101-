using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Services;

namespace WarehouseManagementSystem.Controllers
{
    public class AccountController : Controller
    {
        private readonly IUserService _userService;
        private readonly ILogger<AccountController> _logger;

        public AccountController(IUserService userService, ILogger<AccountController> logger)
        {
            _userService = userService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Login(string returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password, string returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ModelState.AddModelError("", "用户名和密码不能为空");
                return View();
            }

            var isValid = await _userService.ValidateUser(username, password);
            if (isValid)
            {
                var user = await _userService.GetUserByUsername(username);
                if (user != null)
                {
                    // 设置用户信息到 HttpContext.Items
                    HttpContext.Items["User"] = user;

                    // 设置 Cookie
                    var cookieOptions = new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = false, // 改为false以支持HTTP环境
                        SameSite = SameSiteMode.Lax, // 改为Lax以支持跨站请求
                        Expires = DateTime.Now.AddHours(24)
                    };

                    Response.Cookies.Append("UserSession", user.Username, cookieOptions);
                    
                    // 设置Token过期时间（24小时后）
                    var tokenExpireTime = DateTime.Now.AddHours(24);
                    Response.Cookies.Append("TokenExpireTime", tokenExpireTime.ToString("yyyy-MM-dd HH:mm:ss"), cookieOptions);

                    // 更新最后登录时间
                    await _userService.UpdateLastLoginTime(user.Id);

                    _logger.LogInformation($"用户 {username} 登录成功");

                    if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                    {
                        return Redirect(returnUrl);
                    }

                    return RedirectToAction("Index", "Tasks");
                }
            }

            ModelState.AddModelError("", "用户名或密码错误");
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("UserSession");
            Response.Cookies.Delete("TokenExpireTime");
            HttpContext.Items.Remove("User");
            return RedirectToAction("Login");
        }

        [HttpGet]
        public IActionResult TestCookie()
        {
            var username = Request.Cookies["UserSession"];
            var user = HttpContext.Items["User"] as User;

            return Json(new
            {
                CookieValue = username,
                UserInContext = user?.Username,
                UserActive = user?.IsActive,
                UserRole = user?.Role.ToString(),
                AllCookies = Request.Cookies.Select(c => new { c.Key, c.Value }).ToList()
            });
        }
    }
}