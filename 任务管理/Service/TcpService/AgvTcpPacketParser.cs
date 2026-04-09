using System;
using System.Collections.Generic;
using System.Linq;
using WarehouseManagementSystem.Models;

namespace WarehouseManagementSystem.Service.TcpService
{
    internal static class AgvTcpPacketParser
    {
        public const int PacketLength = 100;

        private static readonly byte[] Header = { 0x4A, 0x54, 0x03, 0x29 };

        private static readonly Dictionary<ushort, (string Text, bool HasValue)> StatusTextMap = new()
        {
            [4] = ("Pos unknown", false),
            [5] = ("Outside safety zone", false),
            [11] = ("Cannot enter system", false),
            [12] = ("Trying to enter sys", false),
            [13] = ("Inserting to point", true),
            [14] = ("Moving to point", true),
            [15] = ("Moving to op point", true),
            [16] = ("Wait for operation", false),
            [17] = ("Manual mode", false),
            [18] = ("On point", true),
            [20] = ("Cannot enter system", false),
            [21] = ("CAN Bus Error", false),
            [22] = ("Semi mode", false),
            [42] = ("Fork height", true),
            [48] = ("StopWord #", true),
            [49] = ("FailCode #", true),
            [50] = ("Moving to stn", true),
            [51] = ("Loading at stn", true),
            [52] = ("Unloading at stn", true),
            [67] = ("Blocked by AGV", true),
            [75] = ("Blocked by AGV", true)
        };

        private static readonly Dictionary<int, string> StopInfo1Bits = new()
        {
            [0] = "正在等待货叉达到目标高度",
            [1] = "货叉光电检测到障碍物,请移开障碍物",
            [2] = "前方扫描仪检测到障碍物,请移开障碍物",
            [3] = "货叉编码器未同步,请手动下降至零位同步",
            [4] = "AGV启动延时中,请注意避让",
            [5] = "紧急停止",
            [6] = "停止按钮触发停止,请恢复停止按钮",
            [7] = "载货状态检测异常,请检查货叉载货弹片",
            [8] = "安全防撞触边触发,请检查",
            [9] = "急停通道错误,请检查安全继电器",
            [10] = "转向编码器未同步,请手动操作转向左或右",
            [11] = "紧急停止按钮触发停止,请恢复急停按钮并按下复位按钮",
            [12] = "侧边传感器检测到障碍物,请移开障碍物",
            [13] = "请求按下复位按钮,请按下复位按钮",
            [14] = "取卸货操作码错误,请检查操作码是否正确",
            [15] = "手动模式停止",
            [16] = "充电模式停止",
            [17] = "StopClamp",
            [18] = "接触器故障,请检查接触器是否粘连",
            [19] = "复位按钮触发停止,请松开复位按钮",
            [20] = "取卸货操作码错误,请检查操作码是否正确",
            [21] = "安全触边检测到障碍物,请移开障碍物",
            [22] = "AGV超出安全区停止,需要优化车辆参数",
            [23] = "安全状态停止",
            [24] = "StopVoice,Stopword",
            [25] = "StopHalt,Stopword",
            [26] = "StopLoadDroppedWhileUnloading",
            [27] = "AGV车轮打滑,请检查车轮",
            [28] = "站点停止",
            [29] = "正在通道自动门,等待中",
            [30] = "LiftChangeStop",
            [31] = "Spare32,Stopword"
        };

