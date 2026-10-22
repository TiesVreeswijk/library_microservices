namespace MemberService;

public static class SeedData
{
    public static void Seed(MemberDbContext db)
    {
        if (db.Members.Any())
            return;

        db.Members.AddRange(
            new Member { Name = "Anna de Vries", CardNumber = "LIB0001", IsBlocked = false },
            new Member { Name = "Bram Jansen", CardNumber = "LIB0002", IsBlocked = false },
            // Blocked, so the Loan Service can later show that a blocked member cannot borrow.
            new Member { Name = "Carla Bakker", CardNumber = "LIB0003", IsBlocked = true }
        );
        db.SaveChanges();
    }
}
