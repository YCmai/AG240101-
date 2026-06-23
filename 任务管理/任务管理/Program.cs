using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog;
using System.Collections.Concurrent;
using System.IO;
using Dapper;

using WarehouseManagementSystem.Hubs;
using WarehouseManagementSystem.Hubs.TcpClient.Hubs;
using WarehouseManagementSystem.Models.TcpService;
using WarehouseManagementSystem.Service.TcpService;
using WarehouseManagementSystem.Service.Io;
using WarehouseManagementSystem.Db;
using WarehouseManagementSystem.Service.WepApi;
using WarehouseManagementSystem.Service.Plc;
using Microsoft.Data.SqlClient;
using System.Data;
using Services.Tasks;
using WarehouseManagementSystem.Service.Tasks;
using WarehouseManagementSystem.Services;
using WarehouseManagementSystem.Middleware;
using Microsoft.AspNetCore.Authentication.Cookies;
using WarehouseManagementSystem.Models;
using WarehouseManagementSystem.Service;

var builder = WebApplication.CreateBuilder(args);

// 确保日志目录存在
var logDirectory = Path.Combine(builder.Environment.ContentRootPath, "Logs");
if (!Directory.Exists(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

// 配置 Serilog
var logPath = Path.Combine(logDirectory, "RCS-Pad-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information() // 改为Information级别，减少调试日志
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Filter.ByExcluding(logEvent =>
        logEvent.MessageTemplate.Text.Contains("Request starting HTTP") ||
        logEvent.MessageTemplate.Text.Contains("Executing endpoint") ||
        logEvent.MessageTemplate.Text.Contains("Executed endpoint") ||
        logEvent.MessageTemplate.Text.Contains("Request finished") ||
        logEvent.MessageTemplate.Text.Contains("Route matched") ||
        logEvent.MessageTemplate.Text.Contains("Executing action") ||
        logEvent.MessageTemplate.Text.Contains("Executed action"))
    .Enrich.FromLogContext()
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
    .WriteTo.File(logPath,
        rollingInterval: RollingInterval.Day,
        restrictedToMinimumLevel: LogEventLevel.Information, // 改为Information级别
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 31,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(5) // 增加刷新间隔到5秒
    )
    .CreateLogger();

// 使用 Serilog
builder.Host.UseSerilog();

// 添加数据库上下文
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"), 
        sqlServerOptionsAction: sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
            sqlOptions.CommandTimeout(30); // 30秒超时
        }));

// 添加数据库连接注册（优化连接池）
builder.Services.AddScoped<IDbConnection>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    var connection = new SqlConnection(connectionString);
    connection.Open(); // 预打开连接
    return connection;
});

// 注册 LocationService
builder.Services.AddScoped<ILocationService, LocationService>();
builder.Services.AddScoped<ITaskService, TaskService>();

// 添加HttpClient工厂（优化HTTP连接）
builder.Services.AddHttpClient("DefaultClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "WarehouseManagementSystem");
});

// 添加内存缓存
builder.Services.AddMemoryCache();

builder.Services.AddControllersWithViews();

// 添加认证服务
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAuthenticatedUser", policy => policy.RequireAuthenticatedUser());
});

// 注册用户服务
builder.Services.AddScoped<IUserService, UserService>();

// 添加控制器和视图支持
#region 新服务添加
builder.Services.AddSingleton<ConnectionStatusService>();
builder.Services.AddSingleton<IClientManagerService, ClientManagerService>(); // 注册 IClientManagerService
builder.Services.AddSingleton<IClientDataService, ClientDataService>(); // 如果未注册 IClientDataService，也需要注册
builder.Services.AddSingleton<IMessageHistoryService, MessageHistoryService>();
// 注册数据清理服务
builder.Services.AddSignalR();

