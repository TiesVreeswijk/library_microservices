namespace LoanService;

public class Loan
{
    public int Id { get; set; }

    // Only IDs: the member lives in member_db and the book in book_db.
    // There is no foreign key, because those tables are in other databases.
    public int MemberId { get; set; }
    public int BookId { get; set; }

    public DateTime LoanedAt { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? ReturnedAt { get; set; }
}
