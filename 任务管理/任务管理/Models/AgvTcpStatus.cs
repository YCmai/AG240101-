using System;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV TCP 原始报文解析结果实体。
    /// 该实体用于承接小车上送的 100 字节 TCP 报文，并将每个字节段拆成可读字段。
    /// </summary>
    public class AgvTcpStatus
    {
        /// <summary>
        /// 系统内使用的AGV编号，例如 AGV20。
        /// </summary>
        public string AgvId { get; set; } = string.Empty;

        /// <summary>
        /// TCP客户端地址，通常为 IP:Port。
        /// </summary>
        public string ClientEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// 报文头，当前协议固定为 4A 54 03 29。
        /// </summary>
        public string PacketHeader { get; set; } = string.Empty;

        /// <summary>
        /// 整包报文长度，当前样例为 100 字节。
        /// </summary>
        public int PacketLength { get; set; }

        /// <summary>
        /// 小车ID，对应 Byte4-5。
        /// </summary>
        public ushort VehicleId { get; set; }

        /// <summary>
        /// AGV当前X轴坐标，对应 Byte6-9。
        /// </summary>
        public int CurrentX { get; set; }

        /// <summary>
        /// AGV当前Y轴坐标，对应 Byte10-13。
        /// </summary>
        public int CurrentY { get; set; }

        /// <summary>
        /// AGV当前角度原始值，对应 Byte14-15。
        /// </summary>
        public short CurrentAngleRaw { get; set; }

        /// <summary>
        /// AGV当前角度换算值。
        /// </summary>
        public double CurrentAngle { get; set; }

        /// <summary>
        /// AGV当前电量，对应 Byte16-17。
        /// </summary>
        public ushort BatteryLevel { get; set; }

        /// <summary>
        /// AGV货叉高度，对应 Byte18-19。
        /// </summary>
        public ushort ForkHeight { get; set; }

        /// <summary>
        /// AGV当前行驶速度，对应 Byte20-21。
        /// </summary>
        public ushort CurrentSpeed { get; set; }

        /// <summary>
        /// 任务状态，对应 Byte22-23。
        /// </summary>
        public ushort TaskStatus { get; set; }

        /// <summary>
        /// 任务ID，对应 Byte24-25。
        /// </summary>
        public ushort TaskId { get; set; }

        /// <summary>
        /// 上料点，对应 Byte26-27。
        /// </summary>
        public ushort LoadPoint { get; set; }

        /// <summary>
        /// 下料点，对应 Byte28-29。
        /// </summary>
        public ushort UnloadPoint { get; set; }

        /// <summary>
        /// 运行状态第一段文本ID，对应 Byte30-31。
        /// </summary>
        public ushort StatusText1Id { get; set; }

        /// <summary>
        /// 运行状态第二段数值，对应 Byte32-33。
        /// </summary>
        public ushort StatusText2Value { get; set; }

        /// <summary>
        /// 运行状态中文显示值，按状态ID映射后拼接而成。
        /// </summary>
        public string StatusTextDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 停止信息1原始位值，对应 Byte34-37。
        /// </summary>
        public uint StopInfo1 { get; set; }

        /// <summary>
        /// 停止信息1中文解释，按位展开后拼接而成。
        /// </summary>
        public string StopInfo1Display { get; set; } = string.Empty;

        /// <summary>
        /// 停止信息2原始位值，对应 Byte38-41。
        /// </summary>
        public uint StopInfo2 { get; set; }

        /// <summary>
        /// 停止信息2中文解释，按位展开后拼接而成。
        /// </summary>
        public string StopInfo2Display { get; set; } = string.Empty;

        /// <summary>
        /// 无线信号强度，对应 Byte42-43。
        /// </summary>
        public ushort WifiSignal { get; set; }

        /// <summary>
        /// AGV运行时间，对应 Byte44-45。
        /// </summary>
        public ushort RunTime { get; set; }

        /// <summary>
        /// AGV通电时间，对应 Byte46-47。
        /// </summary>
        public ushort PowerOnTime { get; set; }

        /// <summary>
        /// 充电次数，对应 Byte48-49。
        /// </summary>
        public ushort ChargeCount { get; set; }

        /// <summary>
        /// AGV操作模式，对应 Byte50-51，0=人工叉车，1=AGV。
        /// </summary>
        public ushort OperationMode { get; set; }

        /// <summary>
        /// AGV操作模式文本说明。
        /// </summary>
        public string OperationModeDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 行驶速度，对应 Byte52-53。
        /// </summary>
        public ushort DrivingSpeed { get; set; }

        /// <summary>
        /// 行驶距离，对应 Byte54-55。
        /// </summary>
        public ushort DrivingDistance { get; set; }

        /// <summary>
        /// 转向角原始值，对应 Byte56-57。
        /// </summary>
        public short SteeringAngleRaw { get; set; }

        /// <summary>
        /// 转向角换算值。
        /// </summary>
        public double SteeringAngle { get; set; }

        /// <summary>
        /// AGV运行模式，对应 Byte58-59。
        /// </summary>
        public ushort RunMode { get; set; }

        /// <summary>
        /// AGV运行模式文本说明。
        /// </summary>
        public string RunModeDisplay { get; set; } = string.Empty;

        /// <summary>
        /// 电池电流原始值，对应 Byte60-61。
        /// </summary>
        public ushort BatteryCurrentRaw { get; set; }

        /// <summary>
        /// 电池电流换算值。
        /// </summary>
        public double BatteryCurrent { get; set; }

        /// <summary>
        /// 放电电压原始值，对应 Byte62-63。
        /// </summary>
        public ushort DischargeVoltageRaw { get; set; }

        /// <summary>
        /// 放电电压换算值。
        /// </summary>
        public double DischargeVoltage { get; set; }

        /// <summary>
        /// 充电电压原始值，对应 Byte64-65。
        /// </summary>
        public ushort ChargeVoltageRaw { get; set; }

        /// <summary>
        /// 充电电压换算值。
        /// </summary>
        public double ChargeVoltage { get; set; }

        /// <summary>
        /// 电池总容量，对应 Byte66-67。
        /// </summary>
        public ushort BatteryCapacity { get; set; }

        /// <summary>
        /// 电池剩余容量，对应 Byte68-69。
        /// </summary>
        public ushort RemainingCapacity { get; set; }

        /// <summary>
        /// 电池温度传感器1当前温度，对应 Byte70-71。
        /// </summary>
        public ushort BatteryTemp1 { get; set; }

        /// <summary>
        /// 电池温度传感器2当前温度，对应 Byte72-73。
        /// </summary>
        public ushort BatteryTemp2 { get; set; }

        /// <summary>
        /// 电池温度传感器3当前温度，对应 Byte74-75。
        /// </summary>
        public ushort BatteryTemp3 { get; set; }

        /// <summary>
        /// 电池温度传感器4当前温度，对应 Byte76-77。
        /// </summary>
        public ushort BatteryTemp4 { get; set; }

        /// <summary>
        /// 4路电池温度平均值。
        /// </summary>
        public double BatteryTempAverage { get; set; }

        /// <summary>
        /// 停止信息3原始位值，对应 Byte78-81。
        /// </summary>
        public uint StopInfo3 { get; set; }

        /// <summary>
        /// 停止信息3显示值。
        /// </summary>
        public string StopInfo3Display { get; set; } = string.Empty;

        /// <summary>
        /// 预留字节区(Byte82-99)的十六进制文本。
        /// </summary>
        public string ReservedHex { get; set; } = string.Empty;

        /// <summary>
        /// 整包原始HEX字符串。
        /// </summary>
        public string RawHex { get; set; } = string.Empty;

        /// <summary>
        /// 最后一次收到该车报文的时间。
        /// </summary>
        public DateTime LastUpdateTime { get; set; }
    }
}
