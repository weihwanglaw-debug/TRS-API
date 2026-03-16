using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace TRS_Data.Models;

public partial class TRSDbContext : DbContext
{
    public TRSDbContext()
    {
    }

    public TRSDbContext(DbContextOptions<TRSDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<BackgroundJob> BackgroundJobs { get; set; }

    public virtual DbSet<EventParticipant> EventParticipants { get; set; }

    public virtual DbSet<EventRegistration> EventRegistrations { get; set; }

    public virtual DbSet<Participant> Participants { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<Refund> Refunds { get; set; }

    public virtual DbSet<WebhookLog> WebhookLogs { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseSqlServer("Server=LAPTOP-3B6R8DFK\\SQLEXPRESS;Database=TRS;Trusted_Connection=True;TrustServerCertificate=True;");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackgroundJob>(entity =>
        {
            entity.HasKey(e => e.JobId).HasName("PK__Backgrou__056690C218EA13AF");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.JobStatus)
                .HasMaxLength(1)
                .IsUnicode(false)
                .HasDefaultValue("P")
                .IsFixedLength();
            entity.Property(e => e.JobType).HasMaxLength(100);
            entity.Property(e => e.MaxRetry).HasDefaultValue(3);
            entity.Property(e => e.ReferenceType).HasMaxLength(50);
            entity.Property(e => e.ScheduledAt).HasDefaultValueSql("(sysutcdatetime())");
        });

        modelBuilder.Entity<EventParticipant>(entity =>
        {
            entity.HasKey(e => e.EventParticipantId).HasName("PK__EventPar__09F32B7205B108E2");

            entity.Property(e => e.EventParticipantId).HasColumnName("EventParticipantID");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.ParticipantId).HasColumnName("ParticipantID");
            entity.Property(e => e.RegistrationId).HasColumnName("RegistrationID");

            entity.HasOne(d => d.Participant).WithMany(p => p.EventParticipants)
                .HasForeignKey(d => d.ParticipantId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_EventParticipants_Participant");

            entity.HasOne(d => d.Registration).WithMany(p => p.EventParticipants)
                .HasForeignKey(d => d.RegistrationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_EventParticipants_Registration");
        });

        modelBuilder.Entity<EventRegistration>(entity =>
        {
            entity.HasKey(e => e.RegistrationId).HasName("PK__EventReg__6EF58830A1341650");

            entity.Property(e => e.RegistrationId).HasColumnName("RegistrationID");
            entity.Property(e => e.ContactEmail).HasMaxLength(255);
            entity.Property(e => e.ContactName).HasMaxLength(200);
            entity.Property(e => e.ContactPhone)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("SGD");
            entity.Property(e => e.EventId).HasColumnName("EventID");
            entity.Property(e => e.NumberOfParticipants).HasDefaultValue(1);
            entity.Property(e => e.RegistrationStatus)
                .HasMaxLength(1)
                .IsUnicode(false)
                .HasDefaultValue("P");
            entity.Property(e => e.TotalAmount).HasColumnType("decimal(10, 2)");
        });

        modelBuilder.Entity<Participant>(entity =>
        {
            entity.HasKey(e => e.ParticipantId).HasName("PK__Particip__7227997E11C8701E");

            entity.Property(e => e.ParticipantId).HasColumnName("ParticipantID");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Gender)
                .HasMaxLength(1)
                .IsUnicode(false);
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.SbaId)
                .HasMaxLength(50)
                .IsUnicode(false)
                .HasColumnName("SBA_ID");
            entity.Property(e => e.ShirtSize)
                .HasMaxLength(10)
                .IsUnicode(false);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.PaymentId).HasName("PK__Payments__9B556A58E233D9D7");

            entity.Property(e => e.PaymentId).HasColumnName("PaymentID");
            entity.Property(e => e.Amount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .IsUnicode(false)
                .HasDefaultValue("SGD");
            entity.Property(e => e.GatewayChargeId)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("GatewayChargeID");
            entity.Property(e => e.GatewayPaymentId)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("GatewayPaymentID");
            entity.Property(e => e.GatewaySessionId)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("GatewaySessionID");
            entity.Property(e => e.PaymentGateway)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.PaymentGatewayResponse).HasColumnType("text");
            entity.Property(e => e.PaymentMethod)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.PaymentStatus)
                .HasMaxLength(1)
                .IsUnicode(false)
                .HasDefaultValue("P");
            entity.Property(e => e.ReceiptNumber)
                .HasMaxLength(50)
                .IsUnicode(false);
            entity.Property(e => e.RegistrationId).HasColumnName("RegistrationID");

            entity.HasOne(d => d.Registration).WithMany(p => p.Payments)
                .HasForeignKey(d => d.RegistrationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Payments_Registration");
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.HasKey(e => e.RefundId).HasName("PK__Refunds__725AB900B01FF332");

            entity.Property(e => e.RefundId).HasColumnName("RefundID");
            entity.Property(e => e.ApprovedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.GatewayRefundId)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("GatewayRefundID");
            entity.Property(e => e.PaymentGateway)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.PaymentId).HasColumnName("PaymentID");
            entity.Property(e => e.RefundAmount).HasColumnType("decimal(10, 2)");
            entity.Property(e => e.RefundReason).HasMaxLength(500);
            entity.Property(e => e.RefundStatus)
                .HasMaxLength(1)
                .IsUnicode(false)
                .HasDefaultValue("P");
            entity.Property(e => e.RequestedBy).HasMaxLength(100);

            entity.HasOne(d => d.Payment).WithMany(p => p.Refunds)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Refunds_Payment");
        });

        modelBuilder.Entity<WebhookLog>(entity =>
        {
            entity.HasKey(e => e.WebhookLogId).HasName("PK__WebhookL__4DD95F127BA688F7");

            entity.Property(e => e.WebhookLogId).HasColumnName("WebhookLogID");
            entity.Property(e => e.EventType)
                .HasMaxLength(100)
                .IsUnicode(false);
            entity.Property(e => e.GatewayEventId)
                .HasMaxLength(255)
                .IsUnicode(false)
                .HasColumnName("GatewayEventID");
            entity.Property(e => e.PayloadJson)
                .HasColumnType("text")
                .HasColumnName("PayloadJSON");
            entity.Property(e => e.PaymentGateway)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.ProcessingStatus)
                .HasMaxLength(1)
                .IsUnicode(false)
                .HasDefaultValue("P");
            entity.Property(e => e.ReceivedAt).HasDefaultValueSql("(getdate())");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
