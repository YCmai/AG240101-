using System.ComponentModel.DataAnnotations;

namespace WarehouseManagementSystem.Models
{
    public class BinUpdateRequest
    {
        [Required]
        public string binId { get; set; }
        [Required]
        public int binStatus { get; set; } // 0: 清空, 1: 占位
    }
} 