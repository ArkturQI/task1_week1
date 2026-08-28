using Api.Endpoints;
using Api.Services;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var connStr = builder.Configuration.GetConnectionString("Course")
    ?? "Host=postgres;Port=5432;Database=course;Username=course_api_login;Password=api_secret_change_me";

var jwtIssuer = builder.Configuration["COURSE_JWT_ISSUER"] ?? "moduledev-course";
var jwtAudience = builder.Configuration["COURSE_JWT_AUDIENCE"] ?? "moduledev-api";
var jwtSigningKey = builder.Configuration["COURSE_JWT_SIGNING_KEY"] ?? "moduledev-week1-rotated-key-do-not-use-in-production-2026-aug";

var jwtValidator = new JwtValidator(jwtIssuer, jwtAudience, jwtSigningKey);

await Api.DATA.DbMigrator.MigrateAsync(connStr, app.Logger);

HealthEndpoints.Map(app, connStr);
OpenApiEndpoints.Map(app, connStr);
ActionEndpoints.Map(app, connStr, jwtValidator);

app.Run();