// 注册RcsTaskService服务（移到前面，确保在TaskCacheProcessor之前注册）
builder.Services.AddScoped<IAutoDestinationAllocator, AutoDestinationAllocator>();
builder.Services.AddScoped<IRcsTaskService, RcsTaskService>();

// 注册后台服务
builder.Services.AddHostedService<DataCleanupService>();
builder.Services.AddHostedService<TcpServerService>();
builder.Services.AddHostedService<TaskCacheProcessor>();
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();

//IO
//方法注册
//builder.Services.AddSingleton<IIOService, IOService>();
//builder.Services.AddSingleton<IIODeviceService, IODeviceService>();
//builder.Services.AddSingleton<ITaskGenerationService, TaskGenerationService>();

// 添加系统到期检查服务
builder.Services.AddSingleton<ISystemExpirationService, SystemExpirationService>();

//读取信号
//builder.Services.AddHostedService<StartupService>();
//builder.Services.AddHostedService<WarehouseManagementSystem.Service.Tasks.AGVTaskGenerationService>();
//builder.Services.AddHostedService<WarehouseManagementSystem.Service.DiagnosticService>();

// 确保已注册DatabaseService（移到上面了，这里删除重复注册）
//if (!builder.Services.Any(s => s.ServiceType == typeof(IDatabaseService)))
//{
//    builder.Services.AddScoped<IDatabaseService, DatabaseService>();
//}

#endregion


builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(5055, listenOptions =>
    {
        listenOptions.UseConnectionLogging(); // 添加连接日志
    });
    
    // 优化服务器配置
    serverOptions.Limits.MaxConcurrentConnections = 100;
    serverOptions.Limits.MaxConcurrentUpgradedConnections = 100;
    serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// 确保数据库和默认用户存在
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.Database.EnsureCreated();

        // 创建Users表（如果不存在）
        using var conn = context.Database.GetDbConnection();
        var createTableSql = @"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Users')
            BEGIN
                CREATE TABLE Users (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Username NVARCHAR(50) NOT NULL,
                    Password NVARCHAR(100) NOT NULL,
                    Role INT NOT NULL,
                    AllowedTaskTypes NVARCHAR(MAX),
                    CreateTime DATETIME NOT NULL,
                    LastLoginTime DATETIME,
                    IsActive BIT NOT NULL DEFAULT 1
                )
            END";
        await conn.ExecuteAsync(createTableSql);

        var ensureAgvInfoColumnsSql = @"
            IF COL_LENGTH('AGV_Info', 'ClientEndpoint') IS NULL ALTER TABLE AGV_Info ADD ClientEndpoint NVARCHAR(100) NULL;
            IF COL_LENGTH('AGV_Info', 'PacketHeader') IS NULL ALTER TABLE AGV_Info ADD PacketHeader NVARCHAR(20) NULL;
            IF COL_LENGTH('AGV_Info', 'PacketLength') IS NULL ALTER TABLE AGV_Info ADD PacketLength INT NOT NULL CONSTRAINT DF_AGV_Info_PacketLength DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'VehicleId') IS NULL ALTER TABLE AGV_Info ADD VehicleId INT NOT NULL CONSTRAINT DF_AGV_Info_VehicleId DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'CurrentAngleRaw') IS NULL ALTER TABLE AGV_Info ADD CurrentAngleRaw INT NOT NULL CONSTRAINT DF_AGV_Info_CurrentAngleRaw DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'ForkHeight') IS NULL ALTER TABLE AGV_Info ADD ForkHeight INT NOT NULL CONSTRAINT DF_AGV_Info_ForkHeight DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'CurrentSpeed') IS NULL ALTER TABLE AGV_Info ADD CurrentSpeed INT NOT NULL CONSTRAINT DF_AGV_Info_CurrentSpeed DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'TaskStatus') IS NULL ALTER TABLE AGV_Info ADD TaskStatus INT NOT NULL CONSTRAINT DF_AGV_Info_TaskStatus DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'TaskId') IS NULL ALTER TABLE AGV_Info ADD TaskId INT NOT NULL CONSTRAINT DF_AGV_Info_TaskId DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'LoadPoint') IS NULL ALTER TABLE AGV_Info ADD LoadPoint INT NOT NULL CONSTRAINT DF_AGV_Info_LoadPoint DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'UnloadPoint') IS NULL ALTER TABLE AGV_Info ADD UnloadPoint INT NOT NULL CONSTRAINT DF_AGV_Info_UnloadPoint DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StatusText1Id') IS NULL ALTER TABLE AGV_Info ADD StatusText1Id INT NOT NULL CONSTRAINT DF_AGV_Info_StatusText1Id DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StatusText2Value') IS NULL ALTER TABLE AGV_Info ADD StatusText2Value INT NOT NULL CONSTRAINT DF_AGV_Info_StatusText2Value DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StatusTextDisplay') IS NULL ALTER TABLE AGV_Info ADD StatusTextDisplay NVARCHAR(200) NULL;
            IF COL_LENGTH('AGV_Info', 'StopInfo1') IS NULL ALTER TABLE AGV_Info ADD StopInfo1 BIGINT NOT NULL CONSTRAINT DF_AGV_Info_StopInfo1 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StopInfo1Display') IS NULL ALTER TABLE AGV_Info ADD StopInfo1Display NVARCHAR(MAX) NULL;
            IF COL_LENGTH('AGV_Info', 'StopInfo2') IS NULL ALTER TABLE AGV_Info ADD StopInfo2 BIGINT NOT NULL CONSTRAINT DF_AGV_Info_StopInfo2 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StopInfo2Display') IS NULL ALTER TABLE AGV_Info ADD StopInfo2Display NVARCHAR(MAX) NULL;
            IF COL_LENGTH('AGV_Info', 'WifiSignal') IS NULL ALTER TABLE AGV_Info ADD WifiSignal INT NOT NULL CONSTRAINT DF_AGV_Info_WifiSignal DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'RunTime') IS NULL ALTER TABLE AGV_Info ADD RunTime INT NOT NULL CONSTRAINT DF_AGV_Info_RunTime DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'PowerOnTime') IS NULL ALTER TABLE AGV_Info ADD PowerOnTime INT NOT NULL CONSTRAINT DF_AGV_Info_PowerOnTime DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'OperationMode') IS NULL ALTER TABLE AGV_Info ADD OperationMode INT NOT NULL CONSTRAINT DF_AGV_Info_OperationMode DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'OperationModeDisplay') IS NULL ALTER TABLE AGV_Info ADD OperationModeDisplay NVARCHAR(50) NULL;
            IF COL_LENGTH('AGV_Info', 'DrivingSpeed') IS NULL ALTER TABLE AGV_Info ADD DrivingSpeed INT NOT NULL CONSTRAINT DF_AGV_Info_DrivingSpeed DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'DrivingDistance') IS NULL ALTER TABLE AGV_Info ADD DrivingDistance INT NOT NULL CONSTRAINT DF_AGV_Info_DrivingDistance DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'SteeringAngleRaw') IS NULL ALTER TABLE AGV_Info ADD SteeringAngleRaw INT NOT NULL CONSTRAINT DF_AGV_Info_SteeringAngleRaw DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'SteeringAngle') IS NULL ALTER TABLE AGV_Info ADD SteeringAngle FLOAT NOT NULL CONSTRAINT DF_AGV_Info_SteeringAngle DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'RunMode') IS NULL ALTER TABLE AGV_Info ADD RunMode INT NOT NULL CONSTRAINT DF_AGV_Info_RunMode DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'RunModeDisplay') IS NULL ALTER TABLE AGV_Info ADD RunModeDisplay NVARCHAR(50) NULL;
            IF COL_LENGTH('AGV_Info', 'BatteryCurrentRaw') IS NULL ALTER TABLE AGV_Info ADD BatteryCurrentRaw INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryCurrentRaw DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryCurrent') IS NULL ALTER TABLE AGV_Info ADD BatteryCurrent FLOAT NOT NULL CONSTRAINT DF_AGV_Info_BatteryCurrent DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'DischargeVoltageRaw') IS NULL ALTER TABLE AGV_Info ADD DischargeVoltageRaw INT NOT NULL CONSTRAINT DF_AGV_Info_DischargeVoltageRaw DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'DischargeVoltage') IS NULL ALTER TABLE AGV_Info ADD DischargeVoltage FLOAT NOT NULL CONSTRAINT DF_AGV_Info_DischargeVoltage DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'ChargeVoltageRaw') IS NULL ALTER TABLE AGV_Info ADD ChargeVoltageRaw INT NOT NULL CONSTRAINT DF_AGV_Info_ChargeVoltageRaw DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'ChargeVoltage') IS NULL ALTER TABLE AGV_Info ADD ChargeVoltage FLOAT NOT NULL CONSTRAINT DF_AGV_Info_ChargeVoltage DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryCapacity') IS NULL ALTER TABLE AGV_Info ADD BatteryCapacity INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryCapacity DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'RemainingCapacity') IS NULL ALTER TABLE AGV_Info ADD RemainingCapacity INT NOT NULL CONSTRAINT DF_AGV_Info_RemainingCapacity DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryTemp1') IS NULL ALTER TABLE AGV_Info ADD BatteryTemp1 INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryTemp1 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryTemp2') IS NULL ALTER TABLE AGV_Info ADD BatteryTemp2 INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryTemp2 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryTemp3') IS NULL ALTER TABLE AGV_Info ADD BatteryTemp3 INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryTemp3 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryTemp4') IS NULL ALTER TABLE AGV_Info ADD BatteryTemp4 INT NOT NULL CONSTRAINT DF_AGV_Info_BatteryTemp4 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'BatteryTempAverage') IS NULL ALTER TABLE AGV_Info ADD BatteryTempAverage FLOAT NOT NULL CONSTRAINT DF_AGV_Info_BatteryTempAverage DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StopInfo3') IS NULL ALTER TABLE AGV_Info ADD StopInfo3 BIGINT NOT NULL CONSTRAINT DF_AGV_Info_StopInfo3 DEFAULT 0;
            IF COL_LENGTH('AGV_Info', 'StopInfo3Display') IS NULL ALTER TABLE AGV_Info ADD StopInfo3Display NVARCHAR(MAX) NULL;
            IF COL_LENGTH('AGV_Info', 'ReservedHex') IS NULL ALTER TABLE AGV_Info ADD ReservedHex NVARCHAR(200) NULL;
            IF COL_LENGTH('AGV_Info', 'RawHex') IS NULL ALTER TABLE AGV_Info ADD RawHex NVARCHAR(MAX) NULL;";
        await conn.ExecuteAsync(ensureAgvInfoColumnsSql);

        var agvInfoCommentsSql = @"
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'客户端IP和端口', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'ClientEndpoint';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'TCP报文头(4A 54 03 29)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'PacketHeader';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'收到的报文总长度(当前固定100字节)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'PacketLength';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'小车ID(Byte4-5)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'VehicleId';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'当前车体角度原始值(Byte14-15)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'CurrentAngleRaw';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'货叉高度(Byte18-19)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'ForkHeight';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'当前行驶速度(Byte20-21)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'CurrentSpeed';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'任务状态(Byte22-23)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'TaskStatus';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'任务ID(Byte24-25)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'TaskId';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'上料点(Byte26-27)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'LoadPoint';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'下料点(Byte28-29)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'UnloadPoint';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'运行状态第一段文本ID(Byte30-31)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StatusText1Id';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'运行状态第二段值(Byte32-33)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StatusText2Value';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'拼装后的运行状态描述', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StatusTextDisplay';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息1原始位值(Byte34-37)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo1';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息1中文解释', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo1Display';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息2原始位值(Byte38-41)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo2';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息2中文解释', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo2Display';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'无线信号强度(Byte42-43)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'WifiSignal';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'运行时间(Byte44-45)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'RunTime';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'通电时间(Byte46-47)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'PowerOnTime';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'操作模式(Byte50-51,0=人工叉车,1=AGV)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'OperationMode';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'操作模式文本', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'OperationModeDisplay';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'行驶速度(Byte52-53)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'DrivingSpeed';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'行驶距离(Byte54-55)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'DrivingDistance';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'转向角原始值(Byte56-57)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'SteeringAngleRaw';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'转向角(Byte56-57换算后)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'SteeringAngle';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'运行模式(Byte58-59)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'RunMode';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'运行模式文本', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'RunModeDisplay';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'电池电流原始值(Byte60-61)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryCurrentRaw';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'电池电流(Byte60-61换算后)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryCurrent';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'放电电压原始值(Byte62-63)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'DischargeVoltageRaw';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'放电电压(Byte62-63换算后)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'DischargeVoltage';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'充电电压原始值(Byte64-65)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'ChargeVoltageRaw';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'充电电压(Byte64-65换算后)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'ChargeVoltage';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'电池总容量(Byte66-67)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryCapacity';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'电池剩余容量(Byte68-69)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'RemainingCapacity';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'温度传感器1(Byte70-71)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryTemp1';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'温度传感器2(Byte72-73)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryTemp2';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'温度传感器3(Byte74-75)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryTemp3';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'温度传感器4(Byte76-77)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryTemp4';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'4路电池温度平均值', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'BatteryTempAverage';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息3原始位值(Byte78-81)', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo3';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'停机信息3解释', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'StopInfo3Display';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'保留字节区(Byte82-99)十六进制', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'ReservedHex';
            EXEC dbo.sp_addorupdateextendedproperty 'MS_Description', N'完整原始TCP十六进制报文', 'SCHEMA', 'dbo', 'TABLE', 'AGV_Info', 'COLUMN', 'RawHex';";
        await conn.ExecuteAsync(@"
            IF OBJECT_ID('dbo.sp_addorupdateextendedproperty', 'P') IS NULL
            EXEC('CREATE PROCEDURE dbo.sp_addorupdateextendedproperty AS BEGIN SET NOCOUNT ON; END')");
        await conn.ExecuteAsync(@"
            ALTER PROCEDURE dbo.sp_addorupdateextendedproperty
                @name SYSNAME,
                @value SQL_VARIANT,
                @level0type VARCHAR(128),
                @level0name SYSNAME,
                @level1type VARCHAR(128),
                @level1name SYSNAME,
                @level2type VARCHAR(128),
                @level2name SYSNAME
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1
                    FROM fn_listextendedproperty(@name, @level0type, @level0name, @level1type, @level1name, @level2type, @level2name)
                )
                    EXEC sp_updateextendedproperty @name=@name, @value=@value,
                        @level0type=@level0type, @level0name=@level0name,
                        @level1type=@level1type, @level1name=@level1name,
                        @level2type=@level2type, @level2name=@level2name;
                ELSE
                    EXEC sp_addextendedproperty @name=@name, @value=@value,
                        @level0type=@level0type, @level0name=@level0name,
                        @level1type=@level1type, @level1name=@level1name,
                        @level2type=@level2type, @level2name=@level2name;
            END");
        await conn.ExecuteAsync(agvInfoCommentsSql);

        // 检查是否存在默认管理员用户
        var userService = services.GetRequiredService<IUserService>();
        var adminUser = await userService.GetUserByUsername("admin");
        if (adminUser == null)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("开始创建默认管理员用户");
            
            var password = "admin123";
            // 不再进行哈希，直接使用明文密码
            // var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);
            logger.LogInformation($"原始密码 (明文): {password}");
            // logger.LogInformation($"加密后的密码: {hashedPassword}");

            // 创建默认管理员用户
            var defaultAdmin = new User
            {
                Username = "admin",
                Password = password, // 直接存储明文密码
                Role = UserRole.Admin,
                IsActive = true,
                CreateTime = DateTime.Now
            };
            await userService.CreateUser(defaultAdmin);
            logger.LogInformation("默认管理员用户创建完成");
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "初始化数据库时发生错误");
    }
}

