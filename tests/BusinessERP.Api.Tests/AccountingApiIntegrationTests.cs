using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BusinessERP.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
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
                $"GET companies returned {(int)companiesResponse.StatusCode}: {companiesBody}{Environment.NewLine}{TestExceptionHandler.LastException}");
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

        // Customer master records are tenant-scoped, audited and protect the unique customer code.
        var customerCode = "CUST-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var customerResponse = await client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/customers", new
        {
            code = customerCode,
            name = "عميل اختبار API",
            email = "sales@example.invalid",
            taxNumber = "300000000000003"
        });
        Assert.True(customerResponse.StatusCode == HttpStatusCode.Created,
            $"Create customer returned {(int)customerResponse.StatusCode}: {await customerResponse.Content.ReadAsStringAsync()}{Environment.NewLine}{TestExceptionHandler.LastException}");
        using var customerJson = await ParseJsonAsync(customerResponse);
        var customerId = customerJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(customerCode, customerJson.RootElement.GetProperty("code").GetString());

        using var duplicateCustomer = await client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/customers", new
        {
            code = customerCode,
            name = "تكرار يجب رفضه"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicateCustomer.StatusCode);

        // Two simultaneous creates using the same normalized customer code must serialize.
        var racingCustomerCode = "RACE-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var racingCustomerTaskA = client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/customers", new
        {
            code = racingCustomerCode,
            name = "Concurrent customer A"
        });
        var racingCustomerTaskB = client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/customers", new
        {
            code = racingCustomerCode,
            name = "Concurrent customer B"
        });
        using var racingCustomerA = await racingCustomerTaskA;
        using var racingCustomerB = await racingCustomerTaskB;
        var racingCustomerStatuses = new[] { racingCustomerA.StatusCode, racingCustomerB.StatusCode };
        Assert.Contains(HttpStatusCode.Created, racingCustomerStatuses);
        Assert.Contains(HttpStatusCode.Conflict, racingCustomerStatuses);

        // Persisted sales invoice draft lines must equal their header subtotal exactly.
        var invoiceNumber = "DRAFT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        using var draftResponse = await client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts",
            new
            {
                customerId,
                invoiceNumber,
                issueDate = new DateOnly(2026, 2, 15),
                dueDate = new DateOnly(2026, 3, 15),
                notes = "مسودة داخلية غير ضريبية",
                lines = new[]
                {
                    new { description = "خدمة أولى", quantity = 2.5m, unitPrice = 12.3456m },
                    new { description = "خدمة ثانية", quantity = 3m, unitPrice = 0.3333m }
                }
            });
        Assert.True(draftResponse.StatusCode == HttpStatusCode.Created,
            $"Create invoice draft returned {(int)draftResponse.StatusCode}: {await draftResponse.Content.ReadAsStringAsync()}{Environment.NewLine}{TestExceptionHandler.LastException}");
        using var draftJson = await ParseJsonAsync(draftResponse);
        var draftId = draftJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("DRAFT", draftJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(31.8639m, draftJson.RootElement.GetProperty("subtotalAmount").GetDecimal());
        Assert.Equal("عميل اختبار API", draftJson.RootElement.GetProperty("customerName").GetString());
        Assert.Equal(2, draftJson.RootElement.GetProperty("lines").GetArrayLength());
        Assert.False(draftJson.RootElement.TryGetProperty("vatAmount", out _));

        // Competing requests for the same invoice number: at most one draft may commit.
        var racingInvoiceNumber = "DRAFT-RACE-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        object RacingDraftPayload() => new
        {
            customerId,
            invoiceNumber = racingInvoiceNumber,
            issueDate = new DateOnly(2026, 2, 16),
            dueDate = (DateOnly?)null,
            notes = "race test",
            lines = new[] { new { description = "Race line", quantity = 1m, unitPrice = 25m } }
        };
        var racingDraftTaskA = client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts", RacingDraftPayload());
        var racingDraftTaskB = client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts", RacingDraftPayload());
        using var racingDraftA = await racingDraftTaskA;
        using var racingDraftB = await racingDraftTaskB;
        var racingDraftStatuses = new[] { racingDraftA.StatusCode, racingDraftB.StatusCode };
        Assert.Contains(HttpStatusCode.Created, racingDraftStatuses);
        Assert.Contains(HttpStatusCode.Conflict, racingDraftStatuses);

        using (var duplicateDraft = await client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts",
            new
            {
                customerId, invoiceNumber, issueDate = new DateOnly(2026, 2, 15),
                dueDate = (DateOnly?)null, notes = "", lines = new[]
                {
                    new { description = "بند", quantity = 1m, unitPrice = 1m }
                }
            }))
            Assert.Equal(HttpStatusCode.Conflict, duplicateDraft.StatusCode);

        using (var draftList = await client.GetAsync($"/api/v1/companies/{CompanyA}/sales-invoice-drafts"))
        {
            Assert.Equal(HttpStatusCode.OK, draftList.StatusCode);
            using var rows = await ParseJsonAsync(draftList);
            Assert.Contains(rows.RootElement.EnumerateArray(),
                row => row.GetProperty("id").GetGuid() == draftId &&
                       row.GetProperty("lineCount").GetInt32() == 2);
        }

        using (var draftDetails = await client.GetAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts/{draftId}"))
        {
            Assert.Equal(HttpStatusCode.OK, draftDetails.StatusCode);
            using var details = await ParseJsonAsync(draftDetails);
            Assert.Equal(31.8639m, details.RootElement.GetProperty("subtotalAmount").GetDecimal());
            Assert.Equal(30.864m, details.RootElement.GetProperty("lines")[0].GetProperty("lineAmount").GetDecimal());
        }

        using (var invalidDraft = await client.PostAsJsonAsync(
            $"/api/v1/companies/{CompanyA}/sales-invoice-drafts",
            new
            {
                customerId, invoiceNumber = "INVALID-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                issueDate = new DateOnly(2026, 2, 15), dueDate = (DateOnly?)null, notes = "",
                lines = new[] { new { description = "كمية صفر", quantity = 0m, unitPrice = 10m } }
            }))
            Assert.Equal(HttpStatusCode.BadRequest, invalidDraft.StatusCode);

        // Two overlapping fiscal-year requests for the same new company must not both commit.
        using var newCompanyResponse = await client.PostAsJsonAsync("/api/v1/companies", new
        {
            name = "CI Concurrency Company " + Guid.NewGuid().ToString("N"),
            baseCurrency = "SAR"
        });
        Assert.Equal(HttpStatusCode.Created, newCompanyResponse.StatusCode);
        using var newCompanyJson = await ParseJsonAsync(newCompanyResponse);
        var concurrencyCompanyId = newCompanyJson.RootElement.GetProperty("id").GetGuid();
        var fiscalYearUrl = $"/api/v1/companies/{concurrencyCompanyId}/fiscal-years";
        var fiscalYearPayloadA = new
        {
            name = "Concurrent Year A",
            startDate = new DateOnly(2027, 1, 1),
            endDate = new DateOnly(2027, 12, 31)
        };
        var fiscalYearPayloadB = new
        {
            name = "Concurrent Year B",
            startDate = new DateOnly(2027, 1, 1),
            endDate = new DateOnly(2027, 12, 31)
        };
        var fiscalYearTaskA = client.PostAsJsonAsync(fiscalYearUrl, fiscalYearPayloadA);
        var fiscalYearTaskB = client.PostAsJsonAsync(fiscalYearUrl, fiscalYearPayloadB);
        using var fiscalYearResponseA = await fiscalYearTaskA;
        using var fiscalYearResponseB = await fiscalYearTaskB;
        var fiscalYearStatuses = new[] { fiscalYearResponseA.StatusCode, fiscalYearResponseB.StatusCode };
        Assert.Contains(HttpStatusCode.Created, fiscalYearStatuses);
        Assert.Contains(HttpStatusCode.Conflict, fiscalYearStatuses);

        var idempotencyKey = "api-test-" + Guid.NewGuid().ToString("N");
        var idempotentNumber = "IT-IDEM-" + Guid.NewGuid().ToString("N");
        var idempotentPayload = NewJournalPayload(idempotentNumber, "Idempotency retry", 20m);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        // Start both requests before awaiting either response to exercise the transaction lock under contention.
        var firstCreateTask = client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/journal-entries", idempotentPayload);
        var secondCreateTask = client.PostAsJsonAsync($"/api/v1/companies/{CompanyA}/journal-entries", idempotentPayload);
        using var firstCreate = await firstCreateTask;
        using var secondCreate = await secondCreateTask;
        var statuses = new[] { firstCreate.StatusCode, secondCreate.StatusCode };
        Assert.Contains(HttpStatusCode.Created, statuses);
        Assert.Contains(HttpStatusCode.OK, statuses);

        using var firstJson = await ParseJsonAsync(firstCreate);
        using var secondJson = await ParseJsonAsync(secondCreate);
        var firstEntryId = firstJson.RootElement.GetProperty("id").GetGuid();
        var secondEntryId = secondJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(firstEntryId, secondEntryId);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");

        // A duplicate entry number with different idempotency keys must produce one creation and one conflict.
        var competingEntryNumber = "IT-RACE-" + Guid.NewGuid().ToString("N");
        var competingPayload = NewJournalPayload(competingEntryNumber, "Competing entry-number requests", 12m);
        using var competingRequestA = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/companies/{CompanyA}/journal-entries")
        {
            Content = JsonContent.Create(competingPayload)
        };
        using var competingRequestB = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/companies/{CompanyA}/journal-entries")
        {
            Content = JsonContent.Create(competingPayload)
        };
        competingRequestA.Headers.Add("Idempotency-Key", "race-a-" + Guid.NewGuid().ToString("N"));
        competingRequestB.Headers.Add("Idempotency-Key", "race-b-" + Guid.NewGuid().ToString("N"));
        var competingTaskA = client.SendAsync(competingRequestA);
        var competingTaskB = client.SendAsync(competingRequestB);
        using var competingResponseA = await competingTaskA;
        using var competingResponseB = await competingTaskB;
        var competingStatuses = new[] { competingResponseA.StatusCode, competingResponseB.StatusCode };
        Assert.Contains(HttpStatusCode.Created, competingStatuses);
        Assert.Contains(HttpStatusCode.Conflict, competingStatuses);

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

        using (var incomeResponse = await client.GetAsync(
            $"/api/v1/companies/{CompanyA}/reports/income-statement?fromDate=2026-02-01&toDate=2026-02-28"))
        {
            Assert.Equal(HttpStatusCode.OK, incomeResponse.StatusCode);
            using var income = await ParseJsonAsync(incomeResponse);
            Assert.Equal(0m, income.RootElement.GetProperty("totalRevenue").GetDecimal());
            Assert.Equal(0m, income.RootElement.GetProperty("totalExpenses").GetDecimal());
            Assert.Equal(0m, income.RootElement.GetProperty("netIncome").GetDecimal());
        }

        using (var balanceResponse = await client.GetAsync(
            $"/api/v1/companies/{CompanyA}/reports/balance-sheet?asOfDate=2026-02-28"))
        {
            Assert.Equal(HttpStatusCode.OK, balanceResponse.StatusCode);
            using var balance = await ParseJsonAsync(balanceResponse);
            Assert.Equal(225.50m, balance.RootElement.GetProperty("totalAssets").GetDecimal());
            Assert.Equal(0m, balance.RootElement.GetProperty("totalLiabilities").GetDecimal());
            Assert.Equal(225.50m, balance.RootElement.GetProperty("totalEquity").GetDecimal());
            Assert.Equal(0m, balance.RootElement.GetProperty("difference").GetDecimal());
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
            services.AddSingleton<IExceptionHandler, TestExceptionHandler>();
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

public sealed class TestExceptionHandler : IExceptionHandler
{
    public static string? LastException { get; private set; }

    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        LastException = exception.ToString();
        return ValueTask.FromResult(false);
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