        private static readonly Dictionary<int, string> StopInfo2Bits = new()
        {
            [0] = "取货时检测到叉臂已有货物,请检查载货状态",
            [1] = "卸货时未检测到叉臂有货物,请检查载货状态",
            [2] = "货叉故障,请检查货叉动作",
            [3] = "读码失败,需要重新读码",
            [4] = "LS2000检测到障碍物,请移开障碍物",
            [5] = "读码错误,需要重新读码",
            [6] = "货叉正在动作,请等待",
            [7] = "斜扫描枪检测到障碍物,请移开障碍物",
            [8] = "货叉高度反馈错误,请检查货叉高度编码器",
            [9] = "转向编码器电池低电量,请联系人员更换电池",
            [10] = "等待LS2000升降动作,请等待",
            [11] = "斜扫描仪检测到障碍物,请移开障碍物",
            [12] = "后扫描仪检测到障碍物,请移开障碍物",
            [13] = "右侧扫描仪检测到障碍物,请移开障碍物",
            [14] = "左侧扫描仪检测到障碍物,请移开障碍物",
            [15] = "Spare48",
            [16] = "牵引驱动器故障,请检查牵引驱动器",
            [17] = "转向驱动器故障,请检查转向驱动器",
            [18] = "液压驱动器故障,请检查液压驱动器",
            [19] = "Spare52",
            [20] = "左侧扫描仪检测到障碍物,请移开障碍物",
            [21] = "右侧扫描仪检测到障碍物,请移开障碍物",
            [22] = "Spare55",
            [23] = "旁路模式触发停止,请关闭旁路模式",
            [24] = "Spare57",
            [25] = "Spare58",
            [26] = "Spare59",
            [27] = "Spare60",
            [28] = "Spare61",
            [29] = "Spare62",
            [30] = "Spare63",
            [31] = "Spare64"
        };

