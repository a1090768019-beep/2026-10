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
public sealed record ApiCustomer(Guid Id, string Code, string Name, string? Email, string? Phone, string? TaxNumber, bool IsActive, DateTime CreatedAt);
public sealed record CreateCustomerApiRequest(string Code, string Name, string? Email = null, string? Phone = null, string? TaxNumber = null);
public sealed record SalesInvoiceDraftListItem(
    Guid Id, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode,
    Guid CustomerId, string CustomerName, decimal SubtotalAmount, string Status, DateTime CreatedAt, int LineCount);
public sealed record CreateSalesInvoiceDraftApiRequest(
    Guid CustomerId, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
    string? Notes, IReadOnlyList<CreateSalesInvoiceDraftLineApiRequest> Lines);
public sealed record CreateSalesInvoiceDraftLineApiRequest(string Description, decimal Quantity, decimal UnitPrice);
public sealed record SalesInvoiceDraftDetails(
    Guid Id, Guid CompanyId, Guid CustomerId, string InvoiceNumber, DateOnly IssueDate,
    DateOnly? DueDate, string CurrencyCode, string CustomerName, string? CustomerTaxNumber,
    decimal SubtotalAmount, string Notes, string Status, DateTime CreatedAt, IReadOnlyList<SalesInvoiceDraftLine> Lines);
public sealed record SalesInvoiceDraftLine(Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount);
