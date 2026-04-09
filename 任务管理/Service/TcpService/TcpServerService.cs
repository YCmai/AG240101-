using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service.TcpService
{
    public class TcpServerService : BackgroundService
    {
        private readonly ILogger<TcpServerService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private TcpListener _listener = null!;
        private IPAddress _listenAddress = IPAddress.Any;
        private int _listenPort;

        public TcpServerService(ILogger<TcpServerService> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _configuration = configuration;
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            var portFromConfig = _configuration.GetValue<int?>("TcpServer:Port");
            _listenPort = portFromConfig ?? 9000;
            var ipString = _configuration.GetValue<string>("TcpServer:IPAddress");
            if (string.IsNullOrWhiteSpace(ipString) || ipString == "0.0.0.0")
            {
                _listenAddress = IPAddress.Any;
            }
            else if (!IPAddress.TryParse(ipString, out _listenAddress))
            {
                _logger.LogWarning("TcpServer:IPAddress 配置无效，回退到 Any");
                _listenAddress = IPAddress.Any;
            }

            _listener = new TcpListener(_listenAddress, _listenPort);
            _listener.Start();
            _logger.LogInformation("TcpServerService 启动，监听 {Address}:{Port}", _listenAddress, _listenPort);

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(stoppingToken);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TcpServerService 在 Accept 过程中发生异常");
            }
            finally
            {
                try { _listener?.Stop(); } catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("客户端已连接: {Endpoint}", remoteEndpoint);

            try
            {
                using var networkStream = client.GetStream();
                var buffer = new byte[8192];
                var packetBuffer = new List<byte>(AgvTcpPacketParser.PacketLength * 4);

                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < bytesRead; i++)
                    {
                        packetBuffer.Add(buffer[i]);
                    }

                    while (TryExtractPacket(packetBuffer, out var packet))
                    {
                        try
                        {
                            var status = AgvTcpPacketParser.Parse(packet, remoteEndpoint);
                            await SaveAgvStatusAsync(status);
                            _logger.LogInformation("已更新 AGV TCP 状态: {Endpoint} {AgvId} X={X} Y={Y} 电量={BatteryLevel}", remoteEndpoint, status.AgvId, status.CurrentX, status.CurrentY, status.BatteryLevel);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "解析或保存 AGV TCP 报文失败，客户端: {Endpoint}", remoteEndpoint);
                            await SaveRawMessageAsync(remoteEndpoint, packet);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理客户端 {Endpoint} 时发生异常", remoteEndpoint);
            }
            finally
            {
                try { client.Close(); } catch { }
                _logger.LogInformation("客户端已断开: {Endpoint}", remoteEndpoint);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            try { _listener?.Stop(); } catch { }
            _logger.LogInformation("TcpServerService 停止");
            return base.StopAsync(cancellationToken);
        }

        private async Task SaveAgvStatusAsync(AgvTcpStatus status)
        {
            using var scope = _scopeFactory.CreateScope();
            var conn = scope.ServiceProvider.GetRequiredService<System.Data.IDbConnection>();

            var agvInfoSql = @"
IF EXISTS (SELECT 1 FROM AGV_Info WHERE AgvId = @AgvId)
BEGIN
    UPDATE AGV_Info
    SET AgvName = @AgvName,
        AgvType = @AgvType,
        Status = @Status,
        X = @X,
        Y = @Y,
        Angle = @Angle,
        BatteryLevel = @BatteryLevel,
        BatteryTemp = @BatteryTemp,
        ChargingStatus = @ChargingStatus,
        JackStatus = @JackStatus,
        LoadStatus = @LoadStatus,
        TotalRunTime = @TotalRunTime,
        TotalDistance = @TotalDistance,
        ChargeCount = @ChargeCount,
        BatteryCircleCount = @BatteryCircleCount,
        ClientEndpoint = @ClientEndpoint,
        PacketHeader = @PacketHeader,
        PacketLength = @PacketLength,
        VehicleId = @VehicleId,
        CurrentAngleRaw = @CurrentAngleRaw,
        ForkHeight = @ForkHeight,
        CurrentSpeed = @CurrentSpeed,
        TaskStatus = @TaskStatus,
        TaskId = @TaskId,
        LoadPoint = @LoadPoint,
        UnloadPoint = @UnloadPoint,
        StatusText1Id = @StatusText1Id,
        StatusText2Value = @StatusText2Value,
        StatusTextDisplay = @StatusTextDisplay,
        StopInfo1 = @StopInfo1,
        StopInfo1Display = @StopInfo1Display,
        StopInfo2 = @StopInfo2,
        StopInfo2Display = @StopInfo2Display,
        WifiSignal = @WifiSignal,
        RunTime = @RunTime,
        PowerOnTime = @PowerOnTime,
        OperationMode = @OperationMode,
        OperationModeDisplay = @OperationModeDisplay,
        DrivingSpeed = @DrivingSpeed,
        DrivingDistance = @DrivingDistance,
        SteeringAngleRaw = @SteeringAngleRaw,
        SteeringAngle = @SteeringAngle,
        RunMode = @RunMode,
        RunModeDisplay = @RunModeDisplay,
        BatteryCurrentRaw = @BatteryCurrentRaw,
        BatteryCurrent = @BatteryCurrent,
        DischargeVoltageRaw = @DischargeVoltageRaw,
        DischargeVoltage = @DischargeVoltage,
        ChargeVoltageRaw = @ChargeVoltageRaw,
        ChargeVoltage = @ChargeVoltage,
        BatteryCapacity = @BatteryCapacity,
        RemainingCapacity = @RemainingCapacity,
        BatteryTemp1 = @BatteryTemp1,
        BatteryTemp2 = @BatteryTemp2,
        BatteryTemp3 = @BatteryTemp3,
        BatteryTemp4 = @BatteryTemp4,
        BatteryTempAverage = @BatteryTempAverage,
        StopInfo3 = @StopInfo3,
        StopInfo3Display = @StopInfo3Display,
        ReservedHex = @ReservedHex,
        RawHex = @RawHex,
        LastUpdateTime = @LastUpdateTime,
        Remarks = @Remarks
    WHERE AgvId = @AgvId
END
ELSE
BEGIN
    INSERT INTO AGV_Info
    (
        AgvId, AgvName, AgvType, Status, X, Y, Angle, BatteryLevel, BatteryTemp, ChargingStatus,
        JackStatus, LoadStatus, TotalRunTime, TotalDistance, ChargeCount, BatteryCircleCount,
        ClientEndpoint, PacketHeader, PacketLength, VehicleId, CurrentAngleRaw, ForkHeight, CurrentSpeed,
        TaskStatus, TaskId, LoadPoint, UnloadPoint, StatusText1Id, StatusText2Value, StatusTextDisplay,
        StopInfo1, StopInfo1Display, StopInfo2, StopInfo2Display, WifiSignal, RunTime, PowerOnTime,
        OperationMode, OperationModeDisplay, DrivingSpeed, DrivingDistance, SteeringAngleRaw, SteeringAngle,
        RunMode, RunModeDisplay, BatteryCurrentRaw, BatteryCurrent, DischargeVoltageRaw, DischargeVoltage,
        ChargeVoltageRaw, ChargeVoltage, BatteryCapacity, RemainingCapacity, BatteryTemp1, BatteryTemp2,
        BatteryTemp3, BatteryTemp4, BatteryTempAverage, StopInfo3, StopInfo3Display, ReservedHex, RawHex,
        LastUpdateTime, Remarks
    )
    VALUES
    (
        @AgvId, @AgvName, @AgvType, @Status, @X, @Y, @Angle, @BatteryLevel, @BatteryTemp, @ChargingStatus,
        @JackStatus, @LoadStatus, @TotalRunTime, @TotalDistance, @ChargeCount, @BatteryCircleCount,
        @ClientEndpoint, @PacketHeader, @PacketLength, @VehicleId, @CurrentAngleRaw, @ForkHeight, @CurrentSpeed,
        @TaskStatus, @TaskId, @LoadPoint, @UnloadPoint, @StatusText1Id, @StatusText2Value, @StatusTextDisplay,
        @StopInfo1, @StopInfo1Display, @StopInfo2, @StopInfo2Display, @WifiSignal, @RunTime, @PowerOnTime,
        @OperationMode, @OperationModeDisplay, @DrivingSpeed, @DrivingDistance, @SteeringAngleRaw, @SteeringAngle,
        @RunMode, @RunModeDisplay, @BatteryCurrentRaw, @BatteryCurrent, @DischargeVoltageRaw, @DischargeVoltage,
        @ChargeVoltageRaw, @ChargeVoltage, @BatteryCapacity, @RemainingCapacity, @BatteryTemp1, @BatteryTemp2,
        @BatteryTemp3, @BatteryTemp4, @BatteryTempAverage, @StopInfo3, @StopInfo3Display, @ReservedHex, @RawHex,
        @LastUpdateTime, @Remarks
    )
END";

            await conn.ExecuteAsync(agvInfoSql, new
            {
                status.AgvId,
                AgvName = status.AgvId,
                AgvType = "TCP",
                Status = (int)status.TaskStatus,
                X = (double)status.CurrentX,
                Y = (double)status.CurrentY,
                Angle = status.CurrentAngle,
                BatteryLevel = (double)status.BatteryLevel,
                BatteryTemp = status.BatteryTempAverage,
                ChargingStatus = status.ChargeVoltage > 0 ? 1 : 0,
                JackStatus = status.ForkHeight > 0 ? 1 : 0,
                LoadStatus = status.StatusText1Id == 51 ? 1 : 0,
                TotalRunTime = (double)status.RunTime,
                TotalDistance = (double)status.DrivingDistance,
                ChargeCount = (int)status.ChargeCount,
                BatteryCircleCount = (double)status.RemainingCapacity,
                status.ClientEndpoint,
                status.PacketHeader,
                status.PacketLength,
                VehicleId = (int)status.VehicleId,
                status.CurrentAngleRaw,
                ForkHeight = (int)status.ForkHeight,
                CurrentSpeed = (int)status.CurrentSpeed,
                TaskStatus = (int)status.TaskStatus,
                TaskId = (int)status.TaskId,
                LoadPoint = (int)status.LoadPoint,
                UnloadPoint = (int)status.UnloadPoint,
                StatusText1Id = (int)status.StatusText1Id,
                StatusText2Value = (int)status.StatusText2Value,
                status.StatusTextDisplay,
                StopInfo1 = (long)status.StopInfo1,
                status.StopInfo1Display,
                StopInfo2 = (long)status.StopInfo2,
                status.StopInfo2Display,
                WifiSignal = (int)status.WifiSignal,
                RunTime = (int)status.RunTime,
                PowerOnTime = (int)status.PowerOnTime,
                OperationMode = (int)status.OperationMode,
                status.OperationModeDisplay,
                DrivingSpeed = (int)status.DrivingSpeed,
                DrivingDistance = (int)status.DrivingDistance,
                status.SteeringAngleRaw,
                status.SteeringAngle,
                RunMode = (int)status.RunMode,
                status.RunModeDisplay,
                BatteryCurrentRaw = (int)status.BatteryCurrentRaw,
                status.BatteryCurrent,
                DischargeVoltageRaw = (int)status.DischargeVoltageRaw,
                status.DischargeVoltage,
                ChargeVoltageRaw = (int)status.ChargeVoltageRaw,
                status.ChargeVoltage,
                BatteryCapacity = (int)status.BatteryCapacity,
                RemainingCapacity = (int)status.RemainingCapacity,
                BatteryTemp1 = (int)status.BatteryTemp1,
                BatteryTemp2 = (int)status.BatteryTemp2,
                BatteryTemp3 = (int)status.BatteryTemp3,
                BatteryTemp4 = (int)status.BatteryTemp4,
                status.BatteryTempAverage,
                StopInfo3 = (long)status.StopInfo3,
                status.StopInfo3Display,
                status.ReservedHex,
                status.RawHex,
                status.LastUpdateTime,
                Remarks = string.IsNullOrWhiteSpace(status.StatusTextDisplay) ? status.StopInfo1Display : status.StatusTextDisplay
            });
        }

        private async Task SaveRawMessageAsync(string remoteEndpoint, byte[] packet)
        {
            using var scope = _scopeFactory.CreateScope();
            var dataService = scope.ServiceProvider.GetService<IClientDataService>();
            if (dataService != null)
            {
                await dataService.AddClientMessageAsync(remoteEndpoint, BitConverter.ToString(packet).Replace("-", " "));
            }
        }

        private static bool TryExtractPacket(List<byte> buffer, out byte[] packet)
        {
            packet = Array.Empty<byte>();

            if (buffer.Count < 4)
            {
                return false;
            }

            var headerIndex = -1;
            for (var i = 0; i <= buffer.Count - 4; i++)
            {
                if (!AgvTcpPacketParser.HasHeaderAt(buffer, i))
                {
                    continue;
                }

                headerIndex = i;
                break;
            }

            if (headerIndex < 0)
            {
                if (buffer.Count > AgvTcpPacketParser.PacketLength * 4)
                {
                    buffer.RemoveRange(0, buffer.Count - 4);
                }

                return false;
            }

            if (headerIndex > 0)
            {
                buffer.RemoveRange(0, headerIndex);
            }

            if (buffer.Count < AgvTcpPacketParser.PacketLength)
            {
                return false;
            }

            packet = buffer.GetRange(0, AgvTcpPacketParser.PacketLength).ToArray();
            buffer.RemoveRange(0, AgvTcpPacketParser.PacketLength);
            return true;
        }
    }
}
