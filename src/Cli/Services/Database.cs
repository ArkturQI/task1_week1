using System.Security.Cryptography;
using System.Text;

namespace Cli.Services;

internal static class Database
{
    public static string ConnStr() =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Course")
        ?? "Host=postgres;Port=5432;Database=course;Username=postgres;Password=postgres";

    public static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}