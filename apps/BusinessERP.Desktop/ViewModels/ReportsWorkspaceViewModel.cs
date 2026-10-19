using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

public sealed class ReportsWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private DateTime? _fromDate = new DateTime(DateTime.Today.Year, 1, 1);
    private DateTime? _toDate = DateTime.Today;
    private DateTime? _asOfDate = DateTime.Today;
    private bool _isBusy;
    private string _statusMessage = "فعّل المستأجر لعرض التقارير المالية من القيود المرحلة.";
    private bool _statusIsError;
    private string _trialBalanceSummary = "لم يتم تحميل ميزان المراجعة.";
    private string _incomeSummary = "لم يتم تحميل قائمة الدخل.";
    private string _balanceSummary = "لم يتم تحميل الميزانية.";
    
    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<TrialBalanceApiRow> TrialBalanceRows { get; } = [];
    public ObservableCollection<FinancialReportLine> IncomeRows { get; } = [];
    public ObservableCollection<FinancialReportLine> BalanceRows { get; } = [];

    public ApiCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (ReferenceEquals(_selectedCompany, value)) return;
            _selectedCompany = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCurrency));
            NotifyCommands();
        }
    }

    public string SelectedCurrency => SelectedCompany?.BaseCurrency ?? "SAR";

    public DateTime? FromDate
    {
        get => _fromDate;
        set { if (_fromDate == value) return; _fromDate = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public DateTime? ToDate
    {
        get => _toDate;
        set { if (_toDate == value) return; _toDate = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public DateTime? AsOfDate
    {
        get => _asOfDate;
        set { if (_asOfDate == value) return; _asOfDate = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); NotifyCommands(); }
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

    public string TrialBalanceSummary
    {
        get => _trialBalanceSummary;
        private set { if (_trialBalanceSummary == value) return; _trialBalanceSummary = value; OnPropertyChanged(); }
    }

    public string IncomeSummary
    {
        get => _incomeSummary;
        private set { if (_incomeSummary == value) return; _incomeSummary = value; OnPropertyChanged(); }
    }

    public string BalanceSummary
    {
        get => _balanceSummary;
        private set { if (_balanceSummary == value) return; _balanceSummary = value; OnPropertyChanged(); }
    }

    public ICommand LoadReportsCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReportsWorkspaceViewModel()
    {
        LoadReportsCommand = new AsyncRelayCommand(LoadReportsAsync, CanLoadReports);
    }

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            Companies.Clear();
            SelectedCompany = null;
            ClearRows();
            StatusMessage = "سجّل الدخول وفعّل المستأجر قبل عرض التقارير.";
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
        StatusMessage = "جارٍ تحميل الشركات والتقارير من API…";
        StatusIsError = false;
        try
        {
            var companies = await _apiClient.GetCompaniesAsync();
            var previousCompanyId = SelectedCompany?.Id;
            Companies.Clear();
            foreach (var company in companies)
                Companies.Add(company);

            SelectedCompany = (previousCompanyId.HasValue
                ? Companies.FirstOrDefault(x => x.Id == previousCompanyId.Value)
                : null) ?? Companies.FirstOrDefault();

            if (SelectedCompany is null)
            {
                ClearRows();
                StatusMessage = "لا توجد شركات ضمن هذا المستأجر؛ أنشئ شركة من مساحة المبيعات إذا كانت صلاحيتك تسمح.";
                StatusIsError = false;
                return;
            }

            await LoadReportsCoreAsync();
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء تحميل التقارير.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task LoadReportsAsync()
    {
        if (!CanLoadReports())
        {
            StatusMessage = "اختر شركة وأدخل نطاق تواريخ صحيحًا؛ يجب ألا يسبق تاريخ البداية تاريخ النهاية.";
            StatusIsError = true;
            return;
        }

        IsBusy = true;
        StatusMessage = "جارٍ حساب التقارير من القيود المرحلة…";
        StatusIsError = false;
        try
        {
            await LoadReportsCoreAsync();
        }
        catch (ErpApiException ex)
        {
            StatusMessage = ex.Message;
            StatusIsError = true;
        }
        catch (HttpRequestException)
        {
            StatusMessage = "تعذر الاتصال بالخادم أثناء تحديث التقارير.";
            StatusIsError = true;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanLoadReports() =>
        _apiClient is not null && SelectedCompany is not null &&
        FromDate.HasValue && ToDate.HasValue && AsOfDate.HasValue &&
        FromDate.Value.Date <= ToDate.Value.Date && !IsBusy;

    private async Task LoadReportsCoreAsync()
    {
        if (_apiClient is null || SelectedCompany is null ||
            !FromDate.HasValue || !ToDate.HasValue || !AsOfDate.HasValue)
            return;

        var companyId = SelectedCompany.Id;
        var fromDate = DateOnly.FromDateTime(FromDate.Value.Date);
        var toDate = DateOnly.FromDateTime(ToDate.Value.Date);
        var asOfDate = DateOnly.FromDateTime(AsOfDate.Value.Date);

        var trialTask = _apiClient.GetTrialBalanceAsync(companyId, fromDate, toDate);
        var incomeTask = _apiClient.GetIncomeStatementAsync(companyId, fromDate, toDate);
        var balanceTask = _apiClient.GetBalanceSheetAsync(companyId, asOfDate);
        await Task.WhenAll(trialTask, incomeTask, balanceTask);

        var trial = await trialTask;
        var income = await incomeTask;
        var balance = await balanceTask;

        TrialBalanceRows.Clear();
        foreach (var row in trial.Rows)
            TrialBalanceRows.Add(row);

        IncomeRows.Clear();
        foreach (var row in income.Revenue)
            IncomeRows.Add(new FinancialReportLine("إيراد", row.AccountCode, row.AccountNameAr, row.AccountNameEn, row.Amount));
        foreach (var row in income.Expenses)
            IncomeRows.Add(new FinancialReportLine("مصروف", row.AccountCode, row.AccountNameAr, row.AccountNameEn, row.Amount));

        BalanceRows.Clear();
        foreach (var row in balance.Assets)
            BalanceRows.Add(new FinancialReportLine("أصول", row.AccountCode, row.AccountNameAr, row.AccountNameEn, row.Amount));
        foreach (var row in balance.Liabilities)
            BalanceRows.Add(new FinancialReportLine("التزامات", row.AccountCode, row.AccountNameAr, row.AccountNameEn, row.Amount));
        foreach (var row in balance.Equity)
            BalanceRows.Add(new FinancialReportLine("حقوق ملكية", row.AccountCode, row.AccountNameAr, row.AccountNameEn, row.Amount));

        TrialBalanceSummary = $"حركة مدين {trial.TotalDebitMovement:N2} · حركة دائن {trial.TotalCreditMovement:N2} · رصيد مدين {trial.TotalDebitBalance:N2} · رصيد دائن {trial.TotalCreditBalance:N2} {SelectedCurrency}";
        IncomeSummary = $"الإيرادات {income.TotalRevenue:N2} · المصروفات {income.TotalExpenses:N2} · صافي الدخل {income.NetIncome:N2} {SelectedCurrency}";
        BalanceSummary = $"الأصول {balance.TotalAssets:N2} · الالتزامات {balance.TotalLiabilities:N2} · حقوق الملكية {balance.TotalEquity:N2} · الفرق {balance.Difference:N2} {SelectedCurrency}";

        StatusMessage = "تم تحميل التقارير من القيود بحالة POSTED فقط. راجع سياسة الإقفال قبل اعتماد النتائج رسميًا.";
        StatusIsError = balance.Difference != 0m;
    }

    private void ClearRows()
    {
        TrialBalanceRows.Clear();
        IncomeRows.Clear();
        BalanceRows.Clear();
        TrialBalanceSummary = "لم يتم تحميل ميزان المراجعة.";
        IncomeSummary = "لم يتم تحميل قائمة الدخل.";
        BalanceSummary = "لم يتم تحميل الميزانية.";
    }

    private void NotifyCommands() =>
        (LoadReportsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record FinancialReportLine(
    string Category, string AccountCode, string AccountNameAr, string AccountNameEn, decimal Amount);
