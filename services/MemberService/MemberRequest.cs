namespace MemberService;

// The JSON body for POST and PUT. Fields are nullable so we can tell
// "missing" apart from "" or false and return a clear 400.
public record MemberRequest(string? Name, string? CardNumber, bool? IsBlocked)
{
    // Card number without spaces and in capitals, so "lib 0001" and "LIB0001" count as the same card.
    public string NormalizedCardNumber => (CardNumber ?? "").Replace(" ", "").ToUpperInvariant();

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Name))
            errors["name"] = ["Name is required."];

        if (NormalizedCardNumber.Length == 0)
            errors["cardNumber"] = ["CardNumber is required."];

        if (IsBlocked is null)
            errors["isBlocked"] = ["IsBlocked is required (true or false)."];

        return errors;
    }

    public void CopyTo(Member member)
    {
        member.Name = Name!.Trim();
        member.CardNumber = NormalizedCardNumber;
        member.IsBlocked = IsBlocked!.Value;
    }
}
