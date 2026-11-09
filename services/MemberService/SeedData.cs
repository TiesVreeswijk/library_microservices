namespace MemberService;

// Demo data, only inserted when the table is empty (so after `docker compose down -v`).
// Each member has a role in docs/DEMO.md; their loans are seeded by the Loan Service.
// The ids are 1, 2, 3... because the table is empty and the rows are inserted in this order.
public static class SeedData
{
    public static void Seed(MemberDbContext db)
    {
        if (db.Members.Any())
            return;

        db.Members.AddRange(
            // 1: has books out -> overview demo, and deleting her fails (saga compensates).
            new Member { Name = "Anna de Vries", CardNumber = "LIB0001", IsBlocked = false },
            // 2: only returned loans -> deleting him completes, his loan history gets anonymized.
            new Member { Name = "Bram Jansen", CardNumber = "LIB0002", IsBlocked = false },
            // 3: blocked, so the Loan Service refuses to lend her a book.
            new Member { Name = "Carla Bakker", CardNumber = "LIB0003", IsBlocked = true },
            // 4: no loans -> deleted while the Loan Service is down (saga waits, then finishes).
            new Member { Name = "Daan Visser", CardNumber = "LIB0004", IsBlocked = false },
            // 5: no loans, a spare to repeat a deletion demo without resetting everything.
            new Member { Name = "Eva Smit", CardNumber = "LIB0005", IsBlocked = false }
        );
        db.SaveChanges();
    }
}
