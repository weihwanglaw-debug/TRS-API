using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class Participant
{
    public int ParticipantId { get; set; }

    public string Name { get; set; } = null!;

    public DateOnly DateOfBirth { get; set; }

    public string? Gender { get; set; }

    public string? SbaId { get; set; }

    public string? ShirtSize { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<EventParticipant> EventParticipants { get; set; } = new List<EventParticipant>();
}
