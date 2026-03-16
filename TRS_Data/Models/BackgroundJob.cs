using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class BackgroundJob
{
    public long JobId { get; set; }

    public string JobType { get; set; } = null!;

    public string ReferenceType { get; set; } = null!;

    public long ReferenceId { get; set; }

    public string? PayloadJson { get; set; }

    public string JobStatus { get; set; } = null!;

    public int RetryCount { get; set; }

    public int MaxRetry { get; set; }

    public string? LastError { get; set; }

    public DateTime ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
