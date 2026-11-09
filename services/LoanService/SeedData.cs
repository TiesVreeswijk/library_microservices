namespace LoanService;

// Demo data, only inserted when the table is empty (so after `docker compose down -v`).
//
// A loan only stores ids. These ids point to the seed data of the Member Service (members 1 and 2)
// and the Book Service (books 1-5). This service cannot check them or lower the copy count itself
// (that would mean touching member_db or book_db), so the Book Service's seed already has the
// active loans subtracted: book 1 has 2 of 3 copies available, book 5 has 0 of 1.
// If you change the loans here, change BookService/SeedData.cs too.
public static class SeedData
{
    public static void Seed(LoanDbContext db)
    {
        if (db.Loans.Any())
            return;

        var now = DateTime.UtcNow;
        db.Loans.AddRange(
            // Anna (member 1): two books out, one returned.
            new Loan { MemberId = 1, BookId = 1, LoanedAt = now.AddDays(-5), DueDate = now.AddDays(9) },
            new Loan { MemberId = 1, BookId = 5, LoanedAt = now.AddDays(-2), DueDate = now.AddDays(12) },
            new Loan { MemberId = 1, BookId = 2, LoanedAt = now.AddDays(-30), DueDate = now.AddDays(-16), ReturnedAt = now.AddDays(-20) },
            // Bram (member 2): only returned books.
            new Loan { MemberId = 2, BookId = 3, LoanedAt = now.AddDays(-40), DueDate = now.AddDays(-26), ReturnedAt = now.AddDays(-28) },
            new Loan { MemberId = 2, BookId = 4, LoanedAt = now.AddDays(-15), DueDate = now.AddDays(-1), ReturnedAt = now.AddDays(-3) }
        );
        db.SaveChanges();
    }
}
