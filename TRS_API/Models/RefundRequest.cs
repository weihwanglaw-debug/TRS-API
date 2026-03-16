using System.ComponentModel.DataAnnotations;

namespace TRS_API.Models
{
    public class RefundRequest
    {
        [Required]
        public int PaymentId { get; set; }
        [Required]
        public long Amount { get; set; }
        public string? Reason { get; set; }
        public string? RequestedBy { get; set; }
    }
}
