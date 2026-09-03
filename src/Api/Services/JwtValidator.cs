using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public sealed class JwtValidator
{
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _signingKey;

    public JwtValidator(string issuer, string audience, string signingKey)
    {
        _issuer = issuer;
        _audience = audience;
        _signingKey = signingKey;
    }

    public JwtSecurityToken? Validate(string? authHeader, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "missing bearer token";
            return null;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        try
        {
            if (!ValidateClaimShape(token))
                throw new SecurityTokenException("invalid claim shape");

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireSignedTokens = true
            };

            handler.ValidateToken(token, validationParams, out var validatedToken);
            var jwt = validatedToken as JwtSecurityToken
                ?? throw new SecurityTokenException("token is not a JWT");

            // Defense in depth: the checked claims must still be present after framework validation.
            if (string.IsNullOrWhiteSpace(jwt.Subject) ||
                string.IsNullOrWhiteSpace(jwt.Claims.FirstOrDefault(c => c.Type == "consumer")?.Value) ||
                jwt.Claims.FirstOrDefault(c => c.Type == "scope") is null)
            {
                throw new SecurityTokenException("required claims missing");
            }

            return jwt;
        }
        catch (Exception)
        {
            errorMessage = "token validation failed";
            return null;
        }
    }

    private static bool ValidateClaimShape(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[1]))
            return false;

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlEncoder.DecodeBytes(parts[1]);
        }
        catch
        {
            return false;
        }

        using var document = JsonDocument.Parse(payloadBytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        // These claims are part of the course contract and must be JSON strings.
        foreach (var name in new[] { "iss", "sub", "consumer", "scope" })
        {
            if (!root.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(root.GetProperty("iss").GetString()) &&
               !string.IsNullOrWhiteSpace(root.GetProperty("sub").GetString()) &&
               !string.IsNullOrWhiteSpace(root.GetProperty("consumer").GetString());
    }
}
