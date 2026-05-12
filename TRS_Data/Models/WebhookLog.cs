using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class WebhookLog
{
    public int WebhookLogId { get; set; }

    public int? PaymentId { get; set; }       // FK → Payments — nullable (set after payment matched)

    public string PaymentGateway { get; set; } = null!;

    public string GatewayEventId { get; set; } = null!;

    public string EventType { get; set; } = null!;

    public string PayloadJson { get; set; } = null!;

    public string ProcessingStatus { get; set; } = "P";   // P|S|F|I

    public string? ErrorMessage { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ProcessedAt { get; set; }
}