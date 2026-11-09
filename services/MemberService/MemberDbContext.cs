using Microsoft.EntityFrameworkCore;

namespace MemberService;

public class MemberDbContext(DbContextOptions<MemberDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();
    public DbSet<DeletionSaga> DeletionSagas => Set<DeletionSaga>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Member>().HasIndex(m => m.CardNumber).IsUnique();

        // Enums as text ("PendingDeletion" instead of 1), so the tables are readable in psql.
        modelBuilder.Entity<Member>().Property(m => m.Status).HasConversion<string>();
        modelBuilder.Entity<DeletionSaga>().Property(s => s.CurrentStep).HasConversion<string>();
        modelBuilder.Entity<DeletionSaga>().Property(s => s.State).HasConversion<string>();

        // The worker looks for sagas that are not finished yet.
        modelBuilder.Entity<DeletionSaga>().HasIndex(s => s.State);
        modelBuilder.Entity<DeletionSaga>().HasIndex(s => s.MemberId);
    }
}
