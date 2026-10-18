using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace BusinessERP.Desktop.Services;

/// <summary>
/// Authenticated tenant-scoped API facade used by the desktop UI. Each HTTP request carries
/// X-Tenant-Id; the API remains responsible for validating membership and authorization.
/// </summary>
public sealed class ErpApiClient(HttpClient httpClient, Guid tenantId)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly Guid _tenantId = tenantId != Guid.Empty
        ? tenantId
        : throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));

    public Task<IReadOnlyList<ApiCompany>> GetCompaniesAsync(CancellationToken ct = default) =>
        GetListAsync<ApiCompany>("/api/v1/companies", ct);

    public Task<ApiCompany> CreateCompanyAsync(CreateCompanyApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<ApiCompany>(HttpMethod.Post, "/api/v1/companies", request, ct);

    public Task<IReadOnlyList<ApiAccount>> GetAccountsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiAccount>($"/api/v1/companies/{companyId:D}/accounts", ct);

    public Task<IReadOnlyList<ApiFiscalPeriod>> GetFiscalPeriodsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiFiscalPeriod>($"/api/v1/companies/{companyId:D}/fiscal-periods", ct);

    public Task<SalesDraftLedgerPostingApiResult> PostSalesDraftToLedgerAsync(
        Guid companyId, Guid invoiceId, PostSalesDraftToLedgerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SalesDraftLedgerPostingApiResult>(
            HttpMethod.Post,
            $"/api/v1/companies/{companyId:D}/sales-invoice-drafts/{invoiceId:D}/post-to-ledger",
            request, ct);

    public Task<IReadOnlyList<ApiCustomer>> GetCustomersAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiCustomer>($"/api/v1/companies/{companyId:D}/customers", ct);

    public Task<ApiCustomer> CreateCustomerAsync(Guid companyId, CreateCustomerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<ApiCustomer>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/customers", request, ct);

    public Task<IReadOnlyList<SalesInvoiceDraftListItem>> GetSalesInvoiceDraftsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<SalesInvoiceDraftListItem>($"/api/v1/companies/{companyId:D}/sales-invoice-drafts", ct);

    public Task<SalesInvoiceDraftDetails> CreateSalesInvoiceDraftAsync(
        Guid companyId, CreateSalesInvoiceDraftApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SalesInvoiceDraftDetails>(
            HttpMethod.Post, $"/api/v1/companies/{companyId:D}/sales-invoice-drafts", request, ct);

    public Task<PersistedSalesDraftVatResult> CalculateAndPersistSalesDraftVatAsync(
        Guid companyId, Guid invoiceId, ApplySalesDraftVatApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<PersistedSalesDraftVatResult>(
            HttpMethod.Post,
            $"/api/v1/companies/{companyId:D}/sales-invoice-drafts/{invoiceId:D}/calculate-vat",
            request, ct);

    public Task<SalesVatPreviewApiResult> CalculateSalesVatPreviewAsync(
        Guid companyId, CalculateSalesVatPreviewApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SalesVatPreviewApiResult>(
            HttpMethod.Post, $"/api/v1/companies/{companyId:D}/sales-vat-preview", request, ct);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw BuildException(response.StatusCode, payload);

        try
        {
            return JsonSerializer.Deserialize<List<T>>(payload, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new ErpApiException(response.StatusCode, "استجابة الخادم لا تطابق صيغة البيانات المتوقعة.");
        }
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw BuildException(response.StatusCode, payload);

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new JsonException("Empty JSON response.");
        }
        catch (JsonException)
        {
            throw new ErpApiException(response.StatusCode, "لم يتمكن التطبيق من قراءة استجابة الخادم.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", _tenantId.ToString("D"));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static ErpApiException BuildException(HttpStatusCode statusCode, string payload)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "انتهت صلاحية الدخول أو رفض موفر الهوية الطلب. أعد تسجيل الدخول.",
            HttpStatusCode.Forbidden => "رفض الخادم العملية؛ تحقق من عضوية المستأجر والصلاحيات.",
            HttpStatusCode.NotFound => "لم يعثر الخادم على السجل المطلوب ضمن هذا المستأجر والشركة.",
            HttpStatusCode.Conflict => "تعارضت العملية مع سجل موجود أو حالة البيانات الحالية.",
            _ => "تعذر إكمال العملية. راجع البيانات وحاول مرة أخرى."
        };

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var explicitMessage) &&
                explicitMessage.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(explicitMessage.GetString()))
            {
                message = explicitMessage.GetString()!;
            }
            else if (root.TryGetProperty("errors", out var errors) &&
                     errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in errors.EnumerateObject())
                {
                    if (item.Value.ValueKind == JsonValueKind.Array &&
                        item.Value.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first &&
                        !string.IsNullOrWhiteSpace(first.GetString()))
                    {
                        message = first.GetString()!;
                        break;
                    }
                }
            }
            else if (root.TryGetProperty("detail", out var detail) &&
                     detail.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                message = detail.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Do not show raw body or infrastructure details in the desktop UI.
        }

        return new ErpApiException(statusCode, message);
    }
}

public sealed class ErpApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record ApiCompany(Guid Id, string Name, string BaseCurrency, string? TaxNumber, DateTime CreatedAt);
public sealed record CreateCompanyApiRequest(string Name, string BaseCurrency = "SAR", string? TaxNumber = null);
public sealed record ApiAccount(Guid Id, string Code, string NameAr, string NameEn, string AccountType, string NormalBalance, Guid? ParentAccountId, bool IsActive, bool AllowPosting);
public sealed record ApiFiscalPeriod(Guid Id, Guid FiscalYearId, int PeriodNumber, DateOnly StartDate, DateOnly EndDate, string Status)
{
    public string DisplayName => $"الفترة {PeriodNumber}: {StartDate:yyyy-MM-dd} — {EndDate:yyyy-MM-dd} ({Status})";
}
public sealed record PostSalesDraftToLedgerApiRequest(
    Guid FiscalPeriodId, string EntryNumber, Guid ReceivableAccountId, Guid RevenueAccountId, Guid? OutputVatAccountId = null);
public sealed record SalesDraftLedgerPostingApiResult(
    Guid SalesInvoiceDraftId, Guid JournalEntryId, string EntryNumber, string JournalEntryStatus,
    DateOnly EntryDate, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount,
    decimal VatAmount, decimal TotalAmount, bool AlreadyPosted, string Message);
public sealed record ApiCustomer(Guid Id, string Code, string Name, string? Email, string? Phone, string? TaxNumber, bool IsActive, DateTime CreatedAt);
public sealed record CreateCustomerApiRequest(string Code, string Name, string? Email = null, string? Phone = null, string? TaxNumber = null);
public sealed record SalesInvoiceDraftListItem(
    Guid Id, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode,
    Guid CustomerId, string CustomerName, decimal SubtotalAmount, string TaxCalculationStatus,
    decimal? VatAmount, decimal? TotalAmount, string Status, DateTime CreatedAt, int LineCount);
public sealed record CreateSalesInvoiceDraftApiRequest(
    Guid CustomerId, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
    string? Notes, IReadOnlyList<CreateSalesInvoiceDraftLineApiRequest> Lines);
public sealed record CreateSalesInvoiceDraftLineApiRequest(string Description, decimal Quantity, decimal UnitPrice);
public sealed record SalesInvoiceDraftDetails(
    Guid Id, Guid CompanyId, Guid CustomerId, string InvoiceNumber, DateOnly IssueDate,
    DateOnly? DueDate, string CurrencyCode, string CustomerName, string? CustomerTaxNumber,
    decimal SubtotalAmount, string Notes, string Status, DateTime CreatedAt, IReadOnlyList<SalesInvoiceDraftLine> Lines);
public sealed record SalesInvoiceDraftLine(Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount);


public sealed record CalculateSalesVatPreviewApiRequest(IReadOnlyList<SalesVatPreviewLineApiRequest> Lines);
public sealed record SalesVatPreviewLineApiRequest(
    string Description, decimal Quantity, decimal UnitPrice, decimal DiscountAmount,
    string Treatment, string? TreatmentReason = null);
public sealed record SalesVatPreviewApiResult(
    Guid CompanyId, string CurrencyCode, string DocumentStatus, string Message,
    decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
    IReadOnlyList<SalesVatPreviewApiLineResult> Lines);
public sealed record SalesVatPreviewApiLineResult(
    int LineNumber, string Description, string Treatment, decimal GrossAmount, decimal DiscountAmount,
    decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);


public sealed record ApplySalesDraftVatApiRequest(IReadOnlyList<ApplySalesDraftVatLineApiRequest> Lines);
public sealed record ApplySalesDraftVatLineApiRequest(
    int LineNumber, decimal DiscountAmount, string Treatment, string? TreatmentReason = null);
public sealed record PersistedSalesDraftVatResult(
    Guid InvoiceId, Guid CompanyId, string TaxCalculationStatus, string Message,
    decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
    IReadOnlyList<PersistedSalesDraftVatLineResult> Lines);
public sealed record PersistedSalesDraftVatLineResult(
    int LineNumber, string Description, string Treatment, decimal GrossAmount, decimal DiscountAmount,
    decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);
