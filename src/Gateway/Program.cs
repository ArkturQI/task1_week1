var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var apiBase = builder.Configuration["Api__BaseUrl"] ?? "http://api:8080";
var client = new HttpClient
{
    BaseAddress = new Uri(apiBase),
    Timeout = TimeSpan.FromSeconds(35)
};

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "gateway" }));

app.MapGet("/health/ready", async () =>
{
    try
    {
        var resp = await client.GetAsync("/health/ready");
        if (resp.IsSuccessStatusCode)
            return Results.Ok(new { status = "ok", service = "gateway", api = "up" });
        return Results.Json(new { status = "degraded", service = "gateway", api = "down" }, statusCode: 503);
    }
    catch
    {
        return Results.Json(new { status = "degraded", service = "gateway", api = "down" }, statusCode: 503);
    }
});

app.MapPost("/api/{module}/{action}", (Delegate)((HttpContext http, string module, string action) =>
    ProxyAsync(http, $"/api/{module}/{action}")));

app.MapGet("/openapi/default.json", (Delegate)((HttpContext http) =>
    ProxyAsync(http, "/openapi/default.json")));

app.MapGet("/openapi/actions/{module}/{action}/{version}.json", (Delegate)((HttpContext http, string module, string action, string version) =>
    ProxyAsync(http, $"/openapi/actions/{module}/{action}/{version}.json")));

app.MapMethods("/{**rest}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" }, () =>
    Results.Json(new { code = "route.not_found" }, statusCode: 404));

app.Run();

async Task<IResult> ProxyAsync(HttpContext http, string path)
{
    try
    {
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), path);

        foreach (var headerName in new[] { "Authorization", "Idempotency-Key", "X-Action-Version" })
        {
            if (http.Request.Headers.TryGetValue(headerName, out var values))
            {
                foreach (var value in values)
                {
                    if (value is not null)
                        request.Headers.TryAddWithoutValidation(headerName, value);
                }
            }
        }

        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
        if (ms.Length > 0)
        {
            request.Content = new ByteArrayContent(ms.ToArray());
            request.Content.Headers.TryAddWithoutValidation(
                "Content-Type", http.Request.ContentType ?? "application/json");
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, http.RequestAborted);
        var bodyText = await response.Content.ReadAsStringAsync(http.RequestAborted);
        return Results.Content(bodyText, "application/json", statusCode: (int)response.StatusCode);
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { status = "error", code = "action.timeout", message = "client aborted or timeout" }, statusCode: 504);
    }
    catch
    {
        return Results.Json(
            new { status = "error", code = "dependency.unavailable", message = "api unavailable" },
            statusCode: 503);
    }
}