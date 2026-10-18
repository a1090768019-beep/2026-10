using BusinessERP.Api.Endpoints;
using BusinessERP.Api.Security;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddAuthentication();

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<ErpDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<TenantMembershipVerifier>();
    builder.Services.AddScoped<TenantTransactionRunner>();
    builder.Services.AddScoped<TenantContext>();
    builder.Services.AddScoped<ITenantContext>(services => services.GetRequiredService<TenantContext>());
}

var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
var hasIdentityProvider = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience);
if (hasIdentityProvider)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false;
        });
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}
else
{
    builder.Services.AddAuthorization();
}

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous()
    .WithTags("Health");

app.MapGet("/health/ready", async Task<IResult> (IServiceProvider services, IConfiguration configuration, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")))
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Database is not configured", detail: "Set ConnectionStrings:Postgres to enable readiness.");

    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
    try
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        return canConnect
            ? Results.Ok(new { status = "ready", database = "reachable" })
            : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Database is unavailable");
    }
    catch
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Database is unavailable");
    }
})
.AllowAnonymous()
.WithTags("Health");

app.MapAccountingEndpoints();
app.MapChartOfAccountsTemplateEndpoints();
app.Run();

public partial class Program { }
