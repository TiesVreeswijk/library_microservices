using System.Net;

namespace LoanService;

// The Loan Service never touches member_db or book_db. Everything it needs from
// the other services, it asks for over HTTP through these two small clients.
// If a service cannot be reached, HttpClient throws; Program.cs turns that into a 503.

// Only the fields the Loan Service needs. Extra fields in the JSON are ignored,
// so the Member Service can add fields without breaking this service.
public record MemberInfo(int Id, string Name, bool IsBlocked);

public class MemberServiceClient(HttpClient http)
{
    // null means the member does not exist.
    public async Task<MemberInfo?> GetMemberAsync(int id)
    {
        var response = await http.GetAsync($"/members/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MemberInfo>();
    }
}

public enum CheckoutResult { Ok, BookNotFound, NoCopiesAvailable }

public class BookServiceClient(HttpClient http)
{
    public async Task<CheckoutResult> CheckoutAsync(int bookId)
    {
        var response = await http.PostAsync($"/books/{bookId}/checkout", null);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound: return CheckoutResult.BookNotFound;
            case HttpStatusCode.Conflict: return CheckoutResult.NoCopiesAvailable;
        }

        response.EnsureSuccessStatusCode();
        return CheckoutResult.Ok;
    }

    public async Task ReturnAsync(int bookId)
    {
        var response = await http.PostAsync($"/books/{bookId}/return", null);

        // 404 (book was deleted) or 409 (all copies already in) means the Book Service
        // and the Loan Service disagree. We accept that and still close the loan;
        // without a shared database there is no transaction to keep them in sync.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            return;

        response.EnsureSuccessStatusCode();
    }
}
