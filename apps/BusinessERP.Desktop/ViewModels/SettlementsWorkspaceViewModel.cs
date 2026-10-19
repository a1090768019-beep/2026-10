using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

public sealed class SettlementsWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private ApiCustomer? _selectedCustomer;
    private ApiSupplier? _selectedSupplier;
    private OutstandingInvoiceApiDto? _selectedSalesInvoice;
    private OutstandingInvoiceApiDto? _selectedPurchaseInvoice;
    private CustomerReceiptApiDto? _selectedReceipt;
    private SupplierPaymentApiDto? _selectedPayment;
    private ApiFiscalPeriod? _selectedPeriod;
    private ApiAccount? _selectedCashAccount;
    private ApiAccount? _selectedReceivableAccount;
    private ApiAccount? _selectedPayableAccount;
    private string _receiptNumber = "";
    private string _paymentNumber = "";
    private string _receiptAmountText = "";
    private string _paymentAmountText = "";
    private string _receiptEntryNumber = "";
    private string _paymentEntryNumber = "";
    private string _receiptNotes = "";
    private string _paymentNotes = "";
    private DateTime _receiptDate = DateTime.Today;
    private DateTime _paymentDate = DateTime.Today;
    private DateTime? _agingAsOfDate = DateTime.Today;
    private InvoiceAgingApiReport? _receivablesAgingReport;
    private InvoiceAgingApiReport? _payablesAgingReport;
    private bool _isBusy;
    private bool _statusIsError;
    private string _statusMessage = "سجّل الدخول واختر الشركة لإدارة سندات القبض والصرف.";

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<ApiCustomer> Customers { get; } = [];
    public ObservableCollection<ApiSupplier> Suppliers { get; } = [];
    public ObservableCollection<OutstandingInvoiceApiDto> SalesInvoices { get; } = [];
    public ObservableCollection<OutstandingInvoiceApiDto> PurchaseInvoices { get; } = [];
    public ObservableCollection<CustomerReceiptApiDto> Receipts { get; } = [];
    public ObservableCollection<SupplierPaymentApiDto> Payments { get; } = [];
    public ObservableCollection<ApiFiscalPeriod> FiscalPeriods { get; } = [];
    public ObservableCollection<ApiAccount> CashAccounts { get; } = [];
    public ObservableCollection<ApiAccount> ReceivableAccounts { get; } = [];
    public ObservableCollection<ApiAccount> PayableAccounts { get; } = [];
    public ObservableCollection<InvoiceAgingApiRow> ReceivablesAgingRows { get; } = [];
    public ObservableCollection<InvoiceAgingApiRow> PayablesAgingRows { get; } = [];

    public ApiCompany? SelectedCompany { get => _selectedCompany; set { if (Set(ref _selectedCompany, value)) RaiseCommands(); } }
    public ApiCustomer? SelectedCustomer { get => _selectedCustomer; set { if (Set(ref _selectedCustomer, value)) RaiseCommands(); } }
    public ApiSupplier? SelectedSupplier { get => _selectedSupplier; set { if (Set(ref _selectedSupplier, value)) RaiseCommands(); } }
    public OutstandingInvoiceApiDto? SelectedSalesInvoice { get => _selectedSalesInvoice; set { if (Set(ref _selectedSalesInvoice, value)) RaiseCommands(); } }
    public OutstandingInvoiceApiDto? SelectedPurchaseInvoice { get => _selectedPurchaseInvoice; set { if (Set(ref _selectedPurchaseInvoice, value)) RaiseCommands(); } }
    public CustomerReceiptApiDto? SelectedReceipt { get => _selectedReceipt; set { if (Set(ref _selectedReceipt, value)) RaiseCommands(); } }
    public SupplierPaymentApiDto? SelectedPayment { get => _selectedPayment; set { if (Set(ref _selectedPayment, value)) RaiseCommands(); } }
    public ApiFiscalPeriod? SelectedPeriod { get => _selectedPeriod; set { if (Set(ref _selectedPeriod, value)) RaiseCommands(); } }
    public ApiAccount? SelectedCashAccount { get => _selectedCashAccount; set { if (Set(ref _selectedCashAccount, value)) RaiseCommands(); } }
    public ApiAccount? SelectedReceivableAccount { get => _selectedReceivableAccount; set { if (Set(ref _selectedReceivableAccount, value)) RaiseCommands(); } }
    public ApiAccount? SelectedPayableAccount { get => _selectedPayableAccount; set { if (Set(ref _selectedPayableAccount, value)) RaiseCommands(); } }
    public string ReceiptNumber { get => _receiptNumber; set { if (Set(ref _receiptNumber, value)) RaiseCommands(); } }
    public string PaymentNumber { get => _paymentNumber; set { if (Set(ref _paymentNumber, value)) RaiseCommands(); } }
    public string ReceiptAmountText { get => _receiptAmountText; set { if (Set(ref _receiptAmountText, value)) RaiseCommands(); } }
    public string PaymentAmountText { get => _paymentAmountText; set { if (Set(ref _paymentAmountText, value)) RaiseCommands(); } }
    public string ReceiptEntryNumber { get => _receiptEntryNumber; set { if (Set(ref _receiptEntryNumber, value)) RaiseCommands(); } }
    public string PaymentEntryNumber { get => _paymentEntryNumber; set { if (Set(ref _paymentEntryNumber, value)) RaiseCommands(); } }
    public string ReceiptNotes { get => _receiptNotes; set => Set(ref _receiptNotes, value); }
    public string PaymentNotes { get => _paymentNotes; set => Set(ref _paymentNotes, value); }
    public DateTime ReceiptDate { get => _receiptDate; set => Set(ref _receiptDate, value); }
    public DateTime PaymentDate { get => _paymentDate; set => Set(ref _paymentDate, value); }
    public DateTime? AgingAsOfDate
    {
        get => _agingAsOfDate;
        set
        {
            if (!Set(ref _agingAsOfDate, value)) return;
            ClearAgingReports();
            RaiseCommands();
        }
    }
    public InvoiceAgingApiReport? ReceivablesAgingReport
    {
        get => _receivablesAgingReport;
        private set
        {
            if (!Set(ref _receivablesAgingReport, value)) return;
            OnPropertyChanged(nameof(ReceivablesAgingSummary));
            OnPropertyChanged(nameof(ReceivablesAgingPageLabel));
            RaiseCommands();
        }
    }
    public InvoiceAgingApiReport? PayablesAgingReport
    {
        get => _payablesAgingReport;
        private set
        {
            if (!Set(ref _payablesAgingReport, value)) return;
            OnPropertyChanged(nameof(PayablesAgingSummary));
            OnPropertyChanged(nameof(PayablesAgingPageLabel));
            RaiseCommands();
        }
    }
    public string ReceivablesAgingSummary => ReceivablesAgingReport?.SummaryLabel ?? "حدد تاريخ التقرير ثم اضغط تحديث.";
    public string PayablesAgingSummary => PayablesAgingReport?.SummaryLabel ?? "حدد تاريخ التقرير ثم اضغط تحديث.";
    public string ReceivablesAgingPageLabel => ReceivablesAgingReport?.PageLabel ?? "لا يوجد تقرير محمّل";
    public string PayablesAgingPageLabel => PayablesAgingReport?.PageLabel ?? "لا يوجد تقرير محمّل";
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    public string StatusMessage { get => _statusMessage; private set { if (Set(ref _statusMessage, value)) OnPropertyChanged(nameof(WorkspaceContextSummary)); } }
    public string WorkspaceContextSummary => SelectedCompany is null
        ? "سندات القبض والصرف · لم يتم اختيار شركة"
        : $"الشركة: {SelectedCompany.Name} · العملة: {SelectedCompany.BaseCurrency} · سندات قبض وصرف مرتبطة بالأستاذ العام";
    public bool CanCreateReceipt => !IsBusy && SelectedCompany is not null && SelectedCustomer is not null &&
        SelectedSalesInvoice is not null && SelectedSalesInvoice.PartyId == SelectedCustomer.Id && SelectedSalesInvoice.OutstandingAmount > 0 &&
        !string.IsNullOrWhiteSpace(ReceiptNumber) && TryAmount(ReceiptAmountText, out var receiptAmount) && receiptAmount > 0;
    public bool CanCreatePayment => !IsBusy && SelectedCompany is not null && SelectedSupplier is not null &&
        SelectedPurchaseInvoice is not null && SelectedPurchaseInvoice.PartyId == SelectedSupplier.Id && SelectedPurchaseInvoice.OutstandingAmount > 0 &&
        !string.IsNullOrWhiteSpace(PaymentNumber) && TryAmount(PaymentAmountText, out var paymentAmount) && paymentAmount > 0;
    public bool CanPostReceipt => !IsBusy && SelectedCompany is not null && SelectedReceipt?.Status == "DRAFT" &&
        SelectedPeriod?.Status == "OPEN" && SelectedCashAccount is not null && SelectedReceivableAccount is not null &&
        !string.IsNullOrWhiteSpace(ReceiptEntryNumber);
    public bool CanPostPayment => !IsBusy && SelectedCompany is not null && SelectedPayment?.Status == "DRAFT" &&
        SelectedPeriod?.Status == "OPEN" && SelectedCashAccount is not null && SelectedPayableAccount is not null &&
        !string.IsNullOrWhiteSpace(PaymentEntryNumber);

    public ICommand RefreshCommand { get; }
    public ICommand CreateReceiptCommand { get; }
    public ICommand PostReceiptCommand { get; }
    public ICommand CreatePaymentCommand { get; }
    public ICommand PostPaymentCommand { get; }
    public ICommand RefreshAgingReportsCommand { get; }
    public ICommand PreviousReceivablesAgingPageCommand { get; }
    public ICommand NextReceivablesAgingPageCommand { get; }
    public ICommand PreviousPayablesAgingPageCommand { get; }
    public ICommand NextPayablesAgingPageCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? WorkspaceContextChanged;

    public SettlementsWorkspaceViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _apiClient is not null && !IsBusy);
        CreateReceiptCommand = new AsyncRelayCommand(CreateReceiptAsync, () => CanCreateReceipt);
        PostReceiptCommand = new AsyncRelayCommand(PostReceiptAsync, () => CanPostReceipt);
        CreatePaymentCommand = new AsyncRelayCommand(CreatePaymentAsync, () => CanCreatePayment);
        PostPaymentCommand = new AsyncRelayCommand(PostPaymentAsync, () => CanPostPayment);
        RefreshAgingReportsCommand = new AsyncRelayCommand(RefreshAgingReportsAsync,
            () => _apiClient is not null && SelectedCompany is not null && AgingAsOfDate.HasValue && !IsBusy);
        PreviousReceivablesAgingPageCommand = new AsyncRelayCommand(
            PreviousReceivablesAgingPageAsync, () => !IsBusy && ReceivablesAgingReport is { Page: > 1 });
        NextReceivablesAgingPageCommand = new AsyncRelayCommand(
            NextReceivablesAgingPageAsync, () => !IsBusy && ReceivablesAgingReport is { } ar && ar.Page < ar.TotalPages);
        PreviousPayablesAgingPageCommand = new AsyncRelayCommand(
            PreviousPayablesAgingPageAsync, () => !IsBusy && PayablesAgingReport is { Page: > 1 });
        NextPayablesAgingPageCommand = new AsyncRelayCommand(
            NextPayablesAgingPageAsync, () => !IsBusy && PayablesAgingReport is { } ap && ap.Page < ap.TotalPages);
    }

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            ClearData();
            StatusMessage = "سجّل الدخول ثم فعّل المستأجر للوصول إلى سندات القبض والصرف.";
            StatusIsError = false;
            return;
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_apiClient is null) return;
        await RunBusyAsync(async () =>
        {
            var priorCompanyId = SelectedCompany?.Id;
            var companies = await _apiClient.GetCompaniesAsync();
            Replace(Companies, companies);
            SelectedCompany = Companies.FirstOrDefault(x => x.Id == priorCompanyId) ?? Companies.FirstOrDefault();
            if (SelectedCompany is null)
            {
                ClearCompanyData();
                StatusMessage = "لا توجد شركة متاحة لهذا المستأجر.";
                StatusIsError = false;
                return;
            }
            await LoadCompanyDataCoreAsync();
            StatusMessage = $"تم تحديث بيانات التسويات للشركة {SelectedCompany.Name}.";
            StatusIsError = false;
        });
    }

    public async Task LoadSelectedCompanyAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        await RunBusyAsync(LoadCompanyDataCoreAsync);
    }

    private async Task LoadCompanyDataCoreAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        ClearAgingReports();
        var companyId = SelectedCompany.Id;
        var customersTask = _apiClient.GetCustomersAsync(companyId);
        var suppliersTask = _apiClient.GetSuppliersAsync(companyId);
        var salesTask = _apiClient.GetOutstandingSalesInvoicesAsync(companyId);
        var purchaseTask = _apiClient.GetOutstandingPurchaseInvoicesAsync(companyId);
        var accountsTask = _apiClient.GetAccountsAsync(companyId);
        var periodsTask = _apiClient.GetFiscalPeriodsAsync(companyId);
        var receiptsTask = _apiClient.GetCustomerReceiptsAsync(companyId);
        var paymentsTask = _apiClient.GetSupplierPaymentsAsync(companyId);
        await Task.WhenAll(customersTask, suppliersTask, salesTask, purchaseTask, accountsTask, periodsTask, receiptsTask, paymentsTask);

        Replace(Customers, (await customersTask).Where(x => x.IsActive));
        Replace(Suppliers, (await suppliersTask).Where(x => x.IsActive));
        Replace(SalesInvoices, await salesTask);
        Replace(PurchaseInvoices, await purchaseTask);
        Replace(FiscalPeriods, (await periodsTask).Where(x => x.Status == "OPEN"));
        Replace(Receipts, await receiptsTask);
        Replace(Payments, await paymentsTask);

        var accounts = (await accountsTask).Where(x => x.IsActive && x.AllowPosting).ToArray();
        Replace(CashAccounts, accounts.Where(x => x.AccountType == "ASSET" && x.NormalBalance == "DEBIT"));
        Replace(ReceivableAccounts, accounts.Where(x => x.AccountType == "ASSET" && x.NormalBalance == "DEBIT"));
        Replace(PayableAccounts, accounts.Where(x => x.AccountType == "LIABILITY" && x.NormalBalance == "CREDIT"));

        SelectedCustomer = Customers.FirstOrDefault(x => x.Id == SelectedCustomer?.Id) ?? Customers.FirstOrDefault();
        SelectedSupplier = Suppliers.FirstOrDefault(x => x.Id == SelectedSupplier?.Id) ?? Suppliers.FirstOrDefault();
        SelectedSalesInvoice = SalesInvoices.FirstOrDefault(x => x.InvoiceId == SelectedSalesInvoice?.InvoiceId) ?? SalesInvoices.FirstOrDefault();
        SelectedPurchaseInvoice = PurchaseInvoices.FirstOrDefault(x => x.InvoiceId == SelectedPurchaseInvoice?.InvoiceId) ?? PurchaseInvoices.FirstOrDefault();
        SelectedReceipt = Receipts.FirstOrDefault(x => x.Id == SelectedReceipt?.Id) ?? Receipts.FirstOrDefault();
        SelectedPayment = Payments.FirstOrDefault(x => x.Id == SelectedPayment?.Id) ?? Payments.FirstOrDefault();
        SelectedPeriod = FiscalPeriods.FirstOrDefault(x => x.Id == SelectedPeriod?.Id) ?? FiscalPeriods.FirstOrDefault();
        SelectedCashAccount = CashAccounts.FirstOrDefault(x => x.Id == SelectedCashAccount?.Id) ?? CashAccounts.FirstOrDefault();
        SelectedReceivableAccount = ReceivableAccounts.FirstOrDefault(x => x.Id == SelectedReceivableAccount?.Id) ?? ReceivableAccounts.FirstOrDefault();
        SelectedPayableAccount = PayableAccounts.FirstOrDefault(x => x.Id == SelectedPayableAccount?.Id) ?? PayableAccounts.FirstOrDefault();
        OnPropertyChanged(nameof(WorkspaceContextSummary));
        WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task CreateReceiptAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedCustomer is null || SelectedSalesInvoice is null ||
            !TryAmount(ReceiptAmountText, out var amount) || amount <= 0) return;
        await RunBusyAsync(async () =>
        {
            var request = new CreateCustomerReceiptApiRequest(SelectedCustomer.Id, ReceiptNumber.Trim(),
                DateOnly.FromDateTime(ReceiptDate), amount, SelectedCompany.BaseCurrency, ReceiptNotes.Trim(),
                [new SettlementAllocationApiRequest(SelectedSalesInvoice.InvoiceId, amount)]);
            await _apiClient.CreateCustomerReceiptAsync(SelectedCompany.Id, request);
            ReceiptNumber = "";
            ReceiptAmountText = "";
            ReceiptNotes = "";
            await LoadCompanyDataCoreAsync();
            StatusMessage = "تم إنشاء سند القبض كمسودة. رحّله إلى الأستاذ العام بعد مراجعة الفترة والحسابات.";
            StatusIsError = false;
        });
    }

    private async Task PostReceiptAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedReceipt is null || SelectedPeriod is null ||
            SelectedCashAccount is null || SelectedReceivableAccount is null) return;
        await RunBusyAsync(async () =>
        {
            var result = await _apiClient.PostCustomerReceiptAsync(SelectedCompany.Id, SelectedReceipt.Id,
                new PostCustomerReceiptApiRequest(SelectedPeriod.Id, ReceiptEntryNumber.Trim(),
                    SelectedCashAccount.Id, SelectedReceivableAccount.Id));
            ReceiptEntryNumber = "";
            await LoadCompanyDataCoreAsync();
            StatusMessage = result.AlreadyPosted
                ? $"السند مرحّل بالفعل بالقيد {result.EntryNumber}."
                : $"تم ترحيل سند القبض بالقيد {result.EntryNumber}.";
            StatusIsError = false;
        });
    }

    private async Task CreatePaymentAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedSupplier is null || SelectedPurchaseInvoice is null ||
            !TryAmount(PaymentAmountText, out var amount) || amount <= 0) return;
        await RunBusyAsync(async () =>
        {
            var request = new CreateSupplierPaymentApiRequest(SelectedSupplier.Id, PaymentNumber.Trim(),
                DateOnly.FromDateTime(PaymentDate), amount, SelectedCompany.BaseCurrency, PaymentNotes.Trim(),
                [new SettlementAllocationApiRequest(SelectedPurchaseInvoice.InvoiceId, amount)]);
            await _apiClient.CreateSupplierPaymentAsync(SelectedCompany.Id, request);
            PaymentNumber = "";
            PaymentAmountText = "";
            PaymentNotes = "";
            await LoadCompanyDataCoreAsync();
            StatusMessage = "تم إنشاء سند الصرف كمسودة. رحّله إلى الأستاذ العام بعد مراجعة الفترة والحسابات.";
            StatusIsError = false;
        });
    }

    private async Task PostPaymentAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedPayment is null || SelectedPeriod is null ||
            SelectedCashAccount is null || SelectedPayableAccount is null) return;
        await RunBusyAsync(async () =>
        {
            var result = await _apiClient.PostSupplierPaymentAsync(SelectedCompany.Id, SelectedPayment.Id,
                new PostSupplierPaymentApiRequest(SelectedPeriod.Id, PaymentEntryNumber.Trim(),
                    SelectedCashAccount.Id, SelectedPayableAccount.Id));
            PaymentEntryNumber = "";
            await LoadCompanyDataCoreAsync();
            StatusMessage = result.AlreadyPosted
                ? $"السند مرحّل بالفعل بالقيد {result.EntryNumber}."
                : $"تم ترحيل سند الصرف بالقيد {result.EntryNumber}.";
            StatusIsError = false;
        });
    }

    public async Task RefreshAgingReportsAsync()
    {
        if (_apiClient is null || SelectedCompany is null || !AgingAsOfDate.HasValue) return;
        await RunBusyAsync(async () =>
        {
            var client = _apiClient!;
            var companyId = SelectedCompany!.Id;
            var reportDate = DateOnly.FromDateTime(AgingAsOfDate!.Value);
            var receivablesTask = client.GetReceivablesAgingAsync(companyId, reportDate, page: 1, pageSize: 100);
            var payablesTask = client.GetPayablesAgingAsync(companyId, reportDate, page: 1, pageSize: 100);
            await Task.WhenAll(receivablesTask, payablesTask);

            var receivables = await receivablesTask;
            var payables = await payablesTask;
            ReceivablesAgingReport = receivables;
            PayablesAgingReport = payables;
            Replace(ReceivablesAgingRows, receivables.Rows);
            Replace(PayablesAgingRows, payables.Rows);
            StatusMessage = $"تم تحديث أعمار الذمم المدينة والدائنة حتى {reportDate:yyyy-MM-dd}.";
            StatusIsError = false;
        });
    }

    private Task PreviousReceivablesAgingPageAsync() =>
        LoadReceivablesAgingPageAsync(Math.Max(1, (ReceivablesAgingReport?.Page ?? 1) - 1));

    private Task NextReceivablesAgingPageAsync() =>
        LoadReceivablesAgingPageAsync((ReceivablesAgingReport?.Page ?? 1) + 1);

    private Task PreviousPayablesAgingPageAsync() =>
        LoadPayablesAgingPageAsync(Math.Max(1, (PayablesAgingReport?.Page ?? 1) - 1));

    private Task NextPayablesAgingPageAsync() =>
        LoadPayablesAgingPageAsync((PayablesAgingReport?.Page ?? 1) + 1);

    private async Task LoadReceivablesAgingPageAsync(int page)
    {
        if (_apiClient is null || SelectedCompany is null || !AgingAsOfDate.HasValue || ReceivablesAgingReport is null) return;
        await RunBusyAsync(async () =>
        {
            var report = await _apiClient!.GetReceivablesAgingAsync(
                SelectedCompany!.Id, DateOnly.FromDateTime(AgingAsOfDate!.Value), page, 100);
            ReceivablesAgingReport = report;
            Replace(ReceivablesAgingRows, report.Rows);
            StatusMessage = $"تم تحميل صفحة أعمار الذمم المدينة {report.Page} من {report.TotalPages}.";
            StatusIsError = false;
        });
    }

    private async Task LoadPayablesAgingPageAsync(int page)
    {
        if (_apiClient is null || SelectedCompany is null || !AgingAsOfDate.HasValue || PayablesAgingReport is null) return;
        await RunBusyAsync(async () =>
        {
            var report = await _apiClient!.GetPayablesAgingAsync(
                SelectedCompany!.Id, DateOnly.FromDateTime(AgingAsOfDate!.Value), page, 100);
            PayablesAgingReport = report;
            Replace(PayablesAgingRows, report.Rows);
            StatusMessage = $"تم تحميل صفحة أعمار الذمم الدائنة {report.Page} من {report.TotalPages}.";
            StatusIsError = false;
        });
    }

    private void ClearAgingReports()
    {
        ReceivablesAgingReport = null;
        PayablesAgingReport = null;
        ReceivablesAgingRows.Clear();
        PayablesAgingRows.Clear();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusIsError = false;
        try { await action(); }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم. تحقق من الاتصال ثم أعد المحاولة."; StatusIsError = true; }
        catch (TaskCanceledException) { StatusMessage = "انتهت مهلة الطلب. تحقق من حالة السند قبل إعادة المحاولة."; StatusIsError = true; }
        catch (Exception) { StatusMessage = "تعذر إكمال العملية. راجع البيانات وصلاحيات الشركة ثم أعد المحاولة."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private void ClearData()
    {
        Companies.Clear(); SelectedCompany = null; ClearCompanyData();
    }

    private void ClearCompanyData()
    {
        Customers.Clear(); Suppliers.Clear(); SalesInvoices.Clear(); PurchaseInvoices.Clear(); Receipts.Clear(); Payments.Clear();
        FiscalPeriods.Clear(); CashAccounts.Clear(); ReceivableAccounts.Clear(); PayableAccounts.Clear();
        ClearAgingReports();
        SelectedCustomer = null; SelectedSupplier = null; SelectedSalesInvoice = null; SelectedPurchaseInvoice = null;
        SelectedReceipt = null; SelectedPayment = null; SelectedPeriod = null; SelectedCashAccount = null;
        SelectedReceivableAccount = null; SelectedPayableAccount = null;
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private static bool TryAmount(string? value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) ||
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(WorkspaceContextSummary));
        OnPropertyChanged(nameof(CanCreateReceipt)); OnPropertyChanged(nameof(CanCreatePayment));
        OnPropertyChanged(nameof(CanPostReceipt)); OnPropertyChanged(nameof(CanPostPayment));
        foreach (var command in new[] { RefreshCommand, CreateReceiptCommand, PostReceiptCommand, CreatePaymentCommand, PostPaymentCommand,
                     RefreshAgingReportsCommand, PreviousReceivablesAgingPageCommand, NextReceivablesAgingPageCommand,
                     PreviousPayablesAgingPageCommand, NextPayablesAgingPageCommand })
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
