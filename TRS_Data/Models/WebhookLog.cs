using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class WebhookLog
{
    public int WebhookLogId { get; set; }

    public string PaymentGateway { get; set; } = null!;

    public string GatewayEventId { get; set; } = null!;

    public string EventType { get; set; } = null!;

    public string PayloadJson { get; set; } = null!;

    public string ProcessingStatus { get; set; } = null!;

    public string? ErrorMessage { get; set; }

    public DateTime ReceivedAt { get; set; }

    public DateTime? ProcessedAt { get; set; }
}
