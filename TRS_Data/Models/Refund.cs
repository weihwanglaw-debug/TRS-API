using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class Refund
{
    public int RefundId { get; set; }

    public int PaymentId { get; set; }

    public string PaymentGateway { get; set; } = null!;

    public string? GatewayRefundId { get; set; }

    public decimal RefundAmount { get; set; }

    public string? RefundReason { get; set; }

    public string RefundStatus { get; set; } = null!;

    public string? RequestedBy { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }

    public virtual Payment Payment { get; set; } = null!;
}
