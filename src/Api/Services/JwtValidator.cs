using System.IdentityModel.Tokens.Jwt;
using System.Text;
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
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            handler.ValidateToken(token, validationParams, out var validatedToken);
            return validatedToken as JwtSecurityToken
                ?? throw new SecurityTokenException("token is not a JWT");
        }
        catch (Exception ex)
        {
            errorMessage = "token validation failed";
            return null;
        }
    }
}