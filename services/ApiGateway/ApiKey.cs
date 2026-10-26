using System.Security.Cryptography;
using System.Text;

namespace ApiGateway;

public static class ApiKey
{
    // FixedTimeEquals always compares every byte. A normal == stops at the first wrong
    // character, so an attacker could guess the key one character at a time by measuring
    // how long each answer takes (a timing attack).
    public static bool IsValid(string? sent, string expected) =>
        sent is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));
}
