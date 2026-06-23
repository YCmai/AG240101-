using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Db;
using Dapper;

namespace WarehouseManagementSystem.Services
{
    public class UserService : IUserService
    {
        private readonly IDatabaseService _db;
        private readonly IConfiguration _configuration;
        private readonly ILogger<UserService> _logger;

        public UserService(
            IDatabaseService db,
            IConfiguration configuration,
            ILogger<UserService> logger)
        {
            _db = db;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<List<User>> GetAllUsers()
        {
            try
            {
                using var conn = _db.CreateConnection();
                var users = await conn.QueryAsync<User>("SELECT * FROM Users");
                return users.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户列表失败");
                throw;
            }
        }

        public async Task<User> GetUserById(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                return await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE Id = @Id", new { Id = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户失败");
                throw;
            }
        }

        public async Task<User> GetUserByUsername(string username)
        {
            try
            {
                using var conn = _db.CreateConnection();
                return await conn.QueryFirstOrDefaultAsync<User>("SELECT * FROM Users WHERE Username = @Username", new { Username = username });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取用户失败");
                throw;
            }
        }

        public async Task<ServiceResult> CreateUser(User user)
        {
            try
            {
                // 检查用户名是否已存在
                var existingUser = await GetUserByUsername(user.Username);
                if (existingUser != null)
                {
                    _logger.LogWarning($"创建用户失败: 用户名 {user.Username} 已存在");
                    return new ServiceResult { Success = false, Message = "用户名已存在" };
                }

                // 直接使用传入的密码 (明文或已由调用方处理)
                // 不再在这里进行哈希

                using var conn = _db.CreateConnection();
                var sql = @"
                    INSERT INTO Users (Username, Password, Role, AllowedTaskTypes, CreateTime, IsActive)
                    VALUES (@Username, @Password, @Role, @AllowedTaskTypes, @CreateTime, @IsActive)";

                var parameters = new DynamicParameters();
                parameters.Add("@Username", user.Username);
                parameters.Add("@Password", user.Password); // 直接存储传入的密码
                parameters.Add("@Role", user.Role);
                parameters.Add("@AllowedTaskTypes", user.AllowedTaskTypes);
                parameters.Add("@CreateTime", DateTime.Now);
                parameters.Add("@IsActive", user.IsActive);

                var result = await conn.ExecuteAsync(sql, parameters);

                if (result > 0)
                {
                    _logger.LogInformation($"用户 {user.Username} 创建成功，影响行数: {result}");
                    return new ServiceResult { Success = true };
                }
                else
                {
                    _logger.LogWarning($"用户 {user.Username} 创建失败，影响行数: {result}");
                    return new ServiceResult { Success = false, Message = "用户创建失败" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"创建用户 {user.Username} 失败");
                return new ServiceResult { Success = false, Message = "创建用户失败：" + ex.Message };
            }
        }

        public async Task<ServiceResult> UpdateUser(User user)
        {
            try
            {
                using var conn = _db.CreateConnection();
                var existingUser = await GetUserById(user.Id);
                if (existingUser == null)
                {
                    return new ServiceResult { Success = false, Message = "用户不存在" };
                }

                // 如果密码为空，保留原密码
                if (string.IsNullOrEmpty(user.Password))
                {
                    user.Password = existingUser.Password;
                }
                // 如果密码不为空且与原密码不同，则更新密码
                else if (user.Password != existingUser.Password)
                {
                    // 密码加密处理（如果需要）
                    // user.Password = BCrypt.Net.BCrypt.HashPassword(user.Password);
                }

                var sql = @"
                    UPDATE Users 
                    SET Username = @Username, 
                        Password = @Password, 
                        Role = @Role, 
                        AllowedTaskTypes = @AllowedTaskTypes,
                        IsActive = @IsActive
                    WHERE Id = @Id";

                var parameters = new DynamicParameters();
                parameters.Add("@Id", user.Id);
                parameters.Add("@Username", user.Username);
                parameters.Add("@Password", user.Password);
                parameters.Add("@Role", user.Role);
                parameters.Add("@AllowedTaskTypes", user.AllowedTaskTypes);
                parameters.Add("@IsActive", user.IsActive);

                var result = await conn.ExecuteAsync(sql, parameters);

                if (result > 0)
                {
                    _logger.LogInformation("用户更新成功");
                    return new ServiceResult { Success = true };
                }
                else
                {
                    _logger.LogWarning("用户更新失败");
                    return new ServiceResult { Success = false, Message = "用户更新失败" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新用户失败");
                return new ServiceResult { Success = false, Message = "更新用户失败：" + ex.Message };
            }
        }

        public async Task<ServiceResult> DeleteUser(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                var result = await conn.ExecuteAsync("Delete from  Users WHERE Id = @Id", new { Id = id });
                
                if (result > 0)
                {
                    _logger.LogInformation("用户删除成功");
                    return new ServiceResult { Success = true };
                }
                else
                {
                    _logger.LogWarning("用户删除失败");
                    return new ServiceResult { Success = false, Message = "用户删除失败" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除用户失败");
                return new ServiceResult { Success = false, Message = "删除用户失败：" + ex.Message };
            }
        }

        public async Task<ServiceResult> ToggleUserActive(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                var user = await GetUserById(id);
                if (user == null)
                {
                    return new ServiceResult { Success = false, Message = "用户不存在" };
                }

                var result = await conn.ExecuteAsync("UPDATE Users SET IsActive = ~IsActive WHERE Id = @Id", new { Id = id });
                
                if (result > 0)
                {
                    _logger.LogInformation("用户状态切换成功");
                    return new ServiceResult { Success = true };
                }
                else
                {
                    _logger.LogWarning("用户状态切换失败");
                    return new ServiceResult { Success = false, Message = "用户状态切换失败" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "切换用户状态失败");
                return new ServiceResult { Success = false, Message = "切换用户状态失败：" + ex.Message };
            }
        }

        public async Task<bool> ValidateUser(string username, string password)
        {
            try
            {
                var user = await GetUserByUsername(username);
                if (user == null || !user.IsActive)
                {
                    _logger.LogWarning($"用户验证失败: 用户不存在或未激活 - {username}");
                    return false;
                }

                _logger.LogInformation($"正在验证用户: {username}");
                _logger.LogInformation($"输入的密码: {password}");
                _logger.LogInformation($"数据库中的密码: {user.Password}");

                // 直接比较明文密码 (不安全！)
                var result = password == user.Password;
                _logger.LogInformation($"密码验证结果: {result}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证用户失败");
                return false;
            }
        }

        public async Task UpdateLastLoginTime(int userId)
        {
            try
            {
                using var conn = _db.CreateConnection();
                var sql = "UPDATE Users SET LastLoginTime = @LastLoginTime WHERE Id = @Id";
                var parameters = new DynamicParameters();
                parameters.Add("@Id", userId);
                parameters.Add("@LastLoginTime", DateTime.Now);

                var result = await conn.ExecuteAsync(sql, parameters);
                if (result > 0)
                {
                    _logger.LogInformation("更新最后登录时间成功");
                }
                else
                {
                    _logger.LogWarning("更新最后登录时间失败");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新最后登录时间失败");
                throw;
            }
        }
    }
} 