using System;
using System.Collections.Generic;
namespace TRS_Data.Models;
public partial class EventRegistration
{
    public int RegistrationId { get; set; }
    public int EventId { get; set; }
    public string EventName { get; set; } = null!;     // snapshotted
    public DateTime SubmittedAt { get; set; }
    public string RegStatus { get; set; } = "Pending"; // Pending|Confirmed|Cancelled
    public string ContactName { get; set; } = null!;
    public string ContactEmail { get; set; } = null!;
    public string? ContactPhone { get; set; }
    public DateTime? UpdatedAt { get; set; }
    // Legacy fields kept for PaymentController compat
    public int NumberOfParticipants { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "SGD";
    public string RegistrationStatus { get; set; } = "P";
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public virtual Event? Event { get; set; }
    public virtual ICollection<ParticipantGroup> ParticipantGroups { get; set; } = new List<ParticipantGroup>();
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<EventParticipant> EventParticipants { get; set; } = new List<EventParticipant>();
}
