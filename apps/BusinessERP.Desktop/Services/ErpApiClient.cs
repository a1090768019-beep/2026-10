using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;

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

    public Task<SalesDraftLedgerPostingApiResult> PostSalesDraftToLedgerAsync(
        Guid companyId, Guid invoiceId, PostSalesDraftToLedgerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SalesDraftLedgerPostingApiResult>(
            HttpMethod.Post,
            $"/api/v1/companies/{companyId:D}/sales-invoice-drafts/{invoiceId:D}/post-to-ledger",
            request, ct);

    public Task<TrialBalanceApiReport> GetTrialBalanceAsync(
        Guid companyId, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) =>
        GetObjectAsync<TrialBalanceApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/trial-balance?fromDate={fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&toDate={toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ct);

    public Task<IncomeStatementApiReport> GetIncomeStatementAsync(
        Guid companyId, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) =>
        GetObjectAsync<IncomeStatementApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/income-statement?fromDate={fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&toDate={toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ct);

    public Task<SalesVatDetailsApiReport> GetSalesVatDetailsReportAsync(
        Guid companyId, DateOnly fromDate, DateOnly toDate, int page = 1, int pageSize = 100, CancellationToken ct = default) =>
        GetObjectAsync<SalesVatDetailsApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/sales-vat-details?fromDate={fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&toDate={toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&page={page}&pageSize={pageSize}",
            ct);

    public Task<BalanceSheetApiReport> GetBalanceSheetAsync(
        Guid companyId, DateOnly asOfDate, CancellationToken ct = default) =>
        GetObjectAsync<BalanceSheetApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/balance-sheet?asOfDate={asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            ct);

    public Task<IReadOnlyList<ApiCustomer>> GetCustomersAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiCustomer>($"/api/v1/companies/{companyId:D}/customers", ct);

    public Task<ApiCustomer> CreateCustomerAsync(Guid companyId, CreateCustomerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<ApiCustomer>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/customers", request, ct);

    public Task<IReadOnlyList<ApiAccount>> GetAccountsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiAccount>($"/api/v1/companies/{companyId:D}/accounts", ct);

    public Task<IReadOnlyList<ApiFiscalPeriod>> GetFiscalPeriodsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiFiscalPeriod>($"/api/v1/companies/{companyId:D}/fiscal-periods", ct);

    public Task<IReadOnlyList<InventoryItemApiDto>> GetInventoryItemsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<InventoryItemApiDto>($"/api/v1/companies/{companyId:D}/items", ct);

    public Task<InventoryItemApiDto> CreateInventoryItemAsync(Guid companyId, CreateInventoryItemApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryItemApiDto>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/items", request, ct);

    public Task<IReadOnlyList<InventoryWarehouseApiDto>> GetInventoryWarehousesAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<InventoryWarehouseApiDto>($"/api/v1/companies/{companyId:D}/warehouses", ct);

    public Task<InventoryWarehouseApiDto> CreateInventoryWarehouseAsync(Guid companyId, CreateInventoryWarehouseApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryWarehouseApiDto>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/warehouses", request, ct);

    public Task<StockBalancesApiReport> GetInventoryBalancesAsync(Guid companyId, int page = 1, int pageSize = 100,
        Guid? itemId = null, Guid? warehouseId = null, CancellationToken ct = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (itemId.HasValue) query += $"&itemId={itemId.Value:D}";
        if (warehouseId.HasValue) query += $"&warehouseId={warehouseId.Value:D}";
        return GetObjectAsync<StockBalancesApiReport>($"/api/v1/companies/{companyId:D}/inventory/balances?{query}", ct);
    }

    public Task<InventoryValuationApiReport> GetInventoryValuationAsync(Guid companyId, DateOnly asOfDate,
        int page = 1, int pageSize = 100, Guid? itemId = null, Guid? warehouseId = null, CancellationToken ct = default)
    {
        var query = $"asOfDate={asOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&page={page}&pageSize={pageSize}";
        if (itemId.HasValue) query += $"&itemId={itemId.Value:D}";
        if (warehouseId.HasValue) query += $"&warehouseId={warehouseId.Value:D}";
        return GetObjectAsync<InventoryValuationApiReport>($"/api/v1/companies/{companyId:D}/inventory/valuation?{query}", ct);
    }

    public Task<InventoryMovementsApiReport> GetInventoryMovementsAsync(Guid companyId, DateOnly? fromDate = null,
        DateOnly? toDate = null, Guid? itemId = null, Guid? warehouseId = null,
        int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (fromDate.HasValue) query += $"&fromDate={fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (toDate.HasValue) query += $"&toDate={toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (itemId.HasValue) query += $"&itemId={itemId.Value:D}";
        if (warehouseId.HasValue) query += $"&warehouseId={warehouseId.Value:D}";
        return GetObjectAsync<InventoryMovementsApiReport>($"/api/v1/companies/{companyId:D}/inventory/movements?{query}", ct);
    }

    public Task<InventoryMovementApiResult> ReceiveInventoryStockAsync(Guid companyId, InventoryMovementApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryMovementApiResult>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/inventory/receipts", request, ct);

    public Task<InventoryMovementApiResult> IssueInventoryStockAsync(Guid companyId, InventoryMovementApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryMovementApiResult>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/inventory/issues", request, ct);

    public Task<InventoryMovementLedgerPostingApiResult> PostInventoryMovementToLedgerAsync(
        Guid companyId, Guid movementId, PostInventoryMovementToLedgerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryMovementLedgerPostingApiResult>(HttpMethod.Post,
            $"/api/v1/companies/{companyId:D}/inventory/movements/{movementId:D}/post-to-ledger", request, ct);

    public Task<InventorySerialEventsApiReport> GetInventorySerialEventsAsync(Guid companyId,
        DateOnly? fromDate = null, DateOnly? toDate = null, Guid? itemId = null, Guid? warehouseId = null,
        string? status = null, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (fromDate.HasValue) query += $"&fromDate={fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (toDate.HasValue) query += $"&toDate={toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (itemId.HasValue) query += $"&itemId={itemId.Value:D}";
        if (warehouseId.HasValue) query += $"&warehouseId={warehouseId.Value:D}";
        if (!string.IsNullOrWhiteSpace(status)) query += $"&status={Uri.EscapeDataString(status)}";
        return GetObjectAsync<InventorySerialEventsApiReport>($"/api/v1/companies/{companyId:D}/inventory/serial-numbers?{query}", ct);
    }

    public Task<InventorySerialSummaryApiReport> GetInventorySerialSummaryAsync(Guid companyId,
        string? status = null, int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status)) query += $"&status={Uri.EscapeDataString(status)}";
        return GetObjectAsync<InventorySerialSummaryApiReport>($"/api/v1/companies/{companyId:D}/inventory/serial-numbers/summary?{query}", ct);
    }

    public Task<InventoryTransferApiResult> TransferInventoryStockAsync(Guid companyId, InventoryTransferApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<InventoryTransferApiResult>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/inventory/transfers", request, ct);

    public Task<InventoryTransfersApiReport> GetInventoryTransfersAsync(Guid companyId, DateOnly? fromDate = null,
        DateOnly? toDate = null, Guid? itemId = null, Guid? sourceWarehouseId = null, Guid? destinationWarehouseId = null,
        int page = 1, int pageSize = 100, CancellationToken ct = default)
    {
        var query = $"page={page}&pageSize={pageSize}";
        if (fromDate.HasValue) query += $"&fromDate={fromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (toDate.HasValue) query += $"&toDate={toDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (itemId.HasValue) query += $"&itemId={itemId.Value:D}";
        if (sourceWarehouseId.HasValue) query += $"&sourceWarehouseId={sourceWarehouseId.Value:D}";
        if (destinationWarehouseId.HasValue) query += $"&destinationWarehouseId={destinationWarehouseId.Value:D}";
        return GetObjectAsync<InventoryTransfersApiReport>($"/api/v1/companies/{companyId:D}/inventory/transfers?{query}", ct);
    }

    public Task<IReadOnlyList<ApiSupplier>> GetSuppliersAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<ApiSupplier>($"/api/v1/companies/{companyId:D}/suppliers", ct);

    public Task<ApiSupplier> CreateSupplierAsync(Guid companyId, CreateSupplierApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<ApiSupplier>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/suppliers", request, ct);

    public Task<IReadOnlyList<PurchaseInvoiceDraftListItem>> GetPurchaseInvoiceDraftsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<PurchaseInvoiceDraftListItem>($"/api/v1/companies/{companyId:D}/purchase-invoice-drafts", ct);

    public Task<PurchaseInvoiceDraftDetails> GetPurchaseInvoiceDraftAsync(Guid companyId, Guid invoiceId, CancellationToken ct = default) =>
        GetObjectAsync<PurchaseInvoiceDraftDetails>($"/api/v1/companies/{companyId:D}/purchase-invoice-drafts/{invoiceId:D}", ct);

    public Task<PurchaseInvoiceDraftDetails> CreatePurchaseInvoiceDraftAsync(
        Guid companyId, CreatePurchaseInvoiceDraftApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<PurchaseInvoiceDraftDetails>(
            HttpMethod.Post, $"/api/v1/companies/{companyId:D}/purchase-invoice-drafts", request, ct);

    public Task<PurchaseDraftVatResult> CalculateAndPersistPurchaseDraftVatAsync(
        Guid companyId, Guid invoiceId, ApplyPurchaseDraftVatApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<PurchaseDraftVatResult>(
            HttpMethod.Post, $"/api/v1/companies/{companyId:D}/purchase-invoice-drafts/{invoiceId:D}/calculate-vat", request, ct);

    public Task<PurchaseDraftLedgerPostingResult> PostPurchaseDraftToLedgerAsync(
        Guid companyId, Guid invoiceId, PostPurchaseDraftToLedgerApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<PurchaseDraftLedgerPostingResult>(
            HttpMethod.Post, $"/api/v1/companies/{companyId:D}/purchase-invoice-drafts/{invoiceId:D}/post-to-ledger", request, ct);

    public Task<PurchaseVatDetailsApiReport> GetPurchaseVatDetailsReportAsync(
        Guid companyId, DateOnly fromDate, DateOnly toDate, int page = 1, int pageSize = 100, CancellationToken ct = default) =>
        GetObjectAsync<PurchaseVatDetailsApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/purchase-vat-details?fromDate={fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&toDate={toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&page={page}&pageSize={pageSize}",
            ct);

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

    public Task<InvoiceAgingApiReport> GetReceivablesAgingAsync(
        Guid companyId, DateOnly asOfDate, int page = 1, int pageSize = 100, CancellationToken ct = default) =>
        GetObjectAsync<InvoiceAgingApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/receivables-aging?asOfDate={asOfDate:yyyy-MM-dd}&page={page}&pageSize={pageSize}", ct);

    public Task<InvoiceAgingApiReport> GetPayablesAgingAsync(
        Guid companyId, DateOnly asOfDate, int page = 1, int pageSize = 100, CancellationToken ct = default) =>
        GetObjectAsync<InvoiceAgingApiReport>(
            $"/api/v1/companies/{companyId:D}/reports/payables-aging?asOfDate={asOfDate:yyyy-MM-dd}&page={page}&pageSize={pageSize}", ct);

    public Task<IReadOnlyList<CustomerReceiptApiDto>> GetCustomerReceiptsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<CustomerReceiptApiDto>($"/api/v1/companies/{companyId:D}/customer-receipts", ct);

    public Task<IReadOnlyList<OutstandingInvoiceApiDto>> GetOutstandingSalesInvoicesAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<OutstandingInvoiceApiDto>($"/api/v1/companies/{companyId:D}/customer-receipts/outstanding-invoices", ct);

    public Task<IReadOnlyList<OutstandingInvoiceApiDto>> GetOutstandingPurchaseInvoicesAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<OutstandingInvoiceApiDto>($"/api/v1/companies/{companyId:D}/supplier-payments/outstanding-invoices", ct);

    public Task<CustomerReceiptApiDto> CreateCustomerReceiptAsync(Guid companyId, CreateCustomerReceiptApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<CustomerReceiptApiDto>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/customer-receipts", request, ct);

    public Task<SettlementPostingApiResult> PostCustomerReceiptAsync(Guid companyId, Guid receiptId, PostCustomerReceiptApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SettlementPostingApiResult>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/customer-receipts/{receiptId:D}/post-to-ledger", request, ct);

    public Task<IReadOnlyList<SupplierPaymentApiDto>> GetSupplierPaymentsAsync(Guid companyId, CancellationToken ct = default) =>
        GetListAsync<SupplierPaymentApiDto>($"/api/v1/companies/{companyId:D}/supplier-payments", ct);

    public Task<SupplierPaymentApiDto> CreateSupplierPaymentAsync(Guid companyId, CreateSupplierPaymentApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SupplierPaymentApiDto>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/supplier-payments", request, ct);

    public Task<SettlementPostingApiResult> PostSupplierPaymentAsync(Guid companyId, Guid paymentId, PostSupplierPaymentApiRequest request, CancellationToken ct = default) =>
        SendJsonAsync<SettlementPostingApiResult>(HttpMethod.Post, $"/api/v1/companies/{companyId:D}/supplier-payments/{paymentId:D}/post-to-ledger", request, ct);

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

    private async Task<T> GetObjectAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
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
            throw new ErpApiException(response.StatusCode, "استجابة التقرير لا تطابق صيغة البيانات المتوقعة.");
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
public sealed record ApiAccount(Guid Id, string Code, string NameAr, string NameEn, string AccountType, string NormalBalance, Guid? ParentAccountId, bool IsActive, bool AllowPosting)
{
    public string DisplayName => $"{Code} — {NameAr} ({NameEn})";
    public string DisplayLabel => DisplayName;
}
public sealed record ApiFiscalPeriod(Guid Id, Guid FiscalYearId, int PeriodNumber, DateOnly StartDate, DateOnly EndDate, string Status)
{
    public string DisplayName => $"الفترة {PeriodNumber}: {StartDate:yyyy-MM-dd} — {EndDate:yyyy-MM-dd} ({Status})";
    public string DisplayLabel => DisplayName;
}
public sealed record PostSalesDraftToLedgerApiRequest(
    Guid FiscalPeriodId, string EntryNumber, Guid ReceivableAccountId, Guid RevenueAccountId, Guid? OutputVatAccountId = null,
    Guid? InventoryAssetAccountId = null, Guid? CostOfGoodsSoldAccountId = null);
public sealed record SalesDraftLedgerPostingApiResult(
    Guid SalesInvoiceDraftId, Guid JournalEntryId, string EntryNumber, string JournalEntryStatus,
    DateOnly EntryDate, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount,
    decimal VatAmount, decimal TotalAmount, bool AlreadyPosted, string Message);
public sealed record ApiCustomer(Guid Id, string Code, string Name, string? Email, string? Phone, string? TaxNumber, bool IsActive, DateTime CreatedAt);
public sealed record ApiSupplier(Guid Id, string Code, string Name, string? Email, string? Phone, string? TaxNumber, bool IsActive, DateTime CreatedAt);
public sealed record CreateInventoryItemApiRequest(string ItemCode, string? Barcode, string NameAr, string NameEn, string? UnitCode = "EA", bool TracksSerials = false);
public sealed record InventoryItemApiDto(Guid Id, string ItemCode, string? Barcode, string NameAr, string NameEn, string UnitCode, bool TracksSerials, bool IsActive, DateTime CreatedAt)
{
    public string DisplayLabel => $"{ItemCode} · {NameAr}";
}
public sealed record CreateInventoryWarehouseApiRequest(string Code, string Name);
public sealed record InventoryWarehouseApiDto(Guid Id, string Code, string Name, bool IsActive, DateTime CreatedAt)
{
    public string DisplayLabel => $"{Code} · {Name}";
}
public sealed record InventoryMovementApiRequest(Guid ItemId, Guid WarehouseId, string MovementNumber, DateOnly MovementDate,
    decimal Quantity, decimal? UnitCost = null, string? Notes = null, IReadOnlyList<string>? SerialNumbers = null);
public sealed record InventoryMovementApiResult(Guid Id, string MovementNumber, string MovementType, Guid ItemId,
    Guid WarehouseId, DateOnly MovementDate, decimal QuantityDelta, decimal UnitCost, decimal InventoryValueDelta,
    decimal OnHandQuantity, decimal InventoryValue, decimal AverageUnitCost);
public sealed record StockBalancesApiReport(int Page, int PageSize, long TotalRows, int TotalPages, IReadOnlyList<StockBalanceApiRow> Rows);
public sealed record StockBalanceApiRow(Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, string ItemNameEn,
    string UnitCode, Guid WarehouseId, string WarehouseCode, string WarehouseName, decimal OnHandQuantity,
    decimal InventoryValue, decimal AverageUnitCost);
public sealed record InventoryValuationApiReport(DateOnly AsOfDate, int Page, int PageSize, long TotalRows, int TotalPages,
    decimal TotalInventoryValue, IReadOnlyList<InventoryValuationApiRow> Rows);
public sealed record InventoryValuationApiRow(Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, string ItemNameEn,
    string UnitCode, Guid WarehouseId, string WarehouseCode, string WarehouseName, decimal OnHandQuantity,
    decimal InventoryValue, decimal AverageUnitCost);
public sealed record InventoryMovementsApiReport(int Page, int PageSize, long TotalRows, int TotalPages,
    IReadOnlyList<InventoryMovementApiRow> Rows);
public sealed record InventoryMovementApiRow(Guid Id, string MovementNumber, string MovementType, DateOnly MovementDate,
    Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, Guid WarehouseId, string WarehouseCode,
    string WarehouseName, decimal QuantityDelta, decimal UnitCost, decimal InventoryValueDelta, string Notes, DateTime CreatedAt,
    string SourceType, Guid? TransferId, bool IsAccountingPosted)
{
    public string DisplayLabel => $"{MovementNumber} · {MovementType} · {MovementDate:yyyy-MM-dd} · {InventoryValueDelta:N2}";
}
public sealed record PostInventoryMovementToLedgerApiRequest(
    Guid FiscalPeriodId, string EntryNumber, Guid InventoryAssetAccountId, Guid CounterpartAccountId);
public sealed record InventoryMovementLedgerPostingApiResult(
    Guid InventoryMovementId, Guid JournalEntryId, string EntryNumber, string JournalEntryStatus,
    DateOnly EntryDate, string MovementType, decimal QuantityDelta, decimal Amount,
    Guid InventoryAssetAccountId, Guid CounterpartAccountId, bool AlreadyPosted, string Message);
public sealed record InventoryTransferApiRequest(Guid ItemId, Guid SourceWarehouseId, Guid DestinationWarehouseId,
    string TransferNumber, DateOnly TransferDate, decimal Quantity, string? Notes = null, IReadOnlyList<string>? SerialNumbers = null);
public sealed record InventoryTransferApiResult(Guid Id, string TransferNumber, DateOnly TransferDate, Guid ItemId,
    Guid SourceWarehouseId, Guid DestinationWarehouseId, decimal Quantity, decimal UnitCost, decimal InventoryValue,
    decimal SourceOnHandQuantity, decimal SourceInventoryValue, decimal DestinationOnHandQuantity,
    decimal DestinationInventoryValue, decimal DestinationAverageUnitCost, DateTime CreatedAt);
public sealed record InventoryTransfersApiReport(int Page, int PageSize, long TotalRows, int TotalPages,
    IReadOnlyList<InventoryTransferApiRow> Rows);
public sealed record InventoryTransferApiRow(Guid Id, string TransferNumber, DateOnly TransferDate,
    Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, string UnitCode,
    Guid SourceWarehouseId, string SourceWarehouseCode, string SourceWarehouseName,
    Guid DestinationWarehouseId, string DestinationWarehouseCode, string DestinationWarehouseName,
    decimal Quantity, decimal UnitCost, decimal InventoryValue, string Notes, DateTime CreatedAt);
public sealed record InventorySerialEventsApiReport(int Page, int PageSize, long TotalRows, int TotalPages,
    IReadOnlyList<InventorySerialEventApiRow> Rows);
public sealed record InventorySerialEventApiRow(Guid Id, string SerialNumber, string ItemCode, string? Barcode,
    string ItemNameAr, string EventType, string MovementNumber, string MovementType, DateOnly MovementDate,
    string WarehouseCode, string WarehouseName, string CurrentStatus, string CurrentWarehouseName, string? TransferNumber);
public sealed record InventorySerialSummaryApiReport(int Page, int PageSize, long TotalRows, int TotalPages,
    IReadOnlyList<InventorySerialSummaryApiRow> Rows);
public sealed record InventorySerialSummaryApiRow(Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr,
    string ItemNameEn, string CurrentStatus, Guid? WarehouseId, string WarehouseCode, string WarehouseName, long SerialCount);

public sealed record CreateSupplierApiRequest(string Code, string Name, string? Email = null, string? Phone = null, string? TaxNumber = null);
public sealed record PurchaseInvoiceDraftListItem(
    Guid Id, string PurchaseNumber, string SupplierInvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
    string CurrencyCode, Guid SupplierId, string SupplierName, string? SupplierTaxNumber,
    decimal SubtotalAmount, string TaxCalculationStatus, decimal? GrossAmount, decimal? DiscountAmount,
    decimal? NetAmount, decimal? VatAmount, decimal? TotalAmount, string Status, DateTime CreatedAt, int LineCount,
    Guid? AccountingPostedJournalEntryId, DateTime? AccountingPostedAt, Guid? AccountingInputVatAccountId);
public sealed record CreatePurchaseInvoiceDraftApiRequest(
    Guid SupplierId, string PurchaseNumber, string SupplierInvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
    string? Notes, IReadOnlyList<CreatePurchaseInvoiceDraftLineApiRequest> Lines);
public sealed record CreatePurchaseInvoiceDraftLineApiRequest(string Description, decimal Quantity, decimal UnitPrice,
    Guid? InventoryItemId = null, Guid? InventoryWarehouseId = null, IReadOnlyList<string>? SerialNumbers = null);
public sealed record PurchaseInvoiceDraftDetails(
    Guid Id, Guid CompanyId, Guid SupplierId, string PurchaseNumber, string SupplierInvoiceNumber,
    DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode, string SupplierName, string? SupplierTaxNumber,
    decimal SubtotalAmount, string Notes, string Status, string TaxCalculationStatus,
    decimal? GrossAmount, decimal? DiscountAmount, decimal? NetAmount, decimal? VatAmount, decimal? TotalAmount,
    DateTime CreatedAt, IReadOnlyList<PurchaseInvoiceDraftLine> Lines,
    Guid? AccountingPostedJournalEntryId, DateTime? AccountingPostedAt, Guid? AccountingInputVatAccountId);
public sealed record PurchaseInvoiceDraftLine(
    Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount,
    decimal DiscountAmount, string VatTreatment, string? VatTreatmentReason, decimal? GrossAmount,
    decimal? NetAmount, decimal? VatRatePercent, decimal? VatAmount, decimal? TotalAmount,
    Guid? InventoryItemId, Guid? InventoryWarehouseId, IReadOnlyList<string> SerialNumbers, decimal? InventoryCostAmount);
public sealed record ApplyPurchaseDraftVatApiRequest(IReadOnlyList<ApplyPurchaseDraftVatLineApiRequest> Lines);
public sealed record ApplyPurchaseDraftVatLineApiRequest(int LineNumber, decimal DiscountAmount, string Treatment, string? TreatmentReason = null);
public sealed record PurchaseDraftVatResult(
    Guid InvoiceId, Guid CompanyId, string TaxCalculationStatus, string Message,
    decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
    IReadOnlyList<PurchaseDraftVatLineResult> Lines);
public sealed record PurchaseDraftVatLineResult(
    int LineNumber, string Description, string Treatment, decimal GrossAmount, decimal DiscountAmount,
    decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);
public sealed record PostPurchaseDraftToLedgerApiRequest(
    Guid FiscalPeriodId, string EntryNumber, Guid PurchaseDebitAccountId, Guid PayableAccountId, Guid? InputVatAccountId = null);
public sealed record PurchaseDraftLedgerPostingResult(
    Guid PurchaseInvoiceDraftId, Guid JournalEntryId, string EntryNumber, string JournalEntryStatus, DateOnly EntryDate,
    decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
    bool AlreadyPosted, Guid? InputVatAccountId, string InputVatPostingMode, string Message);
public sealed record PurchaseVatDetailsApiReport(
    Guid CompanyId, DateOnly FromDate, DateOnly ToDate, int Page, int PageSize, long TotalLines, int TotalPages,
    long InvoiceCount, decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount,
    decimal TotalAmount, string ReportKind, string Message, IReadOnlyList<PurchaseVatDetailsApiLine> Rows);
public sealed record PurchaseVatDetailsApiLine(
    Guid InvoiceId, string PurchaseNumber, string SupplierInvoiceNumber, DateOnly IssueDate,
    string SupplierName, string? SupplierTaxNumber, string CurrencyCode, string DocumentStatus,
    int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal GrossAmount,
    decimal DiscountAmount, string VatTreatment, string? VatTreatmentReason, decimal NetAmount,
    decimal VatRatePercent, decimal VatAmount, decimal TotalAmount);

public sealed record CreateCustomerApiRequest(string Code, string Name, string? Email = null, string? Phone = null, string? TaxNumber = null);
public sealed record SalesInvoiceDraftListItem(
    Guid Id, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode,
    Guid CustomerId, string CustomerName, decimal SubtotalAmount, string TaxCalculationStatus,
    decimal? VatAmount, decimal? TotalAmount, string Status, DateTime CreatedAt, int LineCount);
public sealed record CreateSalesInvoiceDraftApiRequest(
    Guid CustomerId, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
    string? Notes, IReadOnlyList<CreateSalesInvoiceDraftLineApiRequest> Lines);
public sealed record CreateSalesInvoiceDraftLineApiRequest(string Description, decimal Quantity, decimal UnitPrice,
    Guid? InventoryItemId = null, Guid? InventoryWarehouseId = null, IReadOnlyList<string>? SerialNumbers = null);
public sealed record SalesInvoiceDraftDetails(
    Guid Id, Guid CompanyId, Guid CustomerId, string InvoiceNumber, DateOnly IssueDate,
    DateOnly? DueDate, string CurrencyCode, string CustomerName, string? CustomerTaxNumber,
    decimal SubtotalAmount, string Notes, string Status, DateTime CreatedAt, IReadOnlyList<SalesInvoiceDraftLine> Lines);
public sealed record SalesInvoiceDraftLine(Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount,
    Guid? InventoryItemId, Guid? InventoryWarehouseId, IReadOnlyList<string> SerialNumbers, decimal? InventoryCostAmount);

public sealed record TrialBalanceApiReport(
    Guid CompanyId, DateOnly FromDate, DateOnly ToDate, IReadOnlyList<TrialBalanceApiRow> Rows,
    decimal TotalDebitMovement, decimal TotalCreditMovement, decimal TotalDebitBalance, decimal TotalCreditBalance);
public sealed record TrialBalanceApiRow(
    Guid AccountId, string AccountCode, string AccountNameAr, string AccountNameEn, string AccountType,
    decimal DebitMovement, decimal CreditMovement, decimal DebitBalance, decimal CreditBalance);
public sealed record FinancialStatementApiRow(Guid? AccountId, string AccountCode, string AccountNameAr, string AccountNameEn, decimal Amount);
public sealed record IncomeStatementApiReport(
    Guid CompanyId, DateOnly FromDate, DateOnly ToDate,
    IReadOnlyList<FinancialStatementApiRow> Revenue, IReadOnlyList<FinancialStatementApiRow> Expenses,
    decimal TotalRevenue, decimal TotalExpenses, decimal NetIncome);
public sealed record SalesVatDetailsApiReport(
    Guid CompanyId, DateOnly FromDate, DateOnly ToDate, int Page, int PageSize,
    long TotalLines, int TotalPages, long InvoiceCount,
    decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
    string ReportKind, string Message, IReadOnlyList<SalesVatDetailsApiLine> Rows);
public sealed record SalesVatDetailsApiLine(
    Guid InvoiceId, string InvoiceNumber, DateOnly IssueDate, string CustomerName, string? CustomerTaxNumber,
    string CurrencyCode, string DocumentStatus, int LineNumber, string Description, decimal Quantity,
    decimal UnitPrice, decimal GrossAmount, decimal DiscountAmount, string VatTreatment,
    string? VatTreatmentReason, decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount);

public sealed record BalanceSheetApiReport(
    Guid CompanyId, DateOnly AsOfDate,
    IReadOnlyList<FinancialStatementApiRow> Assets, IReadOnlyList<FinancialStatementApiRow> Liabilities,
    IReadOnlyList<FinancialStatementApiRow> Equity, decimal UnclosedEarnings,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal Difference);



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

public sealed record SettlementAllocationApiRequest(Guid InvoiceId, decimal Amount);
public sealed record CreateCustomerReceiptApiRequest(Guid PartyId, string? Number, DateOnly Date, decimal Amount,
    string? CurrencyCode, string? Notes, IReadOnlyList<SettlementAllocationApiRequest> Allocations);
public sealed record PostCustomerReceiptApiRequest(Guid FiscalPeriodId, string EntryNumber, Guid CashAccountId, Guid ReceivableAccountId);
public sealed record CreateSupplierPaymentApiRequest(Guid PartyId, string? Number, DateOnly Date, decimal Amount,
    string? CurrencyCode, string? Notes, IReadOnlyList<SettlementAllocationApiRequest> Allocations);
public sealed record PostSupplierPaymentApiRequest(Guid FiscalPeriodId, string EntryNumber, Guid CashAccountId, Guid PayableAccountId);
public sealed record CustomerReceiptApiDto(Guid Id, string Number, DateOnly Date, Guid PartyId, string PartyName,
    string CurrencyCode, decimal Amount, string Status, Guid? JournalEntryId);
public sealed record SupplierPaymentApiDto(Guid Id, string Number, DateOnly Date, Guid PartyId, string PartyName,
    string CurrencyCode, decimal Amount, string Status, Guid? JournalEntryId);
public sealed record SettlementPostingApiResult(Guid SettlementId, Guid JournalEntryId, string EntryNumber,
    string JournalEntryStatus, DateOnly EntryDate, decimal Amount, bool AlreadyPosted);

public sealed record OutstandingInvoiceApiDto(Guid InvoiceId, string Number, DateOnly Date, Guid PartyId,
    string PartyName, string CurrencyCode, decimal InvoiceAmount, decimal OutstandingAmount)
{
    public string DisplayLabel => $"{Number} · متبقي {OutstandingAmount:N2} {CurrencyCode}";
}

public sealed record InvoiceAgingApiReport(
    DateOnly AsOfDate,
    int Page,
    int PageSize,
    long TotalRows,
    int TotalPages,
    decimal TotalOutstandingAmount,
    decimal CurrentAmount,
    decimal Days1To30Amount,
    decimal Days31To60Amount,
    decimal Days61To90Amount,
    decimal Over90Amount,
    IReadOnlyList<InvoiceAgingApiRow> Rows)
{
    public string SummaryLabel =>
        $"الرصيد المفتوح: {TotalOutstandingAmount:N2} · حالي: {CurrentAmount:N2} · 1–30 يومًا: {Days1To30Amount:N2} · 31–60: {Days31To60Amount:N2} · 61–90: {Days61To90Amount:N2} · أكثر من 90: {Over90Amount:N2} · عدد الفواتير: {TotalRows:N0}";

    public string PageLabel => TotalPages == 0 ? "لا توجد فواتير مفتوحة" : $"صفحة {Page} من {TotalPages}";
}

public sealed record InvoiceAgingApiRow(
    Guid InvoiceId,
    string DocumentNumber,
    DateOnly IssueDate,
    DateOnly? DueDate,
    Guid PartyId,
    string PartyName,
    string CurrencyCode,
    decimal InvoiceAmount,
    decimal SettledAmount,
    decimal OutstandingAmount,
    int DaysPastDue,
    string AgingBucket)
{
    public string AgingBucketLabel => AgingBucket switch
    {
        "CURRENT" => "حالي / غير مستحق",
        "DAYS_1_30" => "متأخر 1–30 يومًا",
        "DAYS_31_60" => "متأخر 31–60 يومًا",
        "DAYS_61_90" => "متأخر 61–90 يومًا",
        "OVER_90" => "متأخر أكثر من 90 يومًا",
        _ => "غير محدد"
    };
}
