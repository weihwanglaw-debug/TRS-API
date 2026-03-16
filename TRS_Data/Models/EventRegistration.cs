using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class EventRegistration
{
    public int RegistrationId { get; set; }

    public int EventId { get; set; }

    public string ContactEmail { get; set; } = null!;

    public string? ContactPhone { get; set; }

    public string ContactName { get; set; } = null!;

    public int NumberOfParticipants { get; set; }

    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = null!;

    public string RegistrationStatus { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public virtual ICollection<EventParticipant> EventParticipants { get; set; } = new List<EventParticipant>();

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
