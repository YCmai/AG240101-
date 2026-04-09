using System.Collections.Generic;

namespace WarehouseManagementSystem.Models
{
    /// <summary>
    /// 总库位状态查询响应
    /// </summary>
    public class BinStatusResponse
    {
        /// <summary>
        /// 返回状态
        /// </summary>
        public string status { get; set; }

        /// <summary>
        /// 锁定库位数量
        /// </summary>
        public int lockBinQty { get; set; }

        /// <summary>
        /// 已占用库位数量
        /// </summary>
        public int stockedBinQty { get; set; }

        /// <summary>
        /// 可用库位数量
        /// </summary>
        public int availableBinQty { get; set; }

        /// <summary>
        /// 锁定库位
        /// </summary>
        public List<string> lockBin { get; set; }

        /// <summary>
        /// 已占用库位
        /// </summary>
        public List<string> stockedBin { get; set; }

        /// <summary>
        /// 可用库位
        /// </summary>
        public List<string> availableBin { get; set; }

        /// <summary>
        /// 出货区
        /// </summary>
        public Dictionary<string, BinZoneInfo> dispatchZone { get; set; }

        /// <summary>
        /// 收货区
        /// </summary>
        public Dictionary<string, BinZoneInfo> receivingZone { get; set; }

        /// <summary>
        /// 成品交收区
        /// </summary>
        public Dictionary<string, BinZoneInfo> fgHandoverZone { get; set; }

        /// <summary>
        /// 物料交收区
        /// </summary>
        public Dictionary<string, BinZoneInfo> rmHandoverZone { get; set; }

        /// <summary>
        /// 中转区
        /// </summary>
        public Dictionary<string, BinZoneInfo> transitZone { get; set; }

        /// <summary>
        /// 货架区1
        /// </summary>
        public Dictionary<string, BinZoneInfo> rackingZone01 { get; set; }

        /// <summary>
        /// 货架区2
        /// </summary>
        public Dictionary<string, BinZoneInfo> rackingZone02 { get; set; }

        /// <summary>
        /// 货架区3
        /// </summary>
        public Dictionary<string, BinZoneInfo> rackingZone03 { get; set; }

        /// <summary>
        /// 结果提示
        /// </summary>
        public string message { get; set; }
    }

    /// <summary>
    /// 库位区域信息
    /// </summary>
    public class BinZoneInfo
    {
        /// <summary>
        /// 库位名称
        /// </summary>
        public string name { get; set; }

        /// <summary>
        /// 库位备注
        /// </summary>
        public string nodeRemark { get; set; }

        /// <summary>
        /// 物料编码
        /// </summary>
        public string materialCode { get; set; }

        /// <summary>
        /// 托盘ID
        /// </summary>
        public string palletID { get; set; }

        /// <summary>
        /// 重量
        /// </summary>
        public string weight { get; set; }

        /// <summary>
        /// 数量
        /// </summary>
        public string quantity { get; set; }

        /// <summary>
        /// 入库时间
        /// </summary>
        public string entryDate { get; set; }

        /// <summary>
        /// 分组
        /// </summary>
        public string group { get; set; }

        /// <summary>
        /// 举升高度
        /// </summary>
        public int liftingHeight { get; set; }

        /// <summary>
        /// 是否锁定
        /// </summary>
        public bool Lock { get; set; }

        /// <summary>
        /// 等待点
        /// </summary>
        public string wattingNode { get; set; }
    }
} 