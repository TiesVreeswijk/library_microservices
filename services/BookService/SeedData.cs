namespace BookService;

public static class SeedData
{
    public static void Seed(BookDbContext db)
    {
        if (db.Books.Any())
            return;

        db.Books.AddRange(
            new Book { Title = "The Pragmatic Programmer", Author = "David Thomas, Andrew Hunt", Isbn = "9780135957059", TotalCopies = 3, AvailableCopies = 3 },
            new Book { Title = "Clean Code", Author = "Robert C. Martin", Isbn = "9780132350884", TotalCopies = 2, AvailableCopies = 2 },
            new Book { Title = "Designing Data-Intensive Applications", Author = "Martin Kleppmann", Isbn = "9781449373320", TotalCopies = 4, AvailableCopies = 4 },
            new Book { Title = "Building Microservices", Author = "Sam Newman", Isbn = "9781492034025", TotalCopies = 2, AvailableCopies = 2 },
            new Book { Title = "Domain-Driven Design", Author = "Eric Evans", Isbn = "9780321125217", TotalCopies = 1, AvailableCopies = 1 }
        );
        db.SaveChanges();
    }
}
