namespace MemberService;

public class Member
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Null after the member is deleted (anonymized). The unique index allows many nulls,
    // so two deleted members never clash.
    public string? CardNumber { get; set; }

    public bool IsBlocked { get; set; }

    // Active: normal member. PendingDeletion: a deletion saga is running, so the member cannot
    // borrow books (the Loan Service checks this). Deleted: anonymized, the saga is finished.
    public MemberStatus Status { get; set; } = MemberStatus.Active;
}

// Stored as text in the database and sent as text in JSON ("Active"), so it is readable everywhere.
public enum MemberStatus { Active, PendingDeletion, Deleted }
