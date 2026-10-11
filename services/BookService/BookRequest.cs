namespace BookService;

// The JSON body for POST and PUT. Fields are nullable so we can tell
// "missing" apart from "0" or "" and return a clear 400.
public record BookRequest(string? Title, string? Author, string? Isbn, int? TotalCopies, int? AvailableCopies)
{
    // ISBN without hyphens or spaces, so "978-0-13-235088-4" and "9780132350884" count as the same book.
    public string NormalizedIsbn => (Isbn ?? "").Replace("-", "").Replace(" ", "");

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(Title))
            errors["title"] = ["Title is required."];

        if (string.IsNullOrWhiteSpace(Author))
            errors["author"] = ["Author is required."];

        if (NormalizedIsbn.Length is not (10 or 13) || !NormalizedIsbn.All(char.IsAsciiDigit))
            errors["isbn"] = ["ISBN must be 10 or 13 digits (hyphens are allowed)."];

        if (TotalCopies is null or < 0)
            errors["totalCopies"] = ["TotalCopies must be 0 or more."];

        if (AvailableCopies is null or < 0)
            errors["availableCopies"] = ["AvailableCopies must be 0 or more."];
        else if (AvailableCopies > TotalCopies)
            errors["availableCopies"] = ["AvailableCopies cannot be more than TotalCopies."];

        return errors;
    }

    public void CopyTo(Book book)
    {
        book.Title = Title!.Trim();
        book.Author = Author!.Trim();
        book.Isbn = NormalizedIsbn;
        book.TotalCopies = TotalCopies!.Value;
        book.AvailableCopies = AvailableCopies!.Value;
    }
}
