using System.Net;
using System.Text.Json;
using Xunit;

namespace Week1.Tests;

[Collection("Gateway collection")]
public sealed class GatewayIntegrationTests
{
    private readonly TestEnvironment _env = new();

    [Fact]
    public async Task LiveHealth_IsOk()
    {
        using var client = _env.CreateClient();

        using var response = await client.GetAsync("/health/live");
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("gateway", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task ReadyHealth_IsOk()
    {
        using var client = _env.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("up", json.RootElement.GetProperty("api").GetString());
    }

    [Fact]
    public async Task MissingJwt_Returns401()
    {
        using var client = _env.CreateClient();
        using var content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"missing-jwt\"}");

        using var response = await client.PostAsync("/api/opencheck/probe", content);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidVersionHeader_Returns400()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(request, _env.CreateToken("version-user", "workflow:execute"));
        request.Headers.Add("X-Action-Version", "not-a-number");
        request.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"invalid-version\"}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.invalid", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownAction_DoesNotReachTarget_Returns404()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/does-not-exist");
        TestEnvironment.SetBearer(request, _env.CreateToken("unknown-action-user", "workflow:execute"));
        request.Content = TestEnvironment.Json("{}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("action.not_found", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingIdempotencyKey_Returns400()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(request, _env.CreateToken("idempotency-user", "workflow:execute"));
        request.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"missing-idempotency\"}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency.required", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingPolicyScope_Returns403()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(request, _env.CreateToken("policy-user"));
        request.Headers.Add("Idempotency-Key", "policy-test-" + Guid.NewGuid().ToString("N"));
        request.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"policy-denied\"}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("access.denied", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExplicitVersionV1_ReturnsRevision1()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(request, _env.CreateToken("version-v1-user", "workflow:execute"));
        request.Headers.Add("X-Action-Version", "1");
        request.Headers.Add("Idempotency-Key", "v1-" + Guid.NewGuid().ToString("N"));
        request.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"v1-" + Guid.NewGuid().ToString("N") + "\"}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("APPLIED", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("result").GetProperty("revision").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("meta").GetProperty("actionVersion").GetInt32());
    }

    [Fact]
    public async Task ExplicitVersionV2_ReturnsRevision2()
    {
        using var client = _env.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(request, _env.CreateToken("version-v2-user", "workflow:execute"));
        request.Headers.Add("X-Action-Version", "2");
        request.Headers.Add("Idempotency-Key", "v2-" + Guid.NewGuid().ToString("N"));
        request.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"v2-" + Guid.NewGuid().ToString("N") + "\"}");

        using var response = await client.SendAsync(request);
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("APPLIED", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("result").GetProperty("revision").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("meta").GetProperty("actionVersion").GetInt32());
    }

    [Fact]
    public async Task Idempotency_SameKeyReplay_ReturnsStableResult()
    {
        using var client = _env.CreateClient();
        var key = "replay-" + Guid.NewGuid().ToString("N");
        var value = "replay-value-" + Guid.NewGuid().ToString("N");
        var body = $"{{\"mode\":\"ok\",\"value\":\"{value}\"}}";
        var token = _env.CreateToken("replay-user", "workflow:execute");

        async Task<JsonDocument> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
            TestEnvironment.SetBearer(request, token);
            request.Headers.Add("Idempotency-Key", key);
            request.Content = TestEnvironment.Json(body);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await TestEnvironment.ReadJsonAsync(response);
        }

        using var first = await SendAsync();
        using var second = await SendAsync();

        var firstResult = first.RootElement.GetProperty("result");
        var secondResult = second.RootElement.GetProperty("result");

        Assert.Equal(firstResult.GetProperty("stored").GetBoolean(), secondResult.GetProperty("stored").GetBoolean());
        Assert.Equal(firstResult.GetProperty("revision").GetInt32(), secondResult.GetProperty("revision").GetInt32());
        Assert.Equal(firstResult.GetProperty("principal").GetString(), secondResult.GetProperty("principal").GetString());
    }

