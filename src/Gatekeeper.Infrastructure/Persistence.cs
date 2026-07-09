using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Gatekeeper.Infrastructure;

/// <summary>
/// Per-tenant operational database. One physical database per community; the connection is
/// chosen per request from ITenantContext (see Tenancy.cs / Api Program.cs).
/// </summary>
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options)
    : DbContext(options), IUnitOfWork
{
    public DbSet<TelegramUser> Users => Set<TelegramUser>();
    public DbSet<UserIdentitySnapshot> IdentitySnapshots => Set<UserIdentitySnapshot>();
    public DbSet<Domain.Application> Applications => Set<Domain.Application>();
    public DbSet<SurveySession> Sessions => Set<SurveySession>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<ModerationAction> ModerationActions => Set<ModerationAction>();
    public DbSet<ModerationAttachment> ModerationAttachments => Set<ModerationAttachment>();
    public DbSet<TelegramCommand> TelegramCommands => Set<TelegramCommand>();
    public DbSet<SiteVisit> SiteVisits => Set<SiteVisit>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");

        b.Entity<TelegramUser>(e =>
        {
            e.HasIndex(u => u.TelegramUserId).IsUnique();
            // GIN trigram index powering "applicants with similar names/initials".
            e.HasIndex(u => u.NameNormalized).HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasMany(u => u.History).WithOne().HasForeignKey(h => h.TelegramUserId);
        });

        b.Entity<Domain.Application>(e =>
        {
            e.HasOne(a => a.Session).WithOne().HasForeignKey<SurveySession>(s => s.ApplicationId);
            e.HasMany(a => a.Answers).WithOne().HasForeignKey(a => a.ApplicationId);
            // Map the uint property onto the system xmin column as a concurrency token.
            e.Property(a => a.RowVersion).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            // One live (undecided) application per user per chat.
            e.HasIndex(nameof(Domain.Application.TelegramUserId), nameof(Domain.Application.MainChatId), nameof(Domain.Application.Status))
                .IsUnique()
                .HasFilter("\"Status\" IN (0,1,2,3)");  // JoinRequested..AwaitingReview
        });

        b.Entity<Question>().HasIndex(q => q.Position);

        b.Entity<ModerationAction>(e =>
        {
            e.HasIndex(m => m.TelegramUserId);
            e.HasMany(m => m.Attachments).WithOne().HasForeignKey(a => a.ModerationActionId);
        });

        b.Entity<TelegramCommand>(e =>
        {
            e.HasIndex(c => c.Status);
            e.Property(c => c.RowVersion).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        b.Entity<SiteVisit>(e =>
        {
            e.HasIndex(v => v.CreatedAt);
            e.HasIndex(v => v.SessionId);
        });

        base.OnModelCreating(b);
    }

    // A pre-check comparing an expected RowVersion (e.g. DecideApplicationHandler's If-Match) only
    // catches a race where both sides re-read after the first commit — it does nothing for a
    // second write already holding an in-memory copy loaded BEFORE that commit (the admin-group
    // decision path, which has no expected version to check, is exactly this). That gap can only
    // be caught here, at the actual commit — translate EF's raw exception into the one every
    // caller already handles instead of leaking Postgres/EF concurrency detail across the boundary.
    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await base.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException("The record was modified by someone else.", ex);
        }
    }
}

/// <summary>Shared control-plane database: the tenant registry used for routing.</summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantChat> TenantChats => Set<TenantChat>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.HasMany(t => t.Chats).WithOne().HasForeignKey(c => c.TenantId);
        });
        b.Entity<TenantChat>().HasIndex(c => c.ChatId);  // resolve tenant by incoming chat id
        base.OnModelCreating(b);
    }
}
