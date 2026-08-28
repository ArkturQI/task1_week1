using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Week1.Tests;

public sealed class TestEnvironment
{
    public const string DefaultBaseUrl = "http://localhost:8080";
    public const string DefaultIssuer = "moduledev-course";
    public const string DefaultAudience = "moduledev-api";
    public const string DefaultSigningKey = "moduledev-week1-rotated-key-do-not-use-in-production-2026-aug";

    public Uri BaseUri { get; }
    public string Issuer { get; }
    public string Audience { get; }
    public string SigningKey { get; }

    public TestEnvironment()
    {
        BaseUri = new Uri(Environment.GetEnvironmentVariable("WEEK1_BASE_URL") ?? DefaultBaseUrl);
        Issuer = Environment.GetEnvironmentVariable("COURSE_JWT_ISSUER") ?? DefaultIssuer;
        Audience = Environment.GetEnvironmentVariable("COURSE_JWT_AUDIENCE") ?? DefaultAudience;
        SigningKey = Environment.GetEnvironmentVariable("COURSE_JWT_SIGNING_KEY") ?? DefaultSigningKey;
    }

    public HttpClient CreateClient()
    {
        return new HttpClient
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public string CreateToken(
        string subject = "test-user",
        params string[] scopes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var claims = new List<System.Security.Claims.Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("consumer", "test-consumer"),
            new("scope", string.Join(' ', scopes))
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateExpiredToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: new[]
            {
                new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, "expired-user")
            },
            notBefore: now.AddMinutes(-10),
            expires: now.AddMinutes(-5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected JSON response, got HTTP {(int)response.StatusCode}: {text}\n{ex.Message}");
        }
    }

    public static void SetBearer(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");
}
