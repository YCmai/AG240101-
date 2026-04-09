using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;
using System.Linq;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class AuthorizeAttribute : Attribute, IAuthorizationFilter
    {
        private readonly UserRole[] _roles;
        private readonly RCS_UserTasks.TaskType[] _taskTypes;

        public AuthorizeAttribute(UserRole[] roles = null, RCS_UserTasks.TaskType[] taskTypes = null)
        {
            _roles = roles;
            _taskTypes = taskTypes;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            // 获取当前用户
            var user = context.HttpContext.Items["User"] as User;
            if (user == null)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            // 检查用户是否激活
            if (!user.IsActive)
            {
                context.Result = new ForbidResult();
                return;
            }

            // 如果指定了角色要求
            if (_roles != null && _roles.Length > 0)
            {
                if (!_roles.Contains(user.Role))
                {
                    context.Result = new ForbidResult();
                    return;
                }
            }

            // 如果指定了任务类型要求
            if (_taskTypes != null && _taskTypes.Length > 0)
            {
                // 管理员和主管可以操作所有任务类型
                if (user.Role == UserRole.Admin || user.Role == UserRole.Supervisor)
                {
                    return;
                }

                // 检查操作员是否有权限操作指定的任务类型
                if (user.Role == UserRole.Operator)
                {
                    var allowedTypes = System.Text.Json.JsonSerializer.Deserialize<RCS_UserTasks.TaskType[]>(user.AllowedTaskTypes);
                    if (!_taskTypes.Any(t => allowedTypes.Contains(t)))
                    {
                        context.Result = new ForbidResult();
                        return;
                    }
                }
            }
        }
    }
} 