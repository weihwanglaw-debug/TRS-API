using Microsoft.EntityFrameworkCore;
namespace TRS_Data.Models;

public partial class TRSDbContext : DbContext
{
    public TRSDbContext() { }
    public TRSDbContext(DbContextOptions<TRSDbContext> options) : base(options) { }

    // ── DbSets ────────────────────────────────────────────────────────────────
    public virtual DbSet<SystemConfig>               SystemConfigs              { get; set; }
    public virtual DbSet<AdminUser>                  AdminUsers                 { get; set; }
    public virtual DbSet<Event>                      Events                     { get; set; }
    public virtual DbSet<EventGalleryImage>          EventGalleryImages         { get; set; }
    public virtual DbSet<TrsProgram>                 Programs                   { get; set; }
    public virtual DbSet<ProgramField>               ProgramFields              { get; set; }
    public virtual DbSet<ProgramCustomField>         ProgramCustomFields        { get; set; }
    public virtual DbSet<SbaRanking>                 SbaRankings                { get; set; }
    public virtual DbSet<EventRegistration>          EventRegistrations         { get; set; }
    public virtual DbSet<ParticipantGroup>           ParticipantGroups          { get; set; }
    public virtual DbSet<TrsParticipant>             TrsParticipants            { get; set; }
    public virtual DbSet<ParticipantCustomFieldValue> ParticipantCustomFieldValues { get; set; }
    public virtual DbSet<EventParticipant>           EventParticipants          { get; set; }
    public virtual DbSet<Participant>                Participants               { get; set; }
    public virtual DbSet<Payment>                    Payments                   { get; set; }
    public virtual DbSet<PaymentItem>                PaymentItems               { get; set; }
    public virtual DbSet<Refund>                     Refunds                    { get; set; }
    public virtual DbSet<Fixture>                    Fixtures                   { get; set; }
    public virtual DbSet<WebhookLog>                 WebhookLogs                { get; set; }
    public virtual DbSet<BackgroundJob>              BackgroundJobs             { get; set; }
    public virtual DbSet<PaymentAuditLog>            PaymentAuditLogs           { get; set; }
    public virtual DbSet<AdminAuditLog>              AdminAuditLogs             { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlServer("Name=TRSConnection");
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // SystemConfig
        mb.Entity<SystemConfig>(e => {
            e.HasKey(x => x.ConfigKey);
            e.Property(x => x.ConfigKey).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.Property(x => x.UpdatedBy).HasMaxLength(100);
        });

        // AdminUsers
        mb.Entity<AdminUser>(e => {
            e.HasKey(x => x.UserId).HasName("PK_AdminUsers");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.PasswordHash).HasMaxLength(255).IsUnicode(false);
            e.Property(x => x.Role).HasMaxLength(15).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.MustChangePassword).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("UQ_AdminUsers_Email");
        });

        // Events
        mb.Entity<Event>(e => {
            e.HasKey(x => x.EventId).HasName("PK_Events");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Venue).HasMaxLength(300);
            e.Property(x => x.VenueAddress).HasMaxLength(500);
            e.Property(x => x.BannerUrl).HasMaxLength(1000);
            e.Property(x => x.ProspectusUrl).HasMaxLength(1000);
            e.Property(x => x.SportType).HasMaxLength(100);
            e.Property(x => x.FixtureMode).HasMaxLength(15).IsUnicode(false).HasDefaultValue("internal");
            e.Property(x => x.MaxParticipants).HasDefaultValue(100);
            e.Property(x => x.IsSports).HasDefaultValue(true);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.CreatedByUser).WithMany()
             .HasForeignKey(x => x.CreatedBy).HasConstraintName("FK_Events_CreatedBy");
        });

        // EventGalleryImages
        mb.Entity<EventGalleryImage>(e => {
            e.HasKey(x => x.GalleryImageId).HasName("PK_EventGalleryImages");
            e.Property(x => x.GalleryImageId).HasColumnName("GalleryImageID");
            e.Property(x => x.ImageUrl).HasMaxLength(1000);
            e.HasOne(x => x.Event).WithMany(x => x.GalleryImages)
             .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_EventGalleryImages_Event");
        });

        // Programs (TrsProgram → table name Programs)
        mb.Entity<TrsProgram>(e => {
            e.ToTable("Programs");
            e.HasKey(x => x.ProgramId).HasName("PK_Programs");
            e.Property(x => x.ProgramId).HasColumnName("ProgramID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Gender).HasMaxLength(20);
            e.Property(x => x.Fee).HasColumnType("decimal(10,2)");
            e.Property(x => x.FeeStructure).HasMaxLength(10).IsUnicode(false).HasDefaultValue("per_entry");
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("open");
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.MinAge).HasDefaultValue(0);
            e.Property(x => x.MaxAge).HasDefaultValue(99);
            e.Property(x => x.MinPlayers).HasDefaultValue(1);
            e.Property(x => x.MaxPlayers).HasDefaultValue(1);
            e.Property(x => x.MinParticipants).HasDefaultValue(1);
            e.Property(x => x.MaxParticipants).HasDefaultValue(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.Event).WithMany(x => x.Programs)
             .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_Programs_Event");
        });

        // ProgramFields
        mb.Entity<ProgramField>(e => {
            e.HasKey(x => x.ProgramId).HasName("PK_ProgramFields");
            e.Property(x => x.ProgramId).HasColumnName("ProgramID");
            e.HasOne(x => x.Program).WithOne(x => x.Fields)
             .HasForeignKey<ProgramField>(x => x.ProgramId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_ProgramFields_Program");
        });

        // ProgramCustomFields
        mb.Entity<ProgramCustomField>(e => {
            e.HasKey(x => x.CustomFieldId).HasName("PK_ProgramCustomFields");
            e.Property(x => x.CustomFieldId).HasColumnName("CustomFieldID");
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.FieldType).HasMaxLength(20).IsUnicode(false).HasDefaultValue("text");
            e.HasOne(x => x.Program).WithMany(x => x.CustomFields)
             .HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_ProgramCustomFields_Program");
        });

        // SbaRankings
        mb.Entity<SbaRanking>(e => {
            e.HasKey(x => x.SbaId).HasName("PK_SbaRankings");
            e.Property(x => x.SbaId).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Club).HasMaxLength(200);
            e.Property(x => x.Gender).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        // EventRegistrations
        mb.Entity<EventRegistration>(e => {
            e.HasKey(x => x.RegistrationId).HasName("PK__EventReg__6EF58830A1341650");
            e.Property(x => x.RegistrationId).HasColumnName("RegistrationID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.EventName).HasMaxLength(300);
            e.Property(x => x.RegStatus).HasMaxLength(15).HasDefaultValue("Pending");
            e.Property(x => x.ContactName).HasMaxLength(200);
            e.Property(x => x.ContactEmail).HasMaxLength(255);
            e.Property(x => x.ContactPhone).HasMaxLength(30).IsUnicode(false);
            e.Property(x => x.SubmittedAt).HasDefaultValueSql("(sysutcdatetime())");
            // legacy
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).HasDefaultValue("SGD");
            e.Property(x => x.TotalAmount).HasColumnType("decimal(10,2)");
            e.Property(x => x.RegistrationStatus).HasMaxLength(1).IsUnicode(false).HasDefaultValue("P");
            e.Property(x => x.NumberOfParticipants).HasDefaultValue(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(getdate())");
            e.HasOne(x => x.Event).WithMany()
             .HasForeignKey(x => x.EventId).HasConstraintName("FK_Registrations_Event");
        });

        // ParticipantGroups
        mb.Entity<ParticipantGroup>(e => {
            e.HasKey(x => x.GroupId).HasName("PK_ParticipantGroups");
            e.Property(x => x.GroupId).HasColumnName("GroupID");
            e.Property(x => x.RegistrationId).HasColumnName("RegistrationID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.ProgramId).HasColumnName("ProgramID");
            e.Property(x => x.ProgramName).HasMaxLength(300);
            e.Property(x => x.Fee).HasColumnType("decimal(10,2)");
            e.Property(x => x.GroupStatus).HasMaxLength(15).HasDefaultValue("Pending");
            e.Property(x => x.ClubDisplay).HasMaxLength(200);
            e.Property(x => x.NamesDisplay).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.Registration).WithMany(x => x.ParticipantGroups)
             .HasForeignKey(x => x.RegistrationId).HasConstraintName("FK_ParticipantGroups_Registration");
            e.HasOne(x => x.Event).WithMany()
             .HasForeignKey(x => x.EventId).HasConstraintName("FK_ParticipantGroups_Event");
            e.HasOne(x => x.Program).WithMany(x => x.ParticipantGroups)
             .HasForeignKey(x => x.ProgramId).HasConstraintName("FK_ParticipantGroups_Program");
        });

        // TrsParticipants (table name Participants)
        mb.Entity<TrsParticipant>(e => {
            e.ToTable("Participants");
            e.HasKey(x => x.ParticipantId).HasName("PK_Participants");
            e.Property(x => x.ParticipantId).HasColumnName("ParticipantID");
            e.Property(x => x.GroupId).HasColumnName("GroupID");
            e.Property(x => x.FullName).HasMaxLength(200);
            e.Property(x => x.Gender).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.Nationality).HasMaxLength(100);
            e.Property(x => x.ClubSchoolCompany).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.ContactNumber).HasMaxLength(30).IsUnicode(false);
            e.Property(x => x.TshirtSize).HasMaxLength(5).IsUnicode(false);
            e.Property(x => x.SbaId).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.GuardianName).HasMaxLength(200);
            e.Property(x => x.GuardianContact).HasMaxLength(30).IsUnicode(false);
            e.Property(x => x.DocumentUrl).HasMaxLength(1000);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.Group).WithMany(x => x.Participants)
             .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_Participants_Group");
        });

        // ParticipantCustomFieldValues
        mb.Entity<ParticipantCustomFieldValue>(e => {
            e.HasKey(x => x.ValueId).HasName("PK_ParticipantCFV");
            e.Property(x => x.ValueId).HasColumnName("ValueID");
            e.Property(x => x.FieldLabel).HasMaxLength(200);
            e.HasIndex(x => new { x.ParticipantId, x.CustomFieldId }).IsUnique()
             .HasDatabaseName("UQ_ParticipantCFV_ParticipantField");
            e.HasOne(x => x.Participant).WithMany(x => x.CustomFieldValues)
             .HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.Cascade)
             .HasConstraintName("FK_ParticipantCFV_Participant");
            e.HasOne(x => x.CustomField).WithMany()
             .HasForeignKey(x => x.CustomFieldId).HasConstraintName("FK_ParticipantCFV_CustomField");
        });

        // EventParticipants (legacy junction — preserved as-is)
        mb.Entity<EventParticipant>(e => {
            e.HasKey(x => x.EventParticipantId).HasName("PK__EventPar__09F32B7205B108E2");
            e.Property(x => x.EventParticipantId).HasColumnName("EventParticipantID");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(getdate())");
            e.Property(x => x.ParticipantId).HasColumnName("ParticipantID");
            e.Property(x => x.RegistrationId).HasColumnName("RegistrationID");
            e.HasOne(x => x.Participant).WithMany(x => x.EventParticipants)
             .HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.ClientSetNull)
             .HasConstraintName("FK_EventParticipants_Participant");
            e.HasOne(x => x.Registration).WithMany(x => x.EventParticipants)
             .HasForeignKey(x => x.RegistrationId).OnDelete(DeleteBehavior.ClientSetNull)
             .HasConstraintName("FK_EventParticipants_Registration");
        });

        // Payments
        mb.Entity<Payment>(e => {
            e.HasKey(x => x.PaymentId).HasName("PK__Payments__9B556A58E233D9D7");
            e.Property(x => x.PaymentId).HasColumnName("PaymentID");
            e.Property(x => x.RegistrationId).HasColumnName("RegistrationID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.Amount).HasColumnType("decimal(10,2)");
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false).HasDefaultValue("SGD");
            e.Property(x => x.PaymentGateway).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.PaymentMethod).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.PaymentStatus).HasMaxLength(2).IsUnicode(false).HasDefaultValue("P");
            e.Property(x => x.GatewaySessionId).HasMaxLength(255).IsUnicode(false).HasColumnName("GatewaySessionID");
            e.Property(x => x.GatewayPaymentId).HasMaxLength(255).IsUnicode(false).HasColumnName("GatewayPaymentID");
            e.Property(x => x.GatewayChargeId).HasMaxLength(255).IsUnicode(false).HasColumnName("GatewayChargeID");
            e.Property(x => x.ReceiptNumber).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.PaymentGatewayResponse).HasColumnType("nvarchar(max)");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(getdate())");
            e.HasIndex(x => x.RegistrationId).IsUnique().HasDatabaseName("UQ_Payments_Registration");
            e.HasIndex(x => x.GatewaySessionId).IsUnique().HasDatabaseName("UQ_Payments_GatewaySessionID")
             .HasFilter("[GatewaySessionID] IS NOT NULL");
            e.HasIndex(x => x.GatewayPaymentId).IsUnique().HasDatabaseName("UQ_Payments_GatewayPaymentID")
             .HasFilter("[GatewayPaymentID] IS NOT NULL");
            e.HasOne(x => x.Registration).WithMany(x => x.Payments)
             .HasForeignKey(x => x.RegistrationId).OnDelete(DeleteBehavior.ClientSetNull)
             .HasConstraintName("FK_Payments_Registration");
        });

        // PaymentItems
        mb.Entity<PaymentItem>(e => {
            e.HasKey(x => x.PaymentItemId).HasName("PK_PaymentItems");
            e.Property(x => x.PaymentItemId).HasColumnName("PaymentItemID");
            e.Property(x => x.PaymentId).HasColumnName("PaymentID");
            e.Property(x => x.GroupId).HasColumnName("GroupID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.ProgramId).HasColumnName("ProgramID");
            e.Property(x => x.ProgramName).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.PlayerName).HasMaxLength(200);
            e.Property(x => x.Amount).HasColumnType("decimal(10,2)");
            e.Property(x => x.ItemStatus).HasMaxLength(2).IsUnicode(false).HasDefaultValue("P");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.Payment).WithMany(x => x.Items)
             .HasForeignKey(x => x.PaymentId).HasConstraintName("FK_PaymentItems_Payment");
            e.HasOne(x => x.Group).WithMany(x => x.PaymentItems)
             .HasForeignKey(x => x.GroupId).HasConstraintName("FK_PaymentItems_Group");
            e.HasOne(x => x.Participant).WithMany(x => x.PaymentItems)
             .HasForeignKey(x => x.ParticipantId).IsRequired(false)
             .HasConstraintName("FK_PaymentItems_Participant");
        });

        // Refunds
        mb.Entity<Refund>(e => {
            e.HasKey(x => x.RefundId).HasName("PK__Refunds__725AB900B01FF332");
            e.Property(x => x.RefundId).HasColumnName("RefundID");
            e.Property(x => x.PaymentId).HasColumnName("PaymentID");
            e.Property(x => x.PaymentItemId).HasColumnName("PaymentItemID");
            e.Property(x => x.PaymentGateway).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.GatewayRefundId).HasMaxLength(255).IsUnicode(false).HasColumnName("GatewayRefundID");
            e.Property(x => x.RefundAmount).HasColumnType("decimal(10,2)");
            e.Property(x => x.RefundReason).HasMaxLength(500);
            e.Property(x => x.RefundStatus).HasMaxLength(1).IsUnicode(false).IsFixedLength().HasDefaultValue("P");
            e.Property(x => x.RequestedBy).HasMaxLength(100);
            e.Property(x => x.ApprovedBy).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(getdate())");
            e.HasOne(x => x.Payment).WithMany(x => x.Refunds)
             .HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.ClientSetNull)
             .HasConstraintName("FK_Refunds_Payment");
            e.HasOne(x => x.PaymentItem).WithMany(x => x.Refunds)
             .HasForeignKey(x => x.PaymentItemId).HasConstraintName("FK_Refunds_PaymentItem");
        });

        // Fixtures
        mb.Entity<Fixture>(e => {
            e.HasKey(x => x.FixtureId).HasName("PK_Fixtures");
            e.Property(x => x.FixtureId).HasColumnName("FixtureID");
            e.Property(x => x.EventId).HasColumnName("EventID");
            e.Property(x => x.ProgramId).HasColumnName("ProgramID");
            e.Property(x => x.FixtureMode).HasMaxLength(15).IsUnicode(false).HasDefaultValue("internal");
            e.Property(x => x.FixtureFormat).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Phase).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasIndex(x => new { x.EventId, x.ProgramId }).IsUnique()
             .HasDatabaseName("UQ_Fixtures_EventProgram");
            e.HasOne(x => x.Event).WithMany()
             .HasForeignKey(x => x.EventId).HasConstraintName("FK_Fixtures_Event");
            e.HasOne(x => x.Program).WithMany()
             .HasForeignKey(x => x.ProgramId).HasConstraintName("FK_Fixtures_Program");
            e.HasOne(x => x.GeneratedByUser).WithMany()
             .HasForeignKey(x => x.GeneratedBy).IsRequired(false)
             .HasConstraintName("FK_Fixtures_GeneratedBy");
        });

        // WebhookLogs
        mb.Entity<WebhookLog>(e => {
            e.HasKey(x => x.WebhookLogId).HasName("PK__WebhookL__4DD95F127BA688F7");
            e.Property(x => x.WebhookLogId).HasColumnName("WebhookLogID");
            e.Property(x => x.PaymentGateway).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.GatewayEventId).HasMaxLength(255).IsUnicode(false).HasColumnName("GatewayEventID");
            e.Property(x => x.EventType).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.PayloadJson).HasColumnName("PayloadJSON");
            e.Property(x => x.ProcessingStatus).HasMaxLength(1).IsUnicode(false).IsFixedLength().HasDefaultValue("P");
            e.Property(x => x.ReceivedAt).HasDefaultValueSql("(getdate())");
            e.HasIndex(x => x.GatewayEventId).IsUnique().HasDatabaseName("UQ_WebhookLogs_GatewayEventID");
        });

        // BackgroundJobs
        mb.Entity<BackgroundJob>(e => {
            e.HasKey(x => x.JobId).HasName("PK__Backgrou__056690C218EA13AF");
            e.Property(x => x.JobType).HasMaxLength(100);
            e.Property(x => x.ReferenceType).HasMaxLength(50);
            e.Property(x => x.JobStatus).HasMaxLength(1).IsUnicode(false).IsFixedLength().HasDefaultValue("P");
            e.Property(x => x.MaxRetry).HasDefaultValue(3);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.Property(x => x.ScheduledAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        // PaymentAuditLog
        mb.Entity<PaymentAuditLog>(e => {
            e.HasKey(x => x.AuditId).HasName("PK_PaymentAuditLog");
            e.Property(x => x.AuditId).HasColumnName("AuditID");
            e.Property(x => x.EntityType).HasMaxLength(20).IsUnicode(false);
            e.Property(x => x.Action).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.OldStatus).HasMaxLength(5).IsUnicode(false);
            e.Property(x => x.NewStatus).HasMaxLength(5).IsUnicode(false);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.PerformedBy).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false).HasColumnName("IPAddress");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        // AdminAuditLog
        mb.Entity<AdminAuditLog>(e => {
            e.HasKey(x => x.AuditId).HasName("PK_AdminAuditLog");
            e.Property(x => x.AuditId).HasColumnName("AuditID");
            e.Property(x => x.UserId).HasColumnName("UserID");
            e.Property(x => x.UserEmail).HasMaxLength(255);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(50).IsUnicode(false).HasColumnName("EntityID");
            e.Property(x => x.IpAddress).HasMaxLength(45).IsUnicode(false).HasColumnName("IPAddress");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            e.HasOne(x => x.User).WithMany()
             .HasForeignKey(x => x.UserId).IsRequired(false).HasConstraintName("FK_AdminAuditLog_User");
        });

        OnModelCreatingPartial(mb);
    }
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
