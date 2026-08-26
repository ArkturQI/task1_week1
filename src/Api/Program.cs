using Api.Endpoints;
using Api.Services;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var connStr = builder.Configuration.GetConnectionString("Course")
    ?? "Host=postgres;Port=5432;Database=course;Username=postgres;Password=postgres";

var jwtIssuer = builder.Configuration["COURSE_JWT_ISSUER"] ?? "moduledev-course";
var jwtAudience = builder.Configuration["COURSE_JWT_AUDIENCE"] ?? "moduledev-api";
var jwtSigningKey = builder.Configuration["COURSE_JWT_SIGNING_KEY"] ?? "this-is-a-very-long-and-secure-secret-key-for-jwt-signing-at-least-32-bytes";

var jwtValidator = new JwtValidator(jwtIssuer, jwtAudience, jwtSigningKey);

await Api.DATA.DbMigrator.MigrateAsync(connStr, app.Logger);

HealthEndpoints.Map(app, connStr);
OpenApiEndpoints.Map(app, connStr);
ActionEndpoints.Map(app, connStr, jwtValidator);

app.Run();