    [Fact]
    public async Task Idempotency_SameKeyDifferentPayload_Returns409()
    {
        using var client = _env.CreateClient();
        var key = "conflict-" + Guid.NewGuid().ToString("N");
        var token = _env.CreateToken("conflict-user", "workflow:execute");

        using (var first = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe"))
        {
            TestEnvironment.SetBearer(first, token);
            first.Headers.Add("Idempotency-Key", key);
            first.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"conflict-a-" + Guid.NewGuid().ToString("N") + "\"}");
            using var response = await client.SendAsync(first);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        TestEnvironment.SetBearer(second, token);
        second.Headers.Add("Idempotency-Key", key);
        second.Content = TestEnvironment.Json("{\"mode\":\"ok\",\"value\":\"conflict-b-" + Guid.NewGuid().ToString("N") + "\"}");

        using var conflictResponse = await client.SendAsync(second);
        using var json = await TestEnvironment.ReadJsonAsync(conflictResponse);

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("idempotency.conflict", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Idempotency_ConcurrentRequests_DoNotFail()
    {
        using var client = _env.CreateClient();
        var key = "concurrent-" + Guid.NewGuid().ToString("N");
        var value = "concurrent-value-" + Guid.NewGuid().ToString("N");
        var token = _env.CreateToken("concurrent-user", "workflow:execute");
        var body = $"{{\"mode\":\"ok\",\"value\":\"{value}\"}}";

        async Task<(HttpStatusCode Status, JsonDocument? Json)> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
            TestEnvironment.SetBearer(request, token);
            request.Headers.Add("Idempotency-Key", key);
            request.Content = TestEnvironment.Json(body);

            using var response = await client.SendAsync(request);
            var json = await TestEnvironment.ReadJsonAsync(response);
            return (response.StatusCode, json);
        }

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => SendAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        try
        {
            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
            Assert.All(results, r =>
            {
                Assert.NotNull(r.Json);
                Assert.Equal("ok", r.Json!.RootElement.GetProperty("status").GetString());
                Assert.Equal("APPLIED", r.Json.RootElement.GetProperty("outcome").GetString());
            });
        }
        finally
        {
            foreach (var result in results)
                result.Json?.Dispose();
        }
    }

    [Fact]
    public async Task PaymentRequest_ThenOperationGet_WorksWithSeparatePolicies()
    {
        using var client = _env.CreateClient();
        var idempotencyKey = "payment-" + Guid.NewGuid().ToString("N");

        using var paymentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/payment/request");
        TestEnvironment.SetBearer(paymentRequest, _env.CreateToken("payment-user", "payment:write"));
        paymentRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        paymentRequest.Content = TestEnvironment.Json("{\"operationKind\":\"PAYMENT_EXECUTION\",\"amount\":\"125.50\",\"currency\":\"RUB\"}");

        using var paymentResponse = await client.SendAsync(paymentRequest);
        using var paymentJson = await TestEnvironment.ReadJsonAsync(paymentResponse);

        Assert.Equal(HttpStatusCode.OK, paymentResponse.StatusCode);
        Assert.Equal("CREATED", paymentJson.RootElement.GetProperty("outcome").GetString());

        var operationId = paymentJson.RootElement
            .GetProperty("result")
            .GetProperty("operationId")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(operationId));

        using var getRequest = new HttpRequestMessage(HttpMethod.Post, "/api/operation/get");
        TestEnvironment.SetBearer(getRequest, _env.CreateToken("payment-user", "payment:read"));
        getRequest.Content = TestEnvironment.Json($"{{\"operationId\":\"{operationId}\"}}");

        using var getResponse = await client.SendAsync(getRequest);
        using var getJson = await TestEnvironment.ReadJsonAsync(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal("FOUND", getJson.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, getJson.RootElement.GetProperty("result").GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task OpenApi_Default_ContainsEnvelopeSchema()
    {
        using var client = _env.CreateClient();

        using var response = await client.GetAsync("/openapi/default.json");
        using var json = await TestEnvironment.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var paths = json.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/payment/request", out var paymentPath));

        var schema = paymentPath
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        var required = schema.GetProperty("required");
        var requiredNames = required.EnumerateArray().Select(x => x.GetString()).ToHashSet();

        Assert.Contains("status", requiredNames);
        Assert.Contains("outcome", requiredNames);
        Assert.Contains("result", requiredNames);
        Assert.Contains("meta", requiredNames);
    }
}

[CollectionDefinition("Gateway collection", DisableParallelization = true)]
public sealed class GatewayCollection : ICollectionFixture<TestEnvironment>
{
}
