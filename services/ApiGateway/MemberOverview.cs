using System.Net;

namespace ApiGateway;

// API Composition: GET /api/members/{id}/overview.
// No single service can answer "who is this member and which books do they have?":
// the member lives in member_db, the loans in loan_db and the titles in book_db.
// So the gateway asks all three services and joins the answers in memory.
public static class MemberOverview
{
    // Only the fields the overview needs. Extra fields in the JSON are ignored,
    // so a service can add fields without breaking the gateway.
    public record MemberInfo(int Id, string Name, string CardNumber, bool IsBlocked);
    public record LoanInfo(int Id, int BookId, DateTime LoanedAt, DateTime DueDate, DateTime? ReturnedAt);
    public record BookInfo(int Id, string Title, string Author, string Isbn);

    // Book is null when the Book Service is down or the book was deleted.
    public record LoanWithBook(int Id, int BookId, BookInfo? Book, DateTime LoanedAt, DateTime DueDate, DateTime? ReturnedAt);

    // Loans is null when the Loan Service is down. Warnings tells the client what is missing and why.
    public record Overview(MemberInfo Member, List<LoanWithBook>? Loans, List<string> Warnings);

    public static async Task<IResult> GetAsync(int id, IHttpClientFactory clients, CancellationToken cancel)
    {
        var memberService = clients.CreateClient("MemberService");
        var loanService = clients.CreateClient("LoanService");
        var bookService = clients.CreateClient("BookService");

        // 1. The member and their loans only need the member id, so ask both services at the same
        //    time. The overview then takes as long as the slowest call, not the two added up.
        var memberTask = GetAsync<MemberInfo>(memberService, $"/members/{id}", cancel);
        var loansTask = GetAsync<List<LoanInfo>>(loanService, $"/loans?memberId={id}", cancel);
        await Task.WhenAll(memberTask, loansTask);

        // The member is the core of the overview: without it there is nothing to show.
        var (member, memberReachable) = memberTask.Result;
        if (!memberReachable)
            return Results.Problem("The Member Service cannot be reached. Try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (member is null)
            return Results.NotFound();

        // Fallback: without the Loan Service we still show the member, with a warning.
        var (loans, loansReachable) = loansTask.Result;
        if (!loansReachable)
            return Results.Ok(new Overview(member, null, ["The Loan Service cannot be reached, so the loans are missing."]));

        // 2. The Loan Service only knows book ids. Ask the Book Service for each book once
        //    (a member can borrow the same book twice), all at the same time.
        loans ??= [];
        var bookIds = loans.Select(l => l.BookId).Distinct().ToList();
        var bookAnswers = await Task.WhenAll(bookIds.Select(bookId => GetAsync<BookInfo>(bookService, $"/books/{bookId}", cancel)));
        var books = bookIds.Zip(bookAnswers).ToDictionary(pair => pair.First, pair => pair.Second.Value);

        // Fallback: without the Book Service the loans are still shown, only without titles.
        var warnings = new List<string>();
        if (bookAnswers.Any(answer => !answer.Reachable))
            warnings.Add("The Book Service cannot be reached, so some book details are missing.");

        // 3. Join in memory: put the book next to each loan.
        var loansWithBooks = loans
            .Select(l => new LoanWithBook(l.Id, l.BookId, books[l.BookId], l.LoanedAt, l.DueDate, l.ReturnedAt))
            .ToList();

        return Results.Ok(new Overview(member, loansWithBooks, warnings));
    }

    // Value is null when the service answered 404. Reachable is false when the service is down,
    // too slow, answered with an error, or the circuit breaker is open for it.
    private static async Task<(T? Value, bool Reachable)> GetAsync<T>(HttpClient service, string path, CancellationToken cancel)
    {
        try
        {
            using var response = await service.GetAsync(path, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (default, true);

            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>(cancel), true);
        }
        catch (Exception e) when (Forwarder.IsUnreachable(e))
        {
            return (default, false);
        }
    }
}
