using Microsoft.EntityFrameworkCore;

namespace LoanService;

public class LoanDbContext(DbContextOptions<LoanDbContext> options) : DbContext(options)
{
    public DbSet<Loan> Loans => Set<Loan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // GET /loans?memberId= searches on this column.
        modelBuilder.Entity<Loan>().HasIndex(l => l.MemberId);
    }
}