// 配置中间件
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// 添加系统访问时间限制中间件
app.Use(async (context, next) =>
{
    // 检查是否是静态文件请求，静态文件请求不做限制
    if (!context.Request.Path.StartsWithSegments("/css") && 
        !context.Request.Path.StartsWithSegments("/js") &&
        !context.Request.Path.StartsWithSegments("/lib") &&
        !context.Request.Path.StartsWithSegments("/favicon.ico"))
    {
        var expirationDateString = app.Configuration["SystemAccess:ExpirationDate"];
        if (!string.IsNullOrEmpty(expirationDateString) && 
            DateTime.TryParse(expirationDateString, out var expirationDate))
        {
            if (DateTime.Now > expirationDate)
            {
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync($@"
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset='utf-8' />
                    <meta name='viewport' content='width=device-width, initial-scale=1.0' />
                    <title>系统已过期</title>
                    <style>
                        body {{ font-family: 'Microsoft YaHei', Arial, sans-serif; background-color: #f8f9fa; text-align: center; padding: 50px; }}
                        .container {{ max-width: 600px; margin: 0 auto; background-color: white; padding: 30px; border-radius: 10px; box-shadow: 0 0 10px rgba(0,0,0,0.1); }}
                        h1 {{ color: #dc3545; }}
                        p {{ font-size: 18px; color: #343a40; line-height: 1.6; }}
                        .footer {{ margin-top: 30px; font-size: 14px; color: #6c757d; }}
                    </style>
                </head>
                <body>
                    <div class='container'>
                        <h1>系统使用期限已过</h1>
                        <p>您的系统使用许可已于 {expirationDate:yyyy年MM月dd日} 到期。</p>
                        <p>请联系系统管理员续期或获取新的访问权限。</p>
                        <div class='footer'>
                            &copy; {DateTime.Now.Year} 仓库管理系统 - 所有权利保留
                        </div>
                    </div>
                </body>
                </html>");
                return;
            }
        }
    }
    
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// 添加认证中间件（必须在UseRouting之后）
app.UseMiddleware<AuthenticationMiddleware>();

// 添加性能监控中间件
app.UseMiddleware<PerformanceMiddleware>();

// 添加认证中间件
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<TcpHub>("/tcpHub");

app.MapHub<TcpClientHub>("/tcpClientHub");

app.MapHub<SignalHub>("/signalHub");



app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();

// 类型声明放在顶级语句之后
public class MessageCache
{
    private static readonly ConcurrentDictionary<string, DateTime> _cache = new();
    private const int DuplicateTimeWindowMinutes = 3;

    public static bool ShouldLog(string message)
    {
        var now = DateTime.Now;
        if (_cache.TryGetValue(message, out var lastTime))
        {
            if ((now - lastTime).TotalMinutes < DuplicateTimeWindowMinutes)
            {
                return false;
            }
        }
        _cache.AddOrUpdate(message, now, (_, _) => now);
        return true;
    }

    public static void Cleanup()
    {
        var now = DateTime.Now;
        var expiredMessages = _cache.Where(x => (now - x.Value).TotalMinutes >= DuplicateTimeWindowMinutes)
                                  .Select(x => x.Key)
                                  .ToList();
        foreach (var message in expiredMessages)
        {
            _cache.TryRemove(message, out _);
        }
    }
}

public class CacheCleanupService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            MessageCache.Cleanup();
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
