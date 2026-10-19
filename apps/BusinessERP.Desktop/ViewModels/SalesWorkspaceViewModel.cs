using System.Net.Http;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Data;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

public sealed class SalesWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private ApiCustomer? _selectedCustomer;
    private SalesInvoiceDraftListItem? _selectedDraft;
    private ApiFiscalPeriod? _selectedFiscalPeriod;
    private ApiAccount? _selectedReceivableAccount;
    private ApiAccount? _selectedRevenueAccount;
    private ApiAccount? _selectedOutputVatAccount;
    private ApiAccount? _selectedInventoryAssetAccount;
    private ApiAccount? _selectedCostOfGoodsSoldAccount;
    private string _journalEntryNumber = "";
    private string _tableSearchTerm = "";
    private InvoiceLineInput? _selectedInvoiceLine;
    private string _statusMessage = "سجّل الدخول ثم فعّل المستأجر للوصول إلى بيانات المبيعات.";
    private bool _statusIsError;
    private bool _isBusy;
    private bool _loadingCompanies;
    private SalesVatPreviewApiResult? _vatPreview;
    private string _vatPreviewStatus = "معاينة VAT غير محفوظة. اختر تصنيفًا لكل بند ثم اطلب المعاينة.";

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<ApiCustomer> Customers { get; } = [];
    public ObservableCollection<InventoryItemApiDto> InventoryItems { get; } = [];
    public ObservableCollection<InventoryWarehouseApiDto> InventoryWarehouses { get; } = [];
    public ObservableCollection<SalesInvoiceDraftListItem> Drafts { get; } = [];
    public ObservableCollection<ApiAccount> Accounts { get; } = [];
    public ObservableCollection<ApiFiscalPeriod> FiscalPeriods { get; } = [];
    public ObservableCollection<ApiAccount> ReceivableAccounts { get; } = [];
    public ObservableCollection<ApiAccount> RevenueAccounts { get; } = [];
    public ObservableCollection<ApiAccount> OutputVatAccounts { get; } = [];
    public ObservableCollection<ApiAccount> InventoryAssetAccounts { get; } = [];
    public ObservableCollection<ApiAccount> CostOfGoodsSoldAccounts { get; } = [];
    public ObservableCollection<InvoiceLineInput> InvoiceLines { get; } = [];
    public ICollectionView CustomersView { get; }
    public ICollectionView DraftsView { get; }

    public IReadOnlyList<SalesVatTreatmentOption> VatTreatmentOptions { get; } =
    [
        new("StandardRated", "خاضعة 15%"),
        new("ZeroRated", "صفرية"),
        new("Exempt", "معفاة"),
        new("OutsideScope", "خارج النطاق")
    ];

    public string VatPreviewStatus
    {
        get => _vatPreviewStatus;
        private set { if (_vatPreviewStatus == value) return; _vatPreviewStatus = value; OnPropertyChanged(); }
    }

    public string VatPreviewSummary => _vatPreview is null
        ? "لا توجد معاينة حالية."
        : $"الصافي: {_vatPreview.NetAmount:N2} {_vatPreview.CurrencyCode}  |  VAT: {_vatPreview.VatAmount:N2}  |  الإجمالي: {_vatPreview.TotalAmount:N2}";

    public string CompanyName { get; set; } = "";
    public string BaseCurrency { get; set; } = "SAR";
    public string CompanyTaxNumber { get; set; } = "";
    public string CustomerCode { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string CustomerTaxNumber { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string InvoiceNotes { get; set; } = "";
    public DateTime? DraftIssueDate { get; set; } = DateTime.Today;
    public DateTime? DraftDueDate { get; set; }

    public ApiCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (ReferenceEquals(_selectedCompany, value)) return;
            _selectedCompany = value;
            ClearVatPreview("تغيرت الشركة؛ أعد حساب معاينة VAT.");
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCompanyCurrency));
            OnPropertyChanged(nameof(WorkspaceContextSummary));
            NotifyCommands();
            if (!_loadingCompanies && value is not null)
                _ = LoadCompanyDataAsync();
        }
    }

    public string SelectedCompanyCurrency => SelectedCompany?.BaseCurrency ?? "SAR";

    public string WorkspaceContextSummary => SelectedCompany is null
        ? "الشركة: غير محددة · الفترة المالية: غير محددة"
        : $"الشركة: {SelectedCompany.Name} ({SelectedCompany.BaseCurrency}) · الفترة: {SelectedFiscalPeriod?.DisplayName ?? "غير محددة"}";

    public ApiCustomer? SelectedCustomer
    {
        get => _selectedCustomer;
        set { if (ReferenceEquals(_selectedCustomer, value)) return; _selectedCustomer = value; OnPropertyChanged(); }
    }

    public SalesInvoiceDraftListItem? SelectedDraft
    {
        get => _selectedDraft;
        set
        {
            if (ReferenceEquals(_selectedDraft, value)) return;
            _selectedDraft = value;
            OnPropertyChanged();
            if (value is not null)
            {
                JournalEntryNumber = ("SI-" + value.InvoiceNumber)[..Math.Min(80, ("SI-" + value.InvoiceNumber).Length)];
                SelectedFiscalPeriod = FiscalPeriods.FirstOrDefault(x =>
                    x.Status == "OPEN" && x.StartDate <= value.IssueDate && x.EndDate >= value.IssueDate);
            }
            OnPropertyChanged(nameof(SelectedDraftPostingSummary));
            NotifyCommands();
        }
    }

    public ApiFiscalPeriod? SelectedFiscalPeriod
    {
        get => _selectedFiscalPeriod;
        set
        {
            if (ReferenceEquals(_selectedFiscalPeriod, value)) return;
            _selectedFiscalPeriod = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkspaceContextSummary));
            NotifyCommands();
        }
    }

    public ApiAccount? SelectedReceivableAccount
    {
        get => _selectedReceivableAccount;
        set { if (ReferenceEquals(_selectedReceivableAccount, value)) return; _selectedReceivableAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedRevenueAccount
    {
        get => _selectedRevenueAccount;
        set { if (ReferenceEquals(_selectedRevenueAccount, value)) return; _selectedRevenueAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedOutputVatAccount
    {
        get => _selectedOutputVatAccount;
        set { if (ReferenceEquals(_selectedOutputVatAccount, value)) return; _selectedOutputVatAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedInventoryAssetAccount
    {
        get => _selectedInventoryAssetAccount;
        set { if (ReferenceEquals(_selectedInventoryAssetAccount, value)) return; _selectedInventoryAssetAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedCostOfGoodsSoldAccount
    {
        get => _selectedCostOfGoodsSoldAccount;
        set { if (ReferenceEquals(_selectedCostOfGoodsSoldAccount, value)) return; _selectedCostOfGoodsSoldAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public string JournalEntryNumber
    {
        get => _journalEntryNumber;
        set { if (_journalEntryNumber == value) return; _journalEntryNumber = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public string SelectedDraftPostingSummary => SelectedDraft is null
        ? "حدد مسودة محفوظة لعرض إجمالياتها وإعداد الترحيل."
        : $"المسودة: {SelectedDraft.InvoiceNumber} · الحالة: {SelectedDraft.Status} · VAT: {SelectedDraft.VatAmount:N2} · الإجمالي: {SelectedDraft.TotalAmount:N2} {SelectedDraft.CurrencyCode}";

    private bool CanPostSelectedDraft =>
        _apiClient is not null && SelectedCompany is not null && SelectedDraft is not null &&
        SelectedDraft.Status == "DRAFT" && SelectedDraft.TaxCalculationStatus == "CALCULATED" &&
        SelectedDraft.TotalAmount.HasValue && SelectedDraft.TotalAmount.Value > 0m &&
        SelectedFiscalPeriod is { Status: "OPEN" } period &&
        period.StartDate <= SelectedDraft.IssueDate && period.EndDate >= SelectedDraft.IssueDate &&
        SelectedReceivableAccount is not null && SelectedReceivableAccount.AccountType == "ASSET" &&
        SelectedReceivableAccount.NormalBalance == "DEBIT" && SelectedReceivableAccount.IsActive &&
        SelectedReceivableAccount.AllowPosting &&
        SelectedRevenueAccount is not null && SelectedRevenueAccount.AccountType == "REVENUE" &&
        SelectedRevenueAccount.NormalBalance == "CREDIT" && SelectedRevenueAccount.IsActive &&
        SelectedRevenueAccount.AllowPosting &&
        (SelectedDraft.VatAmount is null or <= 0m ||
         (SelectedOutputVatAccount is not null && SelectedOutputVatAccount.AccountType == "LIABILITY" &&
          SelectedOutputVatAccount.NormalBalance == "CREDIT" && SelectedOutputVatAccount.IsActive &&
          SelectedOutputVatAccount.AllowPosting));

    public InvoiceLineInput? SelectedInvoiceLine
    {
        get => _selectedInvoiceLine;
        set { if (ReferenceEquals(_selectedInvoiceLine, value)) return; _selectedInvoiceLine = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set { if (_statusIsError == value) return; _statusIsError = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ICommand RefreshCommand { get; }
    public ICommand CreateCompanyCommand { get; }
    public ICommand CreateCustomerCommand { get; }
    public ICommand AddInvoiceLineCommand { get; }
    public ICommand RemoveInvoiceLineCommand { get; }
    public ICommand PostSelectedDraftToLedgerCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SalesWorkspaceViewModel()
    {
        CustomersView = CollectionViewSource.GetDefaultView(Customers);
        CustomersView.Filter = FilterCustomer;
        DraftsView = CollectionViewSource.GetDefaultView(Drafts);
        DraftsView.Filter = FilterDraft;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _apiClient is not null && !IsBusy);
        CreateCompanyCommand = new AsyncRelayCommand(CreateCompanyAsync, () => _apiClient is not null && !IsBusy);
        CreateCustomerCommand = new AsyncRelayCommand(CreateCustomerAsync, () => _apiClient is not null && SelectedCompany is not null && !IsBusy);
        AddInvoiceLineCommand = new RelayCommand(AddInvoiceLine, () => SelectedCompany is not null && !IsBusy);
        RemoveInvoiceLineCommand = new RelayCommand(() =>
        {
            if (SelectedInvoiceLine is not null)
            {
                SelectedInvoiceLine.PropertyChanged -= InvoiceLine_PropertyChanged;
                InvoiceLines.Remove(SelectedInvoiceLine);
                SelectedInvoiceLine = null;
            }
        }, () => SelectedInvoiceLine is not null && !IsBusy);
        PostSelectedDraftToLedgerCommand = new AsyncRelayCommand(PostSelectedDraftToLedgerAsync, () => CanPostSelectedDraft && !IsBusy);

        AddInvoiceLine();
    }

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            Companies.Clear();
            Customers.Clear();
            Drafts.Clear();
            _tableSearchTerm = "";
            CustomersView.Refresh();
            DraftsView.Refresh();
            Accounts.Clear();
            FiscalPeriods.Clear();
            ReceivableAccounts.Clear();
            RevenueAccounts.Clear();
            OutputVatAccounts.Clear();
            InventoryItems.Clear(); InventoryWarehouses.Clear(); InventoryAssetAccounts.Clear(); CostOfGoodsSoldAccounts.Clear();
            SelectedInventoryAssetAccount = null; SelectedCostOfGoodsSoldAccount = null;
            SelectedCompany = null;
            SelectedCustomer = null;
            SelectedDraft = null;
            SelectedFiscalPeriod = null;
            SelectedReceivableAccount = null;
            SelectedRevenueAccount = null;
            SelectedOutputVatAccount = null;
            ClearVatPreview("سجّل الدخول وفعّل المستأجر قبل حساب معاينة VAT.");
            StatusMessage = "سجّل الدخول ثم فعّل معرّف المستأجر للوصول إلى البيانات المحفوظة.";
            StatusIsError = false;
            NotifyCommands();
            return;
        }

        await RefreshAsync();
    }

    public bool SearchLoadedData(string searchText)
    {
        var term = (searchText ?? "").Trim();
        if (term.Length == 0)
        {
            _tableSearchTerm = "";
            CustomersView.Refresh();
            DraftsView.Refresh();
            StatusMessage = "تم مسح البحث وإظهار جميع العملاء والمسودات المحمّلة.";
            StatusIsError = false;
            return false;
        }
        if (term.Length < 2)
        {
            StatusMessage = "اكتب حرفين على الأقل للبحث في بيانات المستأجر المحمّلة حاليًا.";
            StatusIsError = true;
            return false;
        }

        _tableSearchTerm = term;
        CustomersView.Refresh();
        DraftsView.Refresh();

        var matchingCompanies = Companies.Where(x =>
            Contains(x.Name, term) || Contains(x.BaseCurrency, term) || Contains(x.TaxNumber, term)).ToList();
        var matchingCustomers = Customers.Where(x =>
            Contains(x.Code, term) || Contains(x.Name, term) || Contains(x.Email, term) ||
            Contains(x.Phone, term) || Contains(x.TaxNumber, term)).ToList();
        var matchingAccounts = Accounts.Where(x =>
            Contains(x.Code, term) || Contains(x.NameAr, term) || Contains(x.NameEn, term)).ToList();
        var matchingDrafts = Drafts.Where(x =>
            Contains(x.InvoiceNumber, term) || Contains(x.CustomerName, term) ||
            Contains(x.Status, term) || Contains(x.TaxCalculationStatus, term)).ToList();

        if (matchingCustomers.Count == 0 && matchingAccounts.Count == 0 && matchingDrafts.Count == 0 &&
            matchingCompanies.Count == 0)
        {
            StatusMessage = $"لم يتم العثور على «{term}» في الشركات والعملاء والحسابات والمسودات المحمّلة.";
            StatusIsError = false;
            return false;
        }

        if (matchingCustomers.Count > 0)
            SelectedCustomer = matchingCustomers[0];
        if (matchingDrafts.Count > 0)
            SelectedDraft = matchingDrafts[0];

        StatusMessage = $"نتائج «{term}»: شركات {matchingCompanies.Count} · عملاء {matchingCustomers.Count} · حسابات {matchingAccounts.Count} · مسودات {matchingDrafts.Count}. البحث ضمن البيانات المحمّلة للمستأجر الحالي فقط.";
        StatusIsError = false;
        return matchingCustomers.Count > 0 || matchingAccounts.Count > 0 || matchingDrafts.Count > 0;
    }

    public void PrepareNewDraft()
    {
        if (_apiClient is null || SelectedCompany is null)
        {
            StatusMessage = "سجّل الدخول وفعّل المستأجر واختر الشركة قبل إنشاء مسودة.";
            StatusIsError = true;
            return;
        }

        _tableSearchTerm = "";
        CustomersView.Refresh();
        DraftsView.Refresh();
        InvoiceNumber = "SI-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        InvoiceNotes = "";
        DraftIssueDate = DateTime.Today;
        DraftDueDate = null;
        OnPropertyChanged(nameof(InvoiceNumber));
        OnPropertyChanged(nameof(InvoiceNotes));
        OnPropertyChanged(nameof(DraftIssueDate));
        OnPropertyChanged(nameof(DraftDueDate));
        foreach (var line in InvoiceLines)
            line.PropertyChanged -= InvoiceLine_PropertyChanged;
        InvoiceLines.Clear();
        SelectedInvoiceLine = null;
        AddInvoiceLine();
        ClearVatPreview("مسودة جديدة؛ راجع تصنيف VAT قبل الحفظ.");
        StatusMessage = "تم تجهيز نموذج مسودة جديدة. لم تُحفظ أي بيانات بعد.";
        StatusIsError = false;
    }

    private bool FilterCustomer(object item)
    {
        if (_tableSearchTerm.Length == 0) return true;
        return item is ApiCustomer customer &&
               (Contains(customer.Code, _tableSearchTerm) || Contains(customer.Name, _tableSearchTerm) ||
                Contains(customer.Email, _tableSearchTerm) || Contains(customer.Phone, _tableSearchTerm) ||
                Contains(customer.TaxNumber, _tableSearchTerm));
    }

    private bool FilterDraft(object item)
    {
        if (_tableSearchTerm.Length == 0) return true;
        return item is SalesInvoiceDraftListItem draft &&
               (Contains(draft.InvoiceNumber, _tableSearchTerm) || Contains(draft.CustomerName, _tableSearchTerm) ||
                Contains(draft.Status, _tableSearchTerm) || Contains(draft.TaxCalculationStatus, _tableSearchTerm));
    }

    private static bool Contains(string? source, string term) =>
        !string.IsNullOrEmpty(source) && source.Contains(term, StringComparison.OrdinalIgnoreCase);

    public async Task RefreshAsync()
    {
        if (_apiClient is null)
        {
            StatusMessage = "سجّل الدخول وفعّل معرّف المستأجر أولًا.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        StatusMessage = "جارٍ تحميل الشركات والعملاء ومسودات المبيعات من الخادم…";
        StatusIsError = false;
        try
        {
            var rows = await _apiClient.GetCompaniesAsync();
            _loadingCompanies = true;
            try
            {
                var oldId = _selectedCompany?.Id;
                Companies.Clear();
                foreach (var company in rows)
                    Companies.Add(company);

                SelectedCompany = (oldId.HasValue ? Companies.FirstOrDefault(x => x.Id == oldId.Value) : null)
                    ?? Companies.FirstOrDefault();
            }
            finally
            {
                _loadingCompanies = false;
            }

            if (SelectedCompany is null)
            {
                Customers.Clear();
                Drafts.Clear();
                _tableSearchTerm = "";
                CustomersView.Refresh();
                DraftsView.Refresh();
                Accounts.Clear();
                FiscalPeriods.Clear();
                ReceivableAccounts.Clear();
                RevenueAccounts.Clear();
                OutputVatAccounts.Clear();
                SelectedDraft = null;
                StatusMessage = "لا توجد شركات لهذا المستأجر. استخدم نموذج إنشاء الشركة إذا كانت صلاحيتك OWNER أو ADMIN.";
                StatusIsError = false;
            }
            else
            {
                var companyDataLoaded = await LoadCompanyDataAsync();
                if (companyDataLoaded)
                {
                    StatusMessage = $"تم تحميل {Companies.Count} شركة وبيانات الشركة المحددة.";
                    StatusIsError = false;
                }
            }
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم. تحقق من عنوان API والشبكة.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task<bool> LoadCompanyDataAsync()
    {
        if (_apiClient is null || SelectedCompany is null || _loadingCompanies)
            return false;

        try
        {
            var companyId = SelectedCompany.Id;
            var customerTask = _apiClient.GetCustomersAsync(companyId);
            var inventoryItemsTask = _apiClient.GetInventoryItemsAsync(companyId);
            var inventoryWarehousesTask = _apiClient.GetInventoryWarehousesAsync(companyId);
            var draftTask = _apiClient.GetSalesInvoiceDraftsAsync(companyId);
            var accountTask = _apiClient.GetAccountsAsync(companyId);
            var periodTask = _apiClient.GetFiscalPeriodsAsync(companyId);
            await Task.WhenAll(customerTask, inventoryItemsTask, inventoryWarehousesTask, draftTask, accountTask, periodTask);
            var customers = await customerTask;
            var drafts = await draftTask;
            var accounts = await accountTask;
            var periods = await periodTask;

            Customers.Clear();
            foreach (var customer in customers)
                Customers.Add(customer);
            InventoryItems.Clear();
            foreach (var item in await inventoryItemsTask) InventoryItems.Add(item);
            InventoryWarehouses.Clear();
            foreach (var warehouse in await inventoryWarehousesTask) InventoryWarehouses.Add(warehouse);
            Drafts.Clear();
            foreach (var draft in drafts)
                Drafts.Add(draft);
            Accounts.Clear();
            foreach (var account in accounts)
                Accounts.Add(account);
            FiscalPeriods.Clear();
            foreach (var period in periods)
                FiscalPeriods.Add(period);
            RebuildPostingAccountChoices();

            if (SelectedCustomer is null || !Customers.Any(x => x.Id == SelectedCustomer.Id))
                SelectedCustomer = Customers.FirstOrDefault();
            if (SelectedDraft is not null)
                SelectedDraft = Drafts.FirstOrDefault(x => x.Id == SelectedDraft.Id);
            else
                SelectedDraft = Drafts.FirstOrDefault();
            if (SelectedFiscalPeriod is null || !FiscalPeriods.Any(x => x.Id == SelectedFiscalPeriod.Id))
                SelectedFiscalPeriod = FiscalPeriods.FirstOrDefault(x => x.Status == "OPEN");
            if (SelectedReceivableAccount is null || !ReceivableAccounts.Any(x => x.Id == SelectedReceivableAccount.Id))
                SelectedReceivableAccount = ReceivableAccounts.FirstOrDefault();
            if (SelectedRevenueAccount is null || !RevenueAccounts.Any(x => x.Id == SelectedRevenueAccount.Id))
                SelectedRevenueAccount = RevenueAccounts.FirstOrDefault();
            if (SelectedOutputVatAccount is null || !OutputVatAccounts.Any(x => x.Id == SelectedOutputVatAccount.Id))
                SelectedOutputVatAccount = OutputVatAccounts.FirstOrDefault();
            if (SelectedInventoryAssetAccount is null || !InventoryAssetAccounts.Any(x => x.Id == SelectedInventoryAssetAccount.Id))
                SelectedInventoryAssetAccount = InventoryAssetAccounts.FirstOrDefault();
            if (SelectedCostOfGoodsSoldAccount is null || !CostOfGoodsSoldAccounts.Any(x => x.Id == SelectedCostOfGoodsSoldAccount.Id))
                SelectedCostOfGoodsSoldAccount = CostOfGoodsSoldAccounts.FirstOrDefault();

            StatusMessage = $"الشركة: {SelectedCompany.Name} · العملاء {Customers.Count} · مسودات البيع {Drafts.Count} · الحسابات {Accounts.Count} · الفترات {FiscalPeriods.Count}.";
            StatusIsError = false;
            return true;
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
            return false;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر تحميل بيانات الشركة من الخادم.";
            StatusIsError = true;
            return false;
        }
        finally
        {
            NotifyCommands();
        }
    }

    private async Task CreateCompanyAsync()
    {
        if (_apiClient is null) return;
        var name = CompanyName.Trim();
        var currency = BaseCurrency.Trim().ToUpperInvariant();
        if (name.Length < 2 || currency.Length != 3)
        {
            StatusMessage = "أدخل اسم شركة من حرفين على الأقل ورمز عملة من ثلاثة أحرف مثل SAR.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            await _apiClient.CreateCompanyAsync(new CreateCompanyApiRequest(
                name, currency, string.IsNullOrWhiteSpace(CompanyTaxNumber) ? null : CompanyTaxNumber.Trim()));
            CompanyName = "";
            CompanyTaxNumber = "";
            OnPropertyChanged(nameof(CompanyName));
            OnPropertyChanged(nameof(CompanyTaxNumber));
            StatusMessage = "تم إنشاء الشركة وحفظها في قاعدة البيانات.";
            StatusIsError = false;
            await RefreshAsync();
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء إنشاء الشركة.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task CreateCustomerAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        if (string.IsNullOrWhiteSpace(CustomerCode) || string.IsNullOrWhiteSpace(CustomerName))
        {
            StatusMessage = "رمز العميل واسمه مطلوبان.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            var created = await _apiClient.CreateCustomerAsync(SelectedCompany.Id,
                new CreateCustomerApiRequest(
                    CustomerCode.Trim().ToUpperInvariant(), CustomerName.Trim(),
                    NullIfBlank(CustomerEmail), NullIfBlank(CustomerPhone), NullIfBlank(CustomerTaxNumber)));
            CustomerCode = CustomerName = CustomerEmail = CustomerPhone = CustomerTaxNumber = "";
            OnPropertyChanged(nameof(CustomerCode));
            OnPropertyChanged(nameof(CustomerName));
            OnPropertyChanged(nameof(CustomerEmail));
            OnPropertyChanged(nameof(CustomerPhone));
            OnPropertyChanged(nameof(CustomerTaxNumber));
            var refreshed = await LoadCompanyDataAsync();
            SelectedCustomer = Customers.FirstOrDefault(x => x.Id == created.Id);
            if (refreshed)
            {
                StatusMessage = $"تم حفظ العميل {created.Code} في قاعدة البيانات.";
                StatusIsError = false;
            }
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ العميل.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task CreateInvoiceDraftAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        if (SelectedCustomer is null)
        {
            StatusMessage = "أنشئ عميلًا أو اختر عميلًا قبل حفظ المسودة.";
            StatusIsError = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(InvoiceNumber))
        {
            StatusMessage = "رقم مسودة البيع مطلوب.";
            StatusIsError = true;
            return;
        }
        if (!DraftIssueDate.HasValue)
        {
            StatusMessage = "تاريخ إصدار المسودة مطلوب.";
            StatusIsError = true;
            return;
        }
        if (DraftDueDate.HasValue && DraftDueDate.Value.Date < DraftIssueDate.Value.Date)
        {
            StatusMessage = "تاريخ الاستحقاق لا يمكن أن يسبق تاريخ الإصدار.";
            StatusIsError = true;
            return;
        }

        var lines = InvoiceLines.Select(x => new CreateSalesInvoiceDraftLineApiRequest(
            x.Description.Trim(), x.Quantity, x.UnitPrice, x.InventoryItemId, x.InventoryWarehouseId,
            ParseSerialNumbers(x.SerialNumbersText))).ToList();
        if (lines.Count == 0 || lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.Quantity <= 0m || x.UnitPrice < 0m) ||
            InvoiceLines.Any(x => x.DiscountAmount < 0m ||
                (x.VatTreatment != "StandardRated" && string.IsNullOrWhiteSpace(x.TreatmentReason))))
        {
            StatusMessage = "تأكد من وجود بند واحد على الأقل، ووصف لكل بند، وكمية موجبة وسعر غير سالب.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            var created = await _apiClient.CreateSalesInvoiceDraftAsync(SelectedCompany.Id,
                new CreateSalesInvoiceDraftApiRequest(
                    SelectedCustomer.Id, InvoiceNumber.Trim().ToUpperInvariant(),
                    DateOnly.FromDateTime(DraftIssueDate.Value.Date),
                    DraftDueDate.HasValue ? DateOnly.FromDateTime(DraftDueDate.Value.Date) : null,
                    NullIfBlank(InvoiceNotes), lines));

            PersistedSalesDraftVatResult taxResult;
            try
            {
                var vatLines = InvoiceLines.Select((line, index) =>
                    new ApplySalesDraftVatLineApiRequest(
                        index + 1, line.DiscountAmount, line.VatTreatment,
                        line.VatTreatment == "StandardRated" ? null : NullIfBlank(line.TreatmentReason))).ToList();
                taxResult = await _apiClient.CalculateAndPersistSalesDraftVatAsync(
                    SelectedCompany.Id, created.Id, new ApplySalesDraftVatApiRequest(vatLines));
            }
            catch (ErpApiException taxError)
            {
                // The draft exists but the tax calculation did not commit. Prevent accidental duplicate-number retry.
                InvoiceNumber = "";
                OnPropertyChanged(nameof(InvoiceNumber));
                await LoadCompanyDataAsync();
                StatusMessage = $"حُفظت المسودة {created.InvoiceNumber}، لكن لم يُحفظ حساب VAT: {taxError.Message}. المسودة غير نهائية وغير مصنفة بالكامل.";
                StatusIsError = true;
                return;
            }

            InvoiceNumber = "";
            InvoiceNotes = "";
            OnPropertyChanged(nameof(InvoiceNumber));
            OnPropertyChanged(nameof(InvoiceNotes));
            foreach (var line in InvoiceLines) line.PropertyChanged -= InvoiceLine_PropertyChanged;
            InvoiceLines.Clear();
            AddInvoiceLine();
            OnPropertyChanged(nameof(InvoiceLines));
            ClearVatPreview("تم حفظ حساب VAT داخل المسودة؛ المستند غير نهائي ولا يُرسل إلى ZATCA.");

            var refreshed = await LoadCompanyDataAsync();
            if (refreshed)
            {
                StatusMessage = $"تم حفظ المسودة {created.InvoiceNumber}: الصافي {taxResult.NetAmount:N2}، VAT {taxResult.VatAmount:N2}، الإجمالي {taxResult.TotalAmount:N2} {created.CurrencyCode}. مسودة داخلية غير نهائية.";
                StatusIsError = false;
            }
            else
            {
                StatusMessage = $"تم حفظ المسودة {created.InvoiceNumber} والإجمالي {taxResult.TotalAmount:N2} {created.CurrencyCode}، لكن تعذر تحديث القائمة. اضغط «تحديث البيانات» لإعادة التحميل.";
                StatusIsError = true;
            }
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ مسودة البيع.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task PostSelectedDraftToLedgerAsync()
    {
        if (_apiClient is null || SelectedCompany is null || SelectedDraft is null)
            return;
        if (!CanPostSelectedDraft)
        {
            StatusMessage = "تحقق من اختيار مسودة محسوبة VAT، وفترة مفتوحة تشمل تاريخها، وحسابات ذمم/إيراد/ضريبة صالحة.";
            StatusIsError = true;
            return;
        }
        var draft = SelectedDraft;
        var companyId = SelectedCompany.Id;
        var entryNumber = JournalEntryNumber.Trim().ToUpperInvariant();
        if (entryNumber.Length is < 1 or > 80)
        {
            StatusMessage = "رقم القيد مطلوب ولا يتجاوز 80 حرفًا.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _apiClient.PostSalesDraftToLedgerAsync(companyId, draft.Id,
                new PostSalesDraftToLedgerApiRequest(
                    SelectedFiscalPeriod!.Id,
                    entryNumber,
                    SelectedReceivableAccount!.Id,
                    SelectedRevenueAccount!.Id,
                    draft.VatAmount.GetValueOrDefault() > 0m ? SelectedOutputVatAccount!.Id : null,
                    SelectedInventoryAssetAccount?.Id, SelectedCostOfGoodsSoldAccount?.Id));
            await LoadCompanyDataAsync();
            SelectedDraft = Drafts.FirstOrDefault(x => x.Id == draft.Id);
            StatusMessage = result.AlreadyPosted
                ? $"المسودة مرتبطة مسبقًا بالقيد {result.EntryNumber}؛ لم يُنشأ قيد مكرر."
                : $"تم ترحيل الأثر المحاسبي للمسودة {draft.InvoiceNumber} إلى القيد {result.EntryNumber}. الإجمالي {result.TotalAmount:N2} {draft.CurrencyCode}. لم يصدر مستند ضريبي ولم يُرسل إلى ZATCA.";
            StatusIsError = false;
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء ترحيل المسودة إلى دفتر الأستاذ.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void RebuildPostingAccountChoices()
    {
        ReceivableAccounts.Clear();
        RevenueAccounts.Clear();
        OutputVatAccounts.Clear();
        InventoryAssetAccounts.Clear();
        CostOfGoodsSoldAccounts.Clear();
        foreach (var account in Accounts.Where(x => x.IsActive && x.AllowPosting))
        {
            if (account.AccountType == "ASSET" && account.NormalBalance == "DEBIT")
                ReceivableAccounts.Add(account);
            else if (account.AccountType == "REVENUE" && account.NormalBalance == "CREDIT")
                RevenueAccounts.Add(account);
            else if (account.AccountType == "LIABILITY" && account.NormalBalance == "CREDIT")
                OutputVatAccounts.Add(account);
            if (account.AccountType == "ASSET" && account.NormalBalance == "DEBIT")
                InventoryAssetAccounts.Add(account);
            if (account.AccountType == "EXPENSE" && account.NormalBalance == "DEBIT")
                CostOfGoodsSoldAccounts.Add(account);
        }
        OnPropertyChanged(nameof(Accounts));
        OnPropertyChanged(nameof(FiscalPeriods));
        NotifyCommands();
    }

    public async Task CalculateVatPreviewAsync()
    {
        if (_apiClient is null || SelectedCompany is null)
        {
            StatusMessage = "سجّل الدخول وفعّل المستأجر قبل حساب معاينة VAT.";
            StatusIsError = true;
            return;
        }

        var inputLines = InvoiceLines.ToList();
        if (inputLines.Count is < 1 or > 500 ||
            inputLines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.Quantity <= 0m || x.UnitPrice < 0m))
        {
            StatusMessage = "للمعاينة، أدخل من 1 إلى 500 بندًا بوصف وكمية موجبة وسعر غير سالب.";
            StatusIsError = true;
            return;
        }

        if (inputLines.Any(x => x.VatTreatment != "StandardRated" && string.IsNullOrWhiteSpace(x.TreatmentReason)))
        {
            StatusMessage = "أدخل سبب التصنيف لكل بند غير خاضع للمعدل القياسي.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        ClearVatPreview("جارٍ حساب معاينة VAT من API…");
        try
        {
            var lines = inputLines.Select(x => new SalesVatPreviewLineApiRequest(
                x.Description.Trim(), x.Quantity, x.UnitPrice, x.DiscountAmount, x.VatTreatment,
                x.VatTreatment == "StandardRated" ? null : NullIfBlank(x.TreatmentReason))).ToList();
            var result = await _apiClient.CalculateSalesVatPreviewAsync(
                SelectedCompany.Id, new CalculateSalesVatPreviewApiRequest(lines));
            _vatPreview = result;
            OnPropertyChanged(nameof(VatPreviewSummary));
            VatPreviewStatus = result.Message;
            StatusMessage = $"تم حساب معاينة VAT غير المحفوظة لإجمالي {result.TotalAmount:N2} {result.CurrencyCode}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex)
        {
            ClearVatPreview("تعذر حساب معاينة VAT.");
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            ClearVatPreview("تعذر حساب معاينة VAT.");
            StatusMessage = "تعذر الاتصال بالخادم أثناء معاينة VAT.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void AddInvoiceLine()
    {
        var line = new InvoiceLineInput();
        line.PropertyChanged += InvoiceLine_PropertyChanged;
        InvoiceLines.Add(line);
    }

    private void InvoiceLine_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InvoiceLineInput.Description) or nameof(InvoiceLineInput.Quantity) or
            nameof(InvoiceLineInput.UnitPrice) or nameof(InvoiceLineInput.DiscountAmount) or nameof(InvoiceLineInput.VatTreatment) or
            nameof(InvoiceLineInput.TreatmentReason))
            ClearVatPreview("تغيرت بيانات البنود؛ أعد حساب معاينة VAT.");
    }

    private void ClearVatPreview(string message)
    {
        _vatPreview = null;
        VatPreviewStatus = message;
        OnPropertyChanged(nameof(VatPreviewSummary));
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] ParseSerialNumbers(string? text) =>
        (text ?? "").Split(new[] { '\r', '\n', ',', ';', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToUpperInvariant()).ToArray();

    private void NotifyCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateCompanyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateCustomerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddInvoiceLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveInvoiceLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PostSelectedDraftToLedgerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class InvoiceLineInput : INotifyPropertyChanged
{
    private string _description = "";
    private decimal _quantity = 1m;
    private decimal _unitPrice;
    private decimal _discountAmount;
    private string _vatTreatment = "StandardRated";
    private string _treatmentReason = "";
    private Guid? _inventoryItemId;
    private Guid? _inventoryWarehouseId;
    private string _serialNumbersText = "";

    public Guid? InventoryItemId
    {
        get => _inventoryItemId;
        set { if (_inventoryItemId == value) return; _inventoryItemId = value; OnPropertyChanged(); }
    }

    public Guid? InventoryWarehouseId
    {
        get => _inventoryWarehouseId;
        set { if (_inventoryWarehouseId == value) return; _inventoryWarehouseId = value; OnPropertyChanged(); }
    }

    public string SerialNumbersText
    {
        get => _serialNumbersText;
        set { if (_serialNumbersText == value) return; _serialNumbersText = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { if (_description == value) return; _description = value; OnPropertyChanged(); }
    }

    public decimal Quantity
    {
        get => _quantity;
        set { if (_quantity == value) return; _quantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(LineAmount)); }
    }

    public decimal UnitPrice
    {
        get => _unitPrice;
        set { if (_unitPrice == value) return; _unitPrice = value; OnPropertyChanged(); OnPropertyChanged(nameof(LineAmount)); }
    }

    public decimal DiscountAmount
    {
        get => _discountAmount;
        set { if (_discountAmount == value) return; _discountAmount = value; OnPropertyChanged(); OnPropertyChanged(nameof(LineAmount)); }
    }

    public string VatTreatment
    {
        get => _vatTreatment;
        set { if (_vatTreatment == value) return; _vatTreatment = value; OnPropertyChanged(); }
    }

    public string TreatmentReason
    {
        get => _treatmentReason;
        set { if (_treatmentReason == value) return; _treatmentReason = value; OnPropertyChanged(); }
    }

    public decimal LineAmount
    {
        get
        {
            try { return decimal.Round(Quantity * UnitPrice, 4, MidpointRounding.AwayFromZero); }
            catch (OverflowException) { return 0m; }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        catch (Exception) { /* View-model commands handle expected API failures; keep WPF event dispatch alive. */ }
        finally { _isExecuting = false; RaiseCanExecuteChanged(); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record SalesVatTreatmentOption(string Value, string Label);
