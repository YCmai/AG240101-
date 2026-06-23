using System.Collections.Generic;
using System.Threading.Tasks;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Services
{
    public interface IUserService
    {
        Task<List<User>> GetAllUsers();
        Task<User> GetUserById(int id);
        Task<User> GetUserByUsername(string username);
        Task<ServiceResult> CreateUser(User user);
        Task<ServiceResult> UpdateUser(User user);
        Task<ServiceResult> DeleteUser(int id);
        Task<ServiceResult> ToggleUserActive(int id);
        Task<bool> ValidateUser(string username, string password);
        Task UpdateLastLoginTime(int userId);
    }
} 