using Microsoft.EntityFrameworkCore;

using WarehouseManagementSystem.Models;

public class ApplicationDbContext : DbContext
{
    public DbSet<RCS_Locations> Locations { get; set; }

    public DbSet<RCS_UserTasks> UserTasks { get; set; }

    public DbSet<RCS_TaskCache> TaskCaches { get; set; }

    public DbSet<AgvInfo> AgvInfos { get; set; }

    public DbSet<SystemSetting> SystemSettings { get; set; }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }
}
