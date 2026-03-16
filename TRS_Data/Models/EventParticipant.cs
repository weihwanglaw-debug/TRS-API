using System;
using System.Collections.Generic;

namespace TRS_Data.Models;

public partial class EventParticipant
{
    public int EventParticipantId { get; set; }

    public int RegistrationId { get; set; }

    public int ParticipantId { get; set; }

    public bool CheckedIn { get; set; }

    public DateTime? CheckInTime { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual Participant Participant { get; set; } = null!;

    public virtual EventRegistration Registration { get; set; } = null!;
}
