using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

public sealed class SalesWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private ApiCustomer? _selectedCustomer;
    private InvoiceLineInput? _selectedInvoiceLine;
    private string _statusMessage = "سجّل الدخول ثم فعّل المستأجر للوصول إلى بيانات المبيعات.";
    private bool _statusIsError;
    private bool _isBusy;
    private bool _loadingCompanies;

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<ApiCustomer> Customers { get; } = [];
    public ObservableCollection<SalesInvoiceDraftListItem> Drafts { get; } = [];
    public ObservableCollection<InvoiceLineInput> InvoiceLines { get; } = [];

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
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCompanyCurrency));
            NotifyCommands();
            if (!_loadingCompanies && value is not null)
                _ = LoadCompanyDataAsync();
        }
    }

    public string SelectedCompanyCurrency => SelectedCompany?.BaseCurrency ?? "SAR";

    public ApiCustomer? SelectedCustomer
    {
        get => _selectedCustomer;
        set { if (ReferenceEquals(_selectedCustomer, value)) return; _selectedCustomer = value; OnPropertyChanged(); }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public SalesWorkspaceViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _apiClient is not null && !IsBusy);
        CreateCompanyCommand = new AsyncRelayCommand(CreateCompanyAsync, () => _apiClient is not null && !IsBusy);
        CreateCustomerCommand = new AsyncRelayCommand(CreateCustomerAsync, () => _apiClient is not null && SelectedCompany is not null && !IsBusy);
        AddInvoiceLineCommand = new RelayCommand(() =>
        {
            InvoiceLines.Add(new InvoiceLineInput());
        }, () => SelectedCompany is not null && !IsBusy);
        RemoveInvoiceLineCommand = new RelayCommand(() =>
        {
            if (SelectedInvoiceLine is not null)
            {
                InvoiceLines.Remove(SelectedInvoiceLine);
                SelectedInvoiceLine = null;
            }
        }, () => SelectedInvoiceLine is not null && !IsBusy);

        InvoiceLines.Add(new InvoiceLineInput());
    }

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            Companies.Clear();
            Customers.Clear();
            Drafts.Clear();
            SelectedCompany = null;
            SelectedCustomer = null;
            StatusMessage = "سجّل الدخول ثم فعّل معرّف المستأجر للوصول إلى البيانات المحفوظة.";
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
            var draftTask = _apiClient.GetSalesInvoiceDraftsAsync(companyId);
            await Task.WhenAll(customerTask, draftTask);
            var customers = await customerTask;
            var drafts = await draftTask;

            Customers.Clear();
            foreach (var customer in customers)
                Customers.Add(customer);
            Drafts.Clear();
            foreach (var draft in drafts)
                Drafts.Add(draft);

            if (SelectedCustomer is null || !Customers.Any(x => x.Id == SelectedCustomer.Id))
                SelectedCustomer = Customers.FirstOrDefault();

            StatusMessage = $"الشركة: {SelectedCompany.Name} · العملاء {Customers.Count} · مسودات البيع {Drafts.Count}.";
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
            x.Description.Trim(), x.Quantity, x.UnitPrice)).ToList();
        if (lines.Count == 0 || lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.Quantity <= 0m || x.UnitPrice < 0m))
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
            InvoiceNumber = "";
            InvoiceNotes = "";
            OnPropertyChanged(nameof(InvoiceNumber));
            OnPropertyChanged(nameof(InvoiceNotes));
            InvoiceLines.Clear();
            InvoiceLines.Add(new InvoiceLineInput());
            OnPropertyChanged(nameof(InvoiceLines));

            var refreshed = await LoadCompanyDataAsync();
            if (refreshed)
            {
                StatusMessage = $"تم حفظ المسودة {created.InvoiceNumber} بإجمالي فرعي {created.SubtotalAmount:N4} {created.CurrencyCode}. هذه مسودة داخلية غير ضريبية.";
                StatusIsError = false;
            }
            else
            {
                StatusMessage = $"تم حفظ المسودة {created.InvoiceNumber}، لكن تعذر تحديث القائمة. اضغط «تحديث البيانات» لإعادة التحميل.";
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

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void NotifyCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateCompanyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateCustomerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddInvoiceLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveInvoiceLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class InvoiceLineInput : INotifyPropertyChanged
{
    private string _description = "";
    private decimal _quantity = 1m;
    private decimal _unitPrice;

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
