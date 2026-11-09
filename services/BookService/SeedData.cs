namespace BookService;

// Demo data, only inserted when the table is empty (so after `docker compose down -v`).
//
// The Loan Service seeds two active loans for these books (see LoanService/SeedData.cs):
// book 1 once and book 5 once. A loan lives in loan_db and the copy count in book_db, and no service
// may touch another service's database, so each service seeds its own part and the numbers are
// kept in step by hand. That is why book 1 has 2 of 3 copies available and book 5 has 0 of 1.
// The ids are 1, 2, 3... because the table is empty and the rows are inserted in this order.
public static class SeedData
{
    public static void Seed(BookDbContext db)
    {
        if (db.Books.Any())
            return;

        db.Books.AddRange(
            new Book { Title = "The Pragmatic Programmer", Author = "David Thomas, Andrew Hunt", Isbn = "9780135957059", TotalCopies = 3, AvailableCopies = 2 }, // 1: one copy on loan
            new Book { Title = "Clean Code", Author = "Robert C. Martin", Isbn = "9780132350884", TotalCopies = 2, AvailableCopies = 2 },                      // 2
            new Book { Title = "Designing Data-Intensive Applications", Author = "Martin Kleppmann", Isbn = "9781449373320", TotalCopies = 4, AvailableCopies = 4 }, // 3
            new Book { Title = "Building Microservices", Author = "Sam Newman", Isbn = "9781492034025", TotalCopies = 2, AvailableCopies = 2 },              // 4
            new Book { Title = "Domain-Driven Design", Author = "Eric Evans", Isbn = "9780321125217", TotalCopies = 1, AvailableCopies = 0 },                // 5: the only copy is on loan
            new Book { Title = "Microservices Patterns", Author = "Chris Richardson", Isbn = "9781617294549", TotalCopies = 2, AvailableCopies = 2 },        // 6
            new Book { Title = "Release It!", Author = "Michael T. Nygard", Isbn = "9781680502398", TotalCopies = 1, AvailableCopies = 1 }                   // 7
        );
        db.SaveChanges();
    }
}
