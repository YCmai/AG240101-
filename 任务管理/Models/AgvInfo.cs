using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// AGV信息实体
    /// </summary>
    [Table("AGV_Info")]
    public class AgvInfo
    {
        /// <summary>
        /// 主键ID
        /// </summary>
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// AGV编号
        /// </summary>
        [Required]
        [StringLength(50)]
        public string AgvId { get; set; }

        /// <summary>
        /// AGV名称
        /// </summary>
        [StringLength(100)]
        public string AgvName { get; set; }

        /// <summary>
        /// AGV类型
        /// </summary>
        [StringLength(50)]
        public string AgvType { get; set; }

        /// <summary>
        /// AGV状态 (0:离线, 1:在线空闲, 2:在线任务中, 3:故障)
        /// </summary>
        public int Status { get; set; }

        /// <summary>
        /// 定位x坐标
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// 定位y坐标
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// 定位角度
        /// </summary>
        public float Angle { get; set; }

        /// <summary>
        /// 电量
        /// </summary>
        public float BatteryLevel { get; set; }

        /// <summary>
        /// 电池温度
        /// </summary>
        public float BatteryTemp { get; set; }

        /// <summary>
        /// 充电状态 (0:未充电, 1:充电中)
        /// </summary>
        public int ChargingStatus { get; set; }

        /// <summary>
        /// 顶升状态 (0:下降/底状态, 1:顶升状态)
        /// </summary>
        public int JackStatus { get; set; }

        /// <summary>
        /// 载货状态 (0:未载货, 1:载货中)
        /// </summary>
        public int LoadStatus { get; set; }

        /// <summary>
        /// 运行总时间(分钟)
        /// </summary>
        public double TotalRunTime { get; set; }

        /// <summary>
        /// 运行总里程(米)
        /// </summary>
        public double TotalDistance { get; set; }

        /// <summary>
        /// 充电次数
        /// </summary>
        public int ChargeCount { get; set; }

        /// <summary>
        /// 电池循环次数
        /// </summary>
        public float BatteryCircleCount { get; set; }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdateTime { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [StringLength(500)]
        public string Remarks { get; set; }

        /// <summary>
        /// TCP客户端地址，格式通常为 IP:Port
        /// </summary>
        public string ClientEndpoint { get; set; }

        /// <summary>
        /// TCP报文头，当前固定为 4A 54 03 29
        /// </summary>
        public string PacketHeader { get; set; }

        /// <summary>
        /// TCP报文总长度，当前样例为固定100字节
        /// </summary>
        public int PacketLength { get; set; }

        /// <summary>
        /// 小车ID，对应报文 Byte4-5
        /// </summary>
        public int VehicleId { get; set; }

        /// <summary>
        /// 当前车体角度原始值，对应报文 Byte14-15
        /// </summary>
        public int CurrentAngleRaw { get; set; }

        /// <summary>
        /// AGV小车货叉高度，对应报文 Byte18-19
        /// </summary>
        public int ForkHeight { get; set; }

        /// <summary>
        /// AGV当前行驶速度，对应报文 Byte20-21
        /// </summary>
        public int CurrentSpeed { get; set; }

        /// <summary>
        /// 任务状态，对应报文 Byte22-23
        /// </summary>
        public int TaskStatus { get; set; }

        /// <summary>
        /// 当前任务ID，对应报文 Byte24-25
        /// </summary>
        public int TaskId { get; set; }

        /// <summary>
        /// 上料点，对应报文 Byte26-27
        /// </summary>
        public int LoadPoint { get; set; }

        /// <summary>
        /// 下料点，对应报文 Byte28-29
        /// </summary>
        public int UnloadPoint { get; set; }

        /// <summary>
        /// AGV运行状态第一段文本ID，对应报文 Byte30-31
        /// </summary>
        public int StatusText1Id { get; set; }

        /// <summary>
        /// AGV运行状态第二段数值，对应报文 Byte32-33
        /// </summary>
        public int StatusText2Value { get; set; }

        /// <summary>
        /// 拼接后的运行状态文本，例如“Moving to point: 32”
        /// </summary>
        [StringLength(200)]
        public string StatusTextDisplay { get; set; }

        /// <summary>
        /// AGV停机信息1原始位值，对应报文 Byte34-37
        /// </summary>
        public long StopInfo1 { get; set; }

        /// <summary>
        /// AGV停机信息1的中文解释
        /// </summary>
        public string StopInfo1Display { get; set; }

        /// <summary>
        /// AGV停机信息2原始位值，对应报文 Byte38-41
        /// </summary>
        public long StopInfo2 { get; set; }

        /// <summary>
        /// AGV停机信息2的中文解释
        /// </summary>
        public string StopInfo2Display { get; set; }

        /// <summary>
        /// AGV当前无线信号强度，对应报文 Byte42-43
        /// </summary>
        public int WifiSignal { get; set; }

        /// <summary>
        /// AGV运行时间，对应报文 Byte44-45
        /// </summary>
        public int RunTime { get; set; }

        /// <summary>
        /// AGV通电时间，对应报文 Byte46-47
        /// </summary>
        public int PowerOnTime { get; set; }

        /// <summary>
        /// AGV操作模式，对应报文 Byte50-51，0=人工叉车，1=AGV
        /// </summary>
        public int OperationMode { get; set; }

        /// <summary>
        /// AGV操作模式文本说明
        /// </summary>
        [StringLength(50)]
        public string OperationModeDisplay { get; set; }

        /// <summary>
        /// AGV行驶速度，对应报文 Byte52-53
        /// </summary>
        public int DrivingSpeed { get; set; }

        /// <summary>
        /// AGV行驶距离，对应报文 Byte54-55
        /// </summary>
        public int DrivingDistance { get; set; }

        /// <summary>
        /// AGV转向角度原始值，对应报文 Byte56-57
        /// </summary>
        public int SteeringAngleRaw { get; set; }

        /// <summary>
        /// AGV转向角度换算值
        /// </summary>
        public double SteeringAngle { get; set; }

        /// <summary>
        /// AGV运行模式，对应报文 Byte58-59
        /// </summary>
        public int RunMode { get; set; }

        /// <summary>
        /// AGV运行模式文本说明，例如上位机模式、单机模式
        /// </summary>
        [StringLength(50)]
        public string RunModeDisplay { get; set; }

        /// <summary>
        /// AGV当前电池电流原始值，对应报文 Byte60-61
        /// </summary>
        public int BatteryCurrentRaw { get; set; }

        /// <summary>
        /// AGV当前电池电流换算值
        /// </summary>
        public double BatteryCurrent { get; set; }

        /// <summary>
        /// AGV当前放电电压原始值，对应报文 Byte62-63
        /// </summary>
        public int DischargeVoltageRaw { get; set; }

        /// <summary>
        /// AGV当前放电电压换算值
        /// </summary>
        public double DischargeVoltage { get; set; }

        /// <summary>
        /// AGV当前充电电压原始值，对应报文 Byte64-65
        /// </summary>
        public int ChargeVoltageRaw { get; set; }

        /// <summary>
        /// AGV当前充电电压换算值
        /// </summary>
        public double ChargeVoltage { get; set; }

        /// <summary>
        /// AGV电池总容量，对应报文 Byte66-67
        /// </summary>
        public int BatteryCapacity { get; set; }

        /// <summary>
        /// AGV电池剩余容量，对应报文 Byte68-69
        /// </summary>
        public int RemainingCapacity { get; set; }

        /// <summary>
        /// AGV电池温度传感器1当前温度，对应报文 Byte70-71
        /// </summary>
        public int BatteryTemp1 { get; set; }

        /// <summary>
        /// AGV电池温度传感器2当前温度，对应报文 Byte72-73
        /// </summary>
        public int BatteryTemp2 { get; set; }

        /// <summary>
        /// AGV电池温度传感器3当前温度，对应报文 Byte74-75
        /// </summary>
        public int BatteryTemp3 { get; set; }

        /// <summary>
        /// AGV电池温度传感器4当前温度，对应报文 Byte76-77
        /// </summary>
        public int BatteryTemp4 { get; set; }

        /// <summary>
        /// 4路电池温度平均值
        /// </summary>
        public double BatteryTempAverage { get; set; }

        /// <summary>
        /// AGV停机信息3原始位值，对应报文 Byte78-81
        /// </summary>
        public long StopInfo3 { get; set; }

        /// <summary>
        /// AGV停机信息3的解释文本
        /// </summary>
        public string StopInfo3Display { get; set; }

        /// <summary>
        /// 保留字节区(Byte82-99)的十六进制字符串
        /// </summary>
        [StringLength(200)]
        public string ReservedHex { get; set; }

        /// <summary>
        /// 完整原始TCP报文的十六进制字符串
        /// </summary>
        public string RawHex { get; set; }
    }
} 
