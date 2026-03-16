using System.ComponentModel.DataAnnotations;

namespace TRS_API.Models
{
    public class PaymentRequest
    {
        [Required]
        public int RegistrationId { get; set; }
        public string? PaymentMethod { get; set; }
        public string? SuccessUrl { get; set; }
        public string? CancelUrl { get; set; }
    }
}
