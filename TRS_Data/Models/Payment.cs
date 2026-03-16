using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class Payment
{
    public int PaymentId { get; set; }

    public int RegistrationId { get; set; }

    public string PaymentGateway { get; set; } = null!;

    public string? GatewaySessionId { get; set; }

    public string? GatewayPaymentId { get; set; }

    public string? GatewayChargeId { get; set; }

    public string PaymentMethod { get; set; } = null!;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = null!;

    public string PaymentStatus { get; set; } = null!;

    public string? ReceiptNumber { get; set; }

    public string? PaymentGatewayResponse { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public virtual ICollection<Refund> Refunds { get; set; } = new List<Refund>();

    public virtual EventRegistration Registration { get; set; } = null!;
}
