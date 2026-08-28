using Api.Services;
using Xunit;

namespace Week1.Tests;

public sealed class UnitTests
{
    private readonly TestEnvironment _env = new();

    [Fact]
    public void JwtValidator_RejectsMissingAuthorization()
    {
        var validator = new JwtValidator(
            _env.Issuer,
            _env.Audience,
            _env.SigningKey);

        var token = validator.Validate(null, out var error);

        Assert.Null(token);
        Assert.Equal("missing bearer token", error);
    }

    [Fact]
    public void JwtValidator_RejectsExpiredToken()
    {
        var validator = new JwtValidator(
            _env.Issuer,
            _env.Audience,
            _env.SigningKey);

        var expired = _env.CreateExpiredToken();

        var token = validator.Validate($"Bearer {expired}", out var error);

        Assert.Null(token);
        Assert.Equal("token validation failed", error);
    }

    [Fact]
    public void JwtValidator_AcceptsValidToken()
    {
        var validator = new JwtValidator(
            _env.Issuer,
            _env.Audience,
            _env.SigningKey);

        var jwt = _env.CreateToken("unit-user", "workflow:execute");

        var token = validator.Validate($"Bearer {jwt}", out var error);

        Assert.NotNull(token);
        Assert.Null(error);
        Assert.Equal("unit-user", token!.Subject);
    }

    [Theory]
    [InlineData("auth.invalid", 401)]
    [InlineData("request.invalid", 400)]
    [InlineData("idempotency.required", 400)]
    [InlineData("access.denied", 403)]
    [InlineData("action.not_found", 404)]
    [InlineData("operation.not_found", 404)]
    [InlineData("idempotency.conflict", 409)]
    [InlineData("payload.invalid", 422)]
    [InlineData("dependency.unavailable", 503)]
    [InlineData("action.contract_violation", 500)]
    [InlineData("action.timeout", 504)]
    public void EnvelopeBuilder_MapsKnownCodes(string code, int expectedStatus)
    {
        Assert.Equal(expectedStatus, EnvelopeBuilder.MapHttpCode(code));
    }

    [Fact]
    public void EnvelopeBuilder_UnknownCodeMapsTo500()
    {
        Assert.Equal(500, EnvelopeBuilder.MapHttpCode("something.unknown"));
    }
}
