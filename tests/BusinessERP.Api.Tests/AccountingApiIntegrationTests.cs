using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BusinessERP.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BusinessERP.Api.Tests;

public sealed class AccountingApiIntegrationTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid CompanyA = Guid.Parse("11111111-1111-4111-8111-111111111101");
    private static readonly Guid PeriodA = Guid.Parse("11111111-1111-4111-8111-111111111103");
    private static readonly Guid CashAccountA = Guid.Parse("11111111-1111-4111-8111-111111111104");
    private static readonly Guid EquityAccountA = Guid.Parse("11111111-1111-4111-8111-111111111105");

    [RequiresApiPostgresFact]
    public async Task Tenant_scoping_idempotency_journal_workflow_and_reports_work_end_to_end()
    {
        var connectionString = Environment.GetEnvironmentVariable("BUSINESS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        using var factory = new ApiTestFactory(connectionString!);
        using var client = factory.CreateClient();
        SetTenant(client, TenantA);

        using (var companiesResponse = await client.GetAsync("/api/v1/companies"))
        {
            var companiesBody = await companiesResponse.Content.ReadAsStringAsync();
            Assert.True(companiesResponse.StatusCode == HttpStatusCode.OK,
                $"GET companies returned {(int)companiesResponse.StatusCode}: {companiesBody}");
            using var companies = JsonDocument.Parse(companiesBody);
            Assert.Contains(companies.RootElement.EnumerateArray(),
                company => company.GetProperty("id").GetGuid() == CompanyA);
        }

        using (var subscriptionResponse = await client.GetAsync("/api/v1/subscription"))
        {
            Assert.Equal(HttpStatusCode.OK, subscriptionResponse.StatusCode);
            using var subscription = await ParseJsonAsync(subscriptionResponse);
            Assert.True(subscription.RootElement.GetProperty("hasSubscription").GetBoolean());
            Assert.True(subscription.RootElement.GetProperty("isEntitled").GetBoolean());
            Assert.Equal("CI-STARTER", subscription.RootElement.GetProperty("plan").GetProperty("code").GetString());
        }

        // Authenticated identity A cannot select tenant B merely by changing the tenant header.
        SetTenant(client, TenantB);
        using (var forbidden = await client.GetAsync("/api/v1/companies"))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        SetTenant(client, TenantA);
        var idempotencyKey = "api-test-" + Guid.NewGuid().ToString("N");
        var idempotentNumber = "IT-IDEM-" + Guid.NewGuid().ToString("N");
        var idempotentPayload = NewJournalPayload(idempotentNumber, "Idempotency retry", 20m);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        using var firstCreate = await client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/journal-entries", idempotentPayload);
        Assert.Equal(HttpStatusCode.Created, firstCreate.StatusCode);
        using var firstJson = await ParseJsonAsync(firstCreate);
        var idempotentEntryId = firstJson.RootElement.GetProperty("id").GetGuid();

        using var retryCreate = await client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/journal-entries", idempotentPayload);
        Assert.Equal(HttpStatusCode.OK, retryCreate.StatusCode);
        using var retryJson = await ParseJsonAsync(retryCreate);
        Assert.Equal(idempotentEntryId, retryJson.RootElement.GetProperty("id").GetGuid());

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        using (var entriesResponse = await client.GetAsync($"/api/v1/companies/{CompanyA}/journal-entries?limit=200"))
        {
            Assert.Equal(HttpStatusCode.OK, entriesResponse.StatusCode);
            using var entries = await ParseJsonAsync(entriesResponse);
            var duplicateCount = entries.RootElement.EnumerateArray()
                .Count(entry => entry.GetProperty("entryNumber").GetString() == idempotentNumber);
            Assert.Equal(1, duplicateCount);
        }

        var entryNumber = "IT-POST-" + Guid.NewGuid().ToString("N");
        using var createResponse = await client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/journal-entries",
            NewJournalPayload(entryNumber, "API integration journal", 125.50m));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var created = await ParseJsonAsync(createResponse);
        var entryId = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("DRAFT", created.RootElement.GetProperty("status").GetString());
        Assert.Equal(125.50m, created.RootElement.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(125.50m, created.RootElement.GetProperty("totalCredit").GetDecimal());

        using (var approval = await client.PostAsync($"/api/v1/journal-entries/{entryId}/approve", null))
            Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        using (var posting = await client.PostAsync($"/api/v1/journal-entries/{entryId}/post", null))
            Assert.Equal(HttpStatusCode.OK, posting.StatusCode);

        using (var ledgerResponse = await client.GetAsync(
            $"/api/v1/companies/{CompanyA}/accounts/{CashAccountA}/ledger?fromDate=2026-02-01&toDate=2026-02-28"))
        {
            Assert.Equal(HttpStatusCode.OK, ledgerResponse.StatusCode);
            using var ledger = await ParseJsonAsync(ledgerResponse);
            Assert.Equal(100m, ledger.RootElement.GetProperty("openingBalance").GetDecimal());
            Assert.Equal(125.50m, ledger.RootElement.GetProperty("totalDebit").GetDecimal());
            Assert.Equal(225.50m, ledger.RootElement.GetProperty("closingBalance").GetDecimal());
        }

        using (var trialBalanceResponse = await client.GetAsync(
            $"/api/v1/companies/{CompanyA}/reports/trial-balance?fromDate=2026-02-01&toDate=2026-02-28"))
        {
            Assert.Equal(HttpStatusCode.OK, trialBalanceResponse.StatusCode);
            using var trialBalance = await ParseJsonAsync(trialBalanceResponse);
            Assert.Equal(125.50m, trialBalance.RootElement.GetProperty("totalDebitMovement").GetDecimal());
            Assert.Equal(125.50m, trialBalance.RootElement.GetProperty("totalCreditMovement").GetDecimal());
        }

        var reversalNumber = "IT-REV-" + Guid.NewGuid().ToString("N");
        using var reversalResponse = await client.PostAsJsonAsync(
            $"/api/v1/journal-entries/{entryId}/reverse",
            new
            {
                fiscalPeriodId = PeriodA,
                entryNumber = reversalNumber,
                entryDate = new DateOnly(2026, 2, 11),
                description = "Integration test reversal"
            });
        Assert.Equal(HttpStatusCode.Created, reversalResponse.StatusCode);
        using var reversal = await ParseJsonAsync(reversalResponse);
        Assert.Equal("POSTED", reversal.RootElement.GetProperty("status").GetString());
        Assert.Equal(entryId, reversal.RootElement.GetProperty("reversalOfEntryId").GetGuid());

        using var duplicateReversal = await client.PostAsJsonAsync(
            $"/api/v1/journal-entries/{entryId}/reverse",
            new
            {
                fiscalPeriodId = PeriodA,
                entryNumber = "IT-REV-DUP-" + Guid.NewGuid().ToString("N"),
                entryDate = new DateOnly(2026, 2, 12),
                description = "Duplicate reversal must be rejected"
            });
        Assert.Equal(HttpStatusCode.Conflict, duplicateReversal.StatusCode);
    }

    private static object NewJournalPayload(string entryNumber, string description, decimal amount) => new
    {
        fiscalPeriodId = PeriodA,
        entryNumber,
        entryDate = new DateOnly(2026, 2, 10),
        description,
        currencyCode = "SAR",
        lines = new[]
        {
            new { accountId = CashAccountA, debit = amount, credit = 0m, description = "Cash debit" },
            new { accountId = EquityAccountA, debit = 0m, credit = amount, description = "Equity credit" }
        }
    };

    private static void SetTenant(HttpClient client, Guid tenantId)
    {
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString("D"));
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success but received {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }
}

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private const string Authority = "https://oidc.example.invalid/";
    private const string Audience = "business-erp-api";
    private readonly string _connectionString;
    private readonly string? _previousConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    private readonly string? _previousAuthority = Environment.GetEnvironmentVariable("Authentication__Authority");
    private readonly string? _previousAudience = Environment.GetEnvironmentVariable("Authentication__Audience");

    public ApiTestFactory(string connectionString)
    {
        _connectionString = connectionString;
        // WebApplication.CreateBuilder reads process environment during Program startup. Set these
        // before CreateClient starts the host so the real DI registrations are exercised.
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", connectionString);
        Environment.SetEnvironmentVariable("Authentication__Authority", Authority);
        Environment.SetEnvironmentVariable("Authentication__Audience", Audience);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Error);
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _connectionString,
                ["Authentication:Authority"] = Authority,
                ["Authentication:Audience"] = Audience
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _previousConnectionString);
        Environment.SetEnvironmentVariable("Authentication__Authority", _previousAuthority);
        Environment.SetEnvironmentVariable("Authentication__Audience", _previousAudience);
    }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "BusinessERP.Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", "ci-subject-a"),
            new Claim(ClaimTypes.Name, "CI Test User")
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class RequiresApiPostgresFactAttribute : FactAttribute
{
    public RequiresApiPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BUSINESS_ERP_TEST_CONNECTION")))
        {
            Skip = "Set BUSINESS_ERP_TEST_CONNECTION to a disposable PostgreSQL database with the CI fixtures applied.";
        }
    }
}