        public static bool HasHeaderAt(IReadOnlyList<byte> buffer, int index)
        {
            if (buffer.Count - index < Header.Length)
            {
                return false;
            }

            for (var i = 0; i < Header.Length; i++)
            {
                if (buffer[index + i] != Header[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static AgvTcpStatus Parse(byte[] packet, string remoteEndpoint)
        {
            if (packet.Length < PacketLength)
            {
                throw new ArgumentException($"报文长度不足，期望 {PacketLength} 字节，实际 {packet.Length} 字节。", nameof(packet));
            }

            var vehicleId = ReadUInt16(packet, 4);
            var statusText1Id = ReadUInt16(packet, 30);
            var statusText2Value = ReadUInt16(packet, 32);
            var stopInfo1 = ReadUInt32(packet, 34);
            var stopInfo2 = ReadUInt32(packet, 38);
            var stopInfo3 = ReadUInt32(packet, 78);
            var currentAngleRaw = ReadInt16(packet, 14);
            var steeringAngleRaw = ReadInt16(packet, 56);
            var operationMode = ReadUInt16(packet, 50);
            var runMode = ReadUInt16(packet, 58);
            var batteryTemp1 = ReadUInt16(packet, 70);
            var batteryTemp2 = ReadUInt16(packet, 72);
            var batteryTemp3 = ReadUInt16(packet, 74);
            var batteryTemp4 = ReadUInt16(packet, 76);

            return new AgvTcpStatus
            {
                AgvId = $"AGV{vehicleId:D2}",
                ClientEndpoint = remoteEndpoint,
                PacketHeader = BitConverter.ToString(packet, 0, 4).Replace("-", " "),
                PacketLength = packet.Length,
                VehicleId = vehicleId,
                CurrentX = ReadInt32(packet, 6),
                CurrentY = ReadInt32(packet, 10),
                CurrentAngleRaw = currentAngleRaw,
                CurrentAngle = currentAngleRaw / 100.0,
                BatteryLevel = ReadUInt16(packet, 16),
                ForkHeight = ReadUInt16(packet, 18),
                CurrentSpeed = ReadUInt16(packet, 20),
                TaskStatus = ReadUInt16(packet, 22),
                TaskId = ReadUInt16(packet, 24),
                LoadPoint = ReadUInt16(packet, 26),
                UnloadPoint = ReadUInt16(packet, 28),
                StatusText1Id = statusText1Id,
                StatusText2Value = statusText2Value,
                StatusTextDisplay = BuildStatusText(statusText1Id, statusText2Value),
                StopInfo1 = stopInfo1,
                StopInfo1Display = DescribeBits(stopInfo1, StopInfo1Bits),
                StopInfo2 = stopInfo2,
                StopInfo2Display = DescribeBits(stopInfo2, StopInfo2Bits),
                WifiSignal = ReadUInt16(packet, 42),
                RunTime = ReadUInt16(packet, 44),
                PowerOnTime = ReadUInt16(packet, 46),
                ChargeCount = ReadUInt16(packet, 48),
                OperationMode = operationMode,
                OperationModeDisplay = operationMode switch
                {
                    0 => "人工叉车",
                    1 => "AGV",
                    _ => $"未知({operationMode})"
                },
                DrivingSpeed = ReadUInt16(packet, 52),
                DrivingDistance = ReadUInt16(packet, 54),
                SteeringAngleRaw = steeringAngleRaw,
                SteeringAngle = steeringAngleRaw / 100.0,
                RunMode = runMode,
                RunModeDisplay = runMode switch
                {
                    1 => "上位机模式",
                    2 => "单机模式",
                    _ => $"未知({runMode})"
                },
                BatteryCurrentRaw = ReadUInt16(packet, 60),
                BatteryCurrent = ReadUInt16(packet, 60) / 1000.0,
                DischargeVoltageRaw = ReadUInt16(packet, 62),
                DischargeVoltage = ReadUInt16(packet, 62) / 100.0,
                ChargeVoltageRaw = ReadUInt16(packet, 64),
                ChargeVoltage = ReadUInt16(packet, 64) / 100.0,
                BatteryCapacity = ReadUInt16(packet, 66),
                RemainingCapacity = ReadUInt16(packet, 68),
                BatteryTemp1 = batteryTemp1,
                BatteryTemp2 = batteryTemp2,
                BatteryTemp3 = batteryTemp3,
                BatteryTemp4 = batteryTemp4,
                BatteryTempAverage = new[] { (double)batteryTemp1, batteryTemp2, batteryTemp3, batteryTemp4 }.Average(),
                StopInfo3 = stopInfo3,
                StopInfo3Display = stopInfo3 == 0 ? string.Empty : $"0x{stopInfo3:X8}",
                ReservedHex = packet.Length > 82 ? ToHex(packet[82..]) : string.Empty,
                RawHex = ToHex(packet),
                LastUpdateTime = DateTime.Now
            };
        }

        private static ushort ReadUInt16(byte[] buffer, int offset) =>
            (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

        private static short ReadInt16(byte[] buffer, int offset) =>
            unchecked((short)ReadUInt16(buffer, offset));

        private static int ReadInt32(byte[] buffer, int offset) =>
            (buffer[offset] << 24) |
            (buffer[offset + 1] << 16) |
            (buffer[offset + 2] << 8) |
            buffer[offset + 3];

        private static uint ReadUInt32(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) |
            ((uint)buffer[offset + 1] << 16) |
            ((uint)buffer[offset + 2] << 8) |
            buffer[offset + 3];

        private static string BuildStatusText(ushort statusId, ushort statusValue)
        {
            if (!StatusTextMap.TryGetValue(statusId, out var status))
            {
                return statusId == 0 ? string.Empty : $"未知状态({statusId}) 值:{statusValue}";
            }

            return status.HasValue ? $"{status.Text}: {statusValue}" : status.Text;
        }

        private static string DescribeBits(uint value, IReadOnlyDictionary<int, string> dictionary)
        {
            if (value == 0)
            {
                return string.Empty;
            }

            var descriptions = new List<string>();
            for (var bit = 0; bit < 32; bit++)
            {
                if ((value & (1u << bit)) == 0)
                {
                    continue;
                }

                descriptions.Add(dictionary.TryGetValue(bit, out var text)
                    ? $"Bit{bit}:{text}"
                    : $"Bit{bit}");
            }

            return string.Join(" | ", descriptions);
        }

        private static string ToHex(IEnumerable<byte> bytes) =>
            string.Join(" ", bytes.Select(static b => b.ToString("X2")));
    }
}
