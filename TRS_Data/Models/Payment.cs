using System;
using System.Collections.Generic;
namespace TRS_Data.Models;
public partial class Payment
{
    public int PaymentId { get; set; }
    public int RegistrationId { get; set; }
    public int EventId { get; set; }                   // denormalised
    public string PaymentGateway { get; set; } = null!;
    public string? GatewaySessionId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public string? GatewayChargeId { get; set; }
    public string PaymentMethod { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "SGD";
    public string PaymentStatus { get; set; } = "P";   // P|S|PR|FR|F|X
    public string? ReceiptNumber { get; set; }
    public string? PaymentGatewayResponse { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public virtual EventRegistration Registration { get; set; } = null!;
    public virtual ICollection<PaymentItem> Items { get; set; } = new List<PaymentItem>();
    public virtual ICollection<Refund> Refunds { get; set; } = new List<Refund>();
}
