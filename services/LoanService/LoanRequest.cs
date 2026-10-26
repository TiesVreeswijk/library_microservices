namespace LoanService;

// The JSON body for POST /loans. Fields are nullable so a missing field gives a clear 400.
public record LoanRequest(int? MemberId, int? BookId)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (MemberId is null or < 1)
            errors["memberId"] = ["MemberId is required."];

        if (BookId is null or < 1)
            errors["bookId"] = ["BookId is required."];

        return errors;
    }
}
