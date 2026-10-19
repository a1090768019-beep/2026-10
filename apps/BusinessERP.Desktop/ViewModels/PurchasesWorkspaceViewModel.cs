using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

/// <summary>
/// WPF purchasing workspace backed by the authenticated tenant-scoped API.
/// Purchase documents remain drafts; this UI does not issue legal tax invoices or post GL entries.
/// </summary>
public sealed class PurchasesWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private ApiSupplier? _selectedSupplier;
    private PurchaseInvoiceDraftListItem? _selectedPurchaseDraft;
    private PurchaseInvoiceDraftDetails? _selectedDraftDetails;
    private PurchaseVatDetailsApiReport? _currentVatReport;
    private bool _suppressCompanyReload;
    private bool _isBusy;
    private bool _statusIsError;
    private string _statusMessage = "سجّل الدخول وفعّل المستأجر لإدارة الموردين ومسودات المشتريات.";
    private string _newSupplierCode = "";
    private string _newSupplierName = "";
    private string _newSupplierEmail = "";
    private string _newSupplierPhone = "";
    private string _newSupplierTaxNumber = "";
    private string _purchaseNumber = $"PUR-{DateTime.Now:yyyyMMdd-HHmmss}";
    private string _supplierInvoiceNumber = "";
    private string _purchaseNotes = "";
    private DateTime? _issueDate = DateTime.Today;
    private DateTime? _dueDate = DateTime.Today.AddDays(30);
    private DateTime? _reportFromDate = new DateTime(DateTime.Today.Year, 1, 1);
    private DateTime? _reportToDate = DateTime.Today;
    private PurchaseLineEditor? _selectedNewPurchaseLine;
    private int _reportPage = 1;
    private const int ReportPageSize = 100;

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<ApiSupplier> Suppliers { get; } = [];
    public ObservableCollection<PurchaseInvoiceDraftListItem> PurchaseDrafts { get; } = [];
    public ObservableCollection<PurchaseLineEditor> NewPurchaseLines { get; } = [new PurchaseLineEditor()];
    public ObservableCollection<PurchaseLineEditor> SelectedDraftLines { get; } = [];
    public ObservableCollection<PurchaseVatDetailsApiLine> PurchaseVatRows { get; } = [];
    public IReadOnlyList<string> VatTreatmentOptions { get; } = ["StandardRated", "ZeroRated", "Exempt", "OutsideScope"];

    public ICommand RefreshCommand { get; }
    public ICommand CreateSupplierCommand { get; }
    public ICommand AddPurchaseLineCommand { get; }
    public ICommand RemovePurchaseLineCommand { get; }
    public ICommand CreatePurchaseDraftCommand { get; }
    public ICommand CalculateSelectedDraftVatCommand { get; }
    public ICommand LoadVatReportCommand { get; }
    public ICommand PreviousVatPageCommand { get; }
    public ICommand NextVatPageCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? WorkspaceContextChanged;

    public PurchasesWorkspaceViewModel()
    {
        NewPurchaseLines.CollectionChanged += (_, _) =>
        {
            foreach (var line in NewPurchaseLines)
                line.PropertyChanged -= NewPurchaseLine_PropertyChanged;
            foreach (var line in NewPurchaseLines)
                line.PropertyChanged += NewPurchaseLine_PropertyChanged;
            NotifyCommands();
        };
        SelectedDraftLines.CollectionChanged += (_, _) =>
        {
            foreach (var line in SelectedDraftLines)
                line.PropertyChanged -= SelectedDraftLine_PropertyChanged;
            foreach (var line in SelectedDraftLines)
                line.PropertyChanged += SelectedDraftLine_PropertyChanged;
            NotifyCommands();
        };
        foreach (var line in NewPurchaseLines)
            line.PropertyChanged += NewPurchaseLine_PropertyChanged;

        RefreshCommand = new AsyncCommand(RefreshAsync, () => _apiClient is not null && !IsBusy, HandleUnexpectedCommandFailure);
        CreateSupplierCommand = new AsyncCommand(CreateSupplierAsync, CanCreateSupplier, HandleUnexpectedCommandFailure);
        AddPurchaseLineCommand = new RelayCommand(AddPurchaseLine, () => !IsBusy);
        RemovePurchaseLineCommand = new RelayCommand(RemoveSelectedPurchaseLine, () => !IsBusy && SelectedNewPurchaseLine is not null);
        CreatePurchaseDraftCommand = new AsyncCommand(CreatePurchaseDraftAsync, CanCreatePurchaseDraft, HandleUnexpectedCommandFailure);
        CalculateSelectedDraftVatCommand = new AsyncCommand(CalculateSelectedDraftVatAsync, CanCalculateSelectedDraftVat, HandleUnexpectedCommandFailure);
        LoadVatReportCommand = new AsyncCommand(() => LoadVatReportAsync(1), CanLoadVatReport, HandleUnexpectedCommandFailure);
        PreviousVatPageCommand = new AsyncCommand(() => LoadVatReportAsync(_reportPage - 1), CanGoToPreviousVatPage, HandleUnexpectedCommandFailure);
        NextVatPageCommand = new AsyncCommand(() => LoadVatReportAsync(_reportPage + 1), CanGoToNextVatPage, HandleUnexpectedCommandFailure);
    }

    public string WorkspaceContextSummary => SelectedCompany is null
        ? "المشتريات: الشركة غير محددة"
        : $"المشتريات: {SelectedCompany.Name} ({SelectedCompany.BaseCurrency}) · الموردون {Suppliers.Count} · المستندات {PurchaseDrafts.Count}";

    public ApiCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (ReferenceEquals(_selectedCompany, value)) return;
            _selectedCompany = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCurrency));
            OnPropertyChanged(nameof(WorkspaceContextSummary));
            if (!_suppressCompanyReload && _apiClient is not null)
            {
                ClearCompanyRows();
                _ = LoadSelectedCompanyDataAsync();
            }
            NotifyCommands();
        }
    }

    public string SelectedCurrency => SelectedCompany?.BaseCurrency ?? "SAR";

    public ApiSupplier? SelectedSupplier
    {
        get => _selectedSupplier;
        set { if (ReferenceEquals(_selectedSupplier, value)) return; _selectedSupplier = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public PurchaseInvoiceDraftListItem? SelectedPurchaseDraft
    {
        get => _selectedPurchaseDraft;
        set
        {
            if (ReferenceEquals(_selectedPurchaseDraft, value)) return;
            _selectedPurchaseDraft = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDraftStatusSummary));
            if (value is not null && _apiClient is not null && SelectedCompany is not null)
                _ = LoadSelectedDraftDetailsAsync(value.Id);
            else
            {
                _selectedDraftDetails = null;
                SelectedDraftLines.Clear();
                OnPropertyChanged(nameof(SelectedDraftDetails));
            }
            NotifyCommands();
        }
    }

    public PurchaseInvoiceDraftDetails? SelectedDraftDetails => _selectedDraftDetails;
    public string SelectedDraftStatusSummary => SelectedPurchaseDraft is null
        ? "اختر مسودة محفوظة لعرض بنودها وحساب VAT."
        : $"{SelectedPurchaseDraft.PurchaseNumber} · فاتورة المورد {SelectedPurchaseDraft.SupplierInvoiceNumber} · {SelectedPurchaseDraft.SupplierName} · {SelectedPurchaseDraft.TaxCalculationStatus}";

    public PurchaseVatDetailsApiReport? CurrentVatReport => _currentVatReport;
    public string VatReportSummary => CurrentVatReport is null
        ? "لم يتم تحميل تقرير ضريبة المشتريات."
        : $"مسودات محسوبة VAT: {CurrentVatReport.InvoiceCount:N0} · الإجمالي قبل الخصم {CurrentVatReport.GrossAmount:N2} · الخصم {CurrentVatReport.DiscountAmount:N2} · الصافي {CurrentVatReport.NetAmount:N2} · VAT {CurrentVatReport.VatAmount:N2} · الإجمالي {CurrentVatReport.TotalAmount:N2} {SelectedCurrency}";
    public string VatReportPageSummary => CurrentVatReport is null
        ? "لا توجد صفحة محمّلة."
        : $"الصفحة {CurrentVatReport.Page} من {CurrentVatReport.TotalPages} · البنود {CurrentVatReport.Rows.Count} من {CurrentVatReport.TotalLines}";

    public string NewSupplierCode { get => _newSupplierCode; set { if (_newSupplierCode == value) return; _newSupplierCode = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewSupplierName { get => _newSupplierName; set { if (_newSupplierName == value) return; _newSupplierName = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewSupplierEmail { get => _newSupplierEmail; set { if (_newSupplierEmail == value) return; _newSupplierEmail = value; OnPropertyChanged(); } }
    public string NewSupplierPhone { get => _newSupplierPhone; set { if (_newSupplierPhone == value) return; _newSupplierPhone = value; OnPropertyChanged(); } }
    public string NewSupplierTaxNumber { get => _newSupplierTaxNumber; set { if (_newSupplierTaxNumber == value) return; _newSupplierTaxNumber = value; OnPropertyChanged(); } }

    public string PurchaseNumber { get => _purchaseNumber; set { if (_purchaseNumber == value) return; _purchaseNumber = value; OnPropertyChanged(); NotifyCommands(); } }
    public string SupplierInvoiceNumber { get => _supplierInvoiceNumber; set { if (_supplierInvoiceNumber == value) return; _supplierInvoiceNumber = value; OnPropertyChanged(); NotifyCommands(); } }
    public string PurchaseNotes { get => _purchaseNotes; set { if (_purchaseNotes == value) return; _purchaseNotes = value; OnPropertyChanged(); } }
    public DateTime? IssueDate { get => _issueDate; set { if (_issueDate == value) return; _issueDate = value; OnPropertyChanged(); NotifyCommands(); } }
    public DateTime? DueDate { get => _dueDate; set { if (_dueDate == value) return; _dueDate = value; OnPropertyChanged(); NotifyCommands(); } }
    public DateTime? ReportFromDate
    {
        get => _reportFromDate;
        set { if (_reportFromDate == value) return; _reportFromDate = value; OnPropertyChanged(); NotifyCommands(); OnPropertyChanged(nameof(WorkspaceContextSummary)); }
    }
    public DateTime? ReportToDate
    {
        get => _reportToDate;
        set { if (_reportToDate == value) return; _reportToDate = value; OnPropertyChanged(); NotifyCommands(); OnPropertyChanged(nameof(WorkspaceContextSummary)); }
    }
    public PurchaseLineEditor? SelectedNewPurchaseLine
    {
        get => _selectedNewPurchaseLine;
        set { if (ReferenceEquals(_selectedNewPurchaseLine, value)) return; _selectedNewPurchaseLine = value; OnPropertyChanged(); NotifyCommands(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); NotifyCommands(); }
    }
    public bool StatusIsError
    {
        get => _statusIsError;
        private set { if (_statusIsError == value) return; _statusIsError = value; OnPropertyChanged(); }
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            _suppressCompanyReload = true;
            Companies.Clear();
            SelectedCompany = null;
            _suppressCompanyReload = false;
            ClearCompanyRows();
            StatusMessage = "سجّل الدخول وفعّل المستأجر لفتح المشتريات.";
            StatusIsError = false;
            NotifyCommands();
            return;
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_apiClient is null)
        {
            StatusMessage = "سجّل الدخول وفعّل المستأجر أولًا.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        StatusMessage = "جارٍ تحميل الشركات والموردين ومسودات المشتريات...";
        StatusIsError = false;
        try
        {
            var companies = await _apiClient.GetCompaniesAsync();
            var previousId = SelectedCompany?.Id;
            _suppressCompanyReload = true;
            Companies.Clear();
            foreach (var company in companies) Companies.Add(company);
            SelectedCompany = (previousId.HasValue ? Companies.FirstOrDefault(x => x.Id == previousId.Value) : null)
                ?? Companies.FirstOrDefault();
            _suppressCompanyReload = false;

            if (SelectedCompany is null)
            {
                ClearCompanyRows();
                StatusMessage = "لا توجد شركة لهذا المستأجر. أنشئ شركة أولًا من مساحة المبيعات.";
                StatusIsError = false;
                return;
            }
            await LoadCompanyDataAsync();
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء تحميل المشتريات."; StatusIsError = true; }
        finally { _suppressCompanyReload = false; IsBusy = false; OnPropertyChanged(nameof(WorkspaceContextSummary)); }
    }

    public void BeginNewPurchaseDraft()
    {
        PurchaseNumber = $"PUR-{DateTime.Now:yyyyMMdd-HHmmss}";
        SupplierInvoiceNumber = "";
        PurchaseNotes = "";
        IssueDate = DateTime.Today;
        DueDate = DateTime.Today.AddDays(30);
        NewPurchaseLines.Clear();
        NewPurchaseLines.Add(new PurchaseLineEditor());
        SelectedNewPurchaseLine = NewPurchaseLines[0];
        StatusMessage = "تم تجهيز نموذج مسودة مشتريات جديدة. أدخل المورد ورقم فاتورته وبنودها ثم احفظ.";
        StatusIsError = false;
    }

    private async Task LoadSelectedCompanyDataAsync()
    {
        if (_apiClient is null || SelectedCompany is null || IsBusy) return;
        IsBusy = true;
        try
        {
            await LoadCompanyDataAsync();
            StatusMessage = "تم تحديث بيانات المشتريات للشركة المحددة.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم لتحميل بيانات الشركة المحددة."; StatusIsError = true; }
        finally { IsBusy = false; OnPropertyChanged(nameof(WorkspaceContextSummary)); }
    }

    private async Task LoadCompanyDataAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        var companyId = SelectedCompany.Id;
        var suppliersTask = _apiClient.GetSuppliersAsync(companyId);
        var draftsTask = _apiClient.GetPurchaseInvoiceDraftsAsync(companyId);
        var reportTask = _reportFromDate.HasValue && _reportToDate.HasValue && _reportFromDate.Value.Date <= _reportToDate.Value.Date
            ? _apiClient.GetPurchaseVatDetailsReportAsync(companyId, DateOnly.FromDateTime(_reportFromDate.Value.Date), DateOnly.FromDateTime(_reportToDate.Value.Date), 1, ReportPageSize)
            : Task.FromResult<PurchaseVatDetailsApiReport?>(null);
        await Task.WhenAll(suppliersTask, draftsTask, reportTask!);
        Suppliers.Clear();
        foreach (var supplier in await suppliersTask) Suppliers.Add(supplier);
        PurchaseDrafts.Clear();
        foreach (var draft in await draftsTask) PurchaseDrafts.Add(draft);
        var selectedId = SelectedPurchaseDraft?.Id;
        SelectedPurchaseDraft = (selectedId.HasValue ? PurchaseDrafts.FirstOrDefault(x => x.Id == selectedId.Value) : null)
            ?? PurchaseDrafts.FirstOrDefault();
        if (reportTask is not null && reportTask.Status == TaskStatus.RanToCompletion && reportTask.Result is { } report)
            ApplyVatReport(report);
        else if (reportTask?.IsFaulted == true)
            await reportTask;
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private async Task CreateSupplierAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        IsBusy = true;
        try
        {
            await _apiClient.CreateSupplierAsync(SelectedCompany.Id, new CreateSupplierApiRequest(
                NewSupplierCode.Trim(), NewSupplierName.Trim(),
                BlankToNull(NewSupplierEmail), BlankToNull(NewSupplierPhone), BlankToNull(NewSupplierTaxNumber)));
            NewSupplierCode = NewSupplierName = NewSupplierEmail = NewSupplierPhone = NewSupplierTaxNumber = "";
            await LoadCompanyDataAsync();
            StatusMessage = "تم حفظ المورد في قاعدة البيانات.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ المورد."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task CreatePurchaseDraftAsync()
    {
        if (!CanCreatePurchaseDraft() || _apiClient is null || SelectedCompany is null || SelectedSupplier is null) return;
        IsBusy = true;
        try
        {
            var request = new CreatePurchaseInvoiceDraftApiRequest(
                SelectedSupplier.Id, PurchaseNumber.Trim(), SupplierInvoiceNumber.Trim().ToUpperInvariant(),
                DateOnly.FromDateTime(IssueDate!.Value.Date),
                DueDate.HasValue ? DateOnly.FromDateTime(DueDate.Value.Date) : null,
                BlankToNull(PurchaseNotes),
                NewPurchaseLines.Select(line => new CreatePurchaseInvoiceDraftLineApiRequest(
                    line.Description.Trim(), line.Quantity, line.UnitPrice)).ToArray());
            var draft = await _apiClient.CreatePurchaseInvoiceDraftAsync(SelectedCompany.Id, request);
            await LoadCompanyDataAsync();
            SelectedPurchaseDraft = PurchaseDrafts.FirstOrDefault(x => x.Id == draft.Id);
            BeginNewPurchaseDraft();
            StatusMessage = "تم حفظ مسودة المشتريات. افتح بندها المحفوظ أسفل الشاشة، راجع تصنيف VAT والخصم ثم احسب VAT لحفظ القيم.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ مسودة المشتريات."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task LoadSelectedDraftDetailsAsync(Guid draftId)
    {
        if (_apiClient is null || SelectedCompany is null || IsBusy) return;
        try
        {
            var detail = await _apiClient.GetPurchaseInvoiceDraftAsync(SelectedCompany.Id, draftId);
            if (SelectedPurchaseDraft?.Id != draftId) return;
            _selectedDraftDetails = detail;
            SelectedDraftLines.Clear();
            foreach (var line in detail.Lines)
            {
                SelectedDraftLines.Add(new PurchaseLineEditor
                {
                    LineNumber = line.LineNumber,
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountAmount = line.DiscountAmount,
                    VatTreatment = line.VatTreatment == "UNCLASSIFIED" ? "StandardRated" : line.VatTreatment,
                    VatTreatmentReason = line.VatTreatmentReason ?? ""
                });
            }
            OnPropertyChanged(nameof(SelectedDraftDetails));
            OnPropertyChanged(nameof(SelectedDraftStatusSummary));
            NotifyCommands();
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم بتحميل تفاصيل مسودة المشتريات."; StatusIsError = true; }
    }

    private async Task CalculateSelectedDraftVatAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedDraftDetails is null || SelectedDraftLines.Count == 0) return;
        IsBusy = true;
        try
        {
            var request = new ApplyPurchaseDraftVatApiRequest(SelectedDraftLines
                .OrderBy(line => line.LineNumber)
                .Select(line => new ApplyPurchaseDraftVatLineApiRequest(
                    line.LineNumber, line.DiscountAmount, line.VatTreatment, BlankToNull(line.VatTreatmentReason)))
                .ToArray());
            var result = await _apiClient.CalculateAndPersistPurchaseDraftVatAsync(
                SelectedCompany.Id, SelectedDraftDetails.Id, request);
            await LoadCompanyDataAsync();
            StatusMessage = result.Message;
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم لحفظ حساب VAT لمسودة المشتريات."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task LoadVatReportAsync(int page)
    {
        if (!CanLoadVatReport() || _apiClient is null || SelectedCompany is null || !_reportFromDate.HasValue || !_reportToDate.HasValue) return;
        if (page < 1 || (CurrentVatReport is not null && page > CurrentVatReport.TotalPages)) return;
        IsBusy = true;
        try
        {
            var report = await _apiClient.GetPurchaseVatDetailsReportAsync(
                SelectedCompany.Id, DateOnly.FromDateTime(ReportFromDate!.Value.Date),
                DateOnly.FromDateTime(ReportToDate!.Value.Date), page, ReportPageSize);
            _reportPage = report.Page;
            ApplyVatReport(report);
            StatusMessage = report.Message;
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم لتحميل تقرير VAT للمشتريات."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private void ApplyVatReport(PurchaseVatDetailsApiReport report)
    {
        _currentVatReport = report;
        _reportPage = report.Page;
        PurchaseVatRows.Clear();
        foreach (var row in report.Rows) PurchaseVatRows.Add(row);
        OnPropertyChanged(nameof(CurrentVatReport));
        OnPropertyChanged(nameof(VatReportSummary));
        OnPropertyChanged(nameof(VatReportPageSummary));
        NotifyCommands();
    }

    private bool CanCreateSupplier() =>
        _apiClient is not null && SelectedCompany is not null && !IsBusy &&
        !string.IsNullOrWhiteSpace(NewSupplierCode) && !string.IsNullOrWhiteSpace(NewSupplierName);

    private bool CanCreatePurchaseDraft() =>
        _apiClient is not null && SelectedCompany is not null && SelectedSupplier is not null && !IsBusy &&
        !string.IsNullOrWhiteSpace(PurchaseNumber) && !string.IsNullOrWhiteSpace(SupplierInvoiceNumber) &&
        IssueDate.HasValue && (!DueDate.HasValue || DueDate.Value.Date >= IssueDate.Value.Date) &&
        NewPurchaseLines.Count is > 0 and <= 500 &&
        NewPurchaseLines.All(line => !string.IsNullOrWhiteSpace(line.Description) &&
                                     line.Quantity > 0m && line.UnitPrice >= 0m &&
                                     HasPrecision(line.Quantity, 4) && HasPrecision(line.UnitPrice, 4));

    private bool CanCalculateSelectedDraftVat() =>
        _apiClient is not null && SelectedCompany is not null && SelectedDraftDetails is not null &&
        SelectedDraftLines.Count > 0 && !IsBusy &&
        SelectedDraftLines.All(line => line.DiscountAmount >= 0m && HasPrecision(line.DiscountAmount, 2) &&
            (line.VatTreatment == "StandardRated" || !string.IsNullOrWhiteSpace(line.VatTreatmentReason)));

    private bool CanLoadVatReport() =>
        _apiClient is not null && SelectedCompany is not null && !IsBusy &&
        ReportFromDate.HasValue && ReportToDate.HasValue && ReportFromDate.Value.Date <= ReportToDate.Value.Date;

    private bool CanGoToPreviousVatPage() => !IsBusy && CurrentVatReport is not null && CurrentVatReport.Page > 1;
    private bool CanGoToNextVatPage() => !IsBusy && CurrentVatReport is not null && CurrentVatReport.Page < CurrentVatReport.TotalPages;
    private void AddPurchaseLine()
    {
        var line = new PurchaseLineEditor();
        NewPurchaseLines.Add(line);
        SelectedNewPurchaseLine = line;
        NotifyCommands();
    }
    private void RemoveSelectedPurchaseLine()
    {
        if (SelectedNewPurchaseLine is null || NewPurchaseLines.Count <= 1) return;
        NewPurchaseLines.Remove(SelectedNewPurchaseLine);
        SelectedNewPurchaseLine = NewPurchaseLines.FirstOrDefault();
        NotifyCommands();
    }

    private void ClearCompanyRows()
    {
        Suppliers.Clear(); PurchaseDrafts.Clear(); SelectedDraftLines.Clear(); PurchaseVatRows.Clear();
        SelectedSupplier = null;
        _selectedPurchaseDraft = null; _selectedDraftDetails = null; _currentVatReport = null;
        OnPropertyChanged(nameof(SelectedPurchaseDraft));
        OnPropertyChanged(nameof(SelectedDraftDetails));
        OnPropertyChanged(nameof(SelectedDraftStatusSummary));
        OnPropertyChanged(nameof(CurrentVatReport));
        OnPropertyChanged(nameof(VatReportSummary));
        OnPropertyChanged(nameof(VatReportPageSummary));
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool HasPrecision(decimal value, int scale) => decimal.Round(value, scale, MidpointRounding.ToEven) == value;

    private void NewPurchaseLine_PropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyCommands();
    private void SelectedDraftLine_PropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyCommands();

    private void HandleUnexpectedCommandFailure(Exception ex)
    {
        StatusMessage = $"حدث خطأ غير متوقع في شاشة المشتريات: {ex.Message}";
        StatusIsError = true;
    }

    private void NotifyCommands()
    {
        (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CreateSupplierCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (AddPurchaseLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemovePurchaseLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CreatePurchaseDraftCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CalculateSelectedDraftVatCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (LoadVatReportCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (PreviousVatPageCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (NextVatPageCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(SelectedCompany) or nameof(Suppliers) or nameof(PurchaseDrafts) or nameof(ReportFromDate) or nameof(ReportToDate))
            WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onFailure) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try { await execute(); }
            catch (Exception ex) { onFailure(ex); }
        }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PurchaseLineEditor : INotifyPropertyChanged
{
    private int _lineNumber;
    private string _description = "";
    private decimal _quantity = 1m;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private string _vatTreatment = "StandardRated";
    private string _vatTreatmentReason = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public int LineNumber { get => _lineNumber; set { if (_lineNumber == value) return; _lineNumber = value; OnChanged(); } }
    public string Description { get => _description; set { if (_description == value) return; _description = value; OnChanged(); } }
    public decimal Quantity { get => _quantity; set { if (_quantity == value) return; _quantity = value; OnChanged(); } }
    public decimal UnitPrice { get => _unitPrice; set { if (_unitPrice == value) return; _unitPrice = value; OnChanged(); } }
    public decimal DiscountAmount { get => _discountAmount; set { if (_discountAmount == value) return; _discountAmount = value; OnChanged(); } }
    public string VatTreatment { get => _vatTreatment; set { if (_vatTreatment == value) return; _vatTreatment = value; OnChanged(); } }
    public string VatTreatmentReason { get => _vatTreatmentReason; set { if (_vatTreatmentReason == value) return; _vatTreatmentReason = value; OnChanged(); } }

    private void OnChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
