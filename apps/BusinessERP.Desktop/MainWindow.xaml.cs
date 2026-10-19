using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using BusinessERP.Desktop.Models;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, ModuleDefinition> Modules =
        new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accounting"] = new("Accounting", "المحاسبة العامة", "دليل الحسابات والقيود ودفتر الأستاذ وميزان المراجعة.", "GL", "قيد التأسيس"),
            ["Sales"] = new("Sales", "المبيعات والعملاء", "العملاء ومسودات البيع وحساب VAT الداخلي وترحيل الأثر المحاسبي إلى الأستاذ العام؛ دون إصدار نظامي أو إرسال ZATCA.", "AR", "مسودات VAT وGL داخلية"),
            ["Purchases"] = new("Purchases", "المشتريات والموردون", "الموردون ومسودات فواتير الشراء وحساب VAT الداخلي وتقرير البنود المحسوبة؛ دون ترحيل GL أو اعتماد استرداد VAT.", "AP", "مسودات الموردين وتقرير VAT داخلي"),
            ["Inventory"] = new("Inventory", "المخزون والمستودعات", "الأصناف والوحدات والتحويلات والجرد وتقييم المخزون.", "ST", "لاحقًا"),
            ["CashBank"] = new("CashBank", "الصناديق والبنوك", "سندات القبض والصرف والتحويلات والتسويات البنكية.", "CB", "لاحقًا"),
            ["Tax"] = new("Tax", "الضرائب والفوترة", "إعدادات ضريبة القيمة المضافة والفوترة الإلكترونية والتكامل مع فاتورة.", "TX", "يتطلب تحققًا رسميًا"),
            ["Settings"] = new("Settings", "الإعدادات والصلاحيات", "إعدادات الشركة والمستخدمين والأدوار والفروع وسياسات الوصول.", "CFG", "لم تُربط بعد"),
            ["Reports"] = new("Reports", "التقارير المالية", "تقارير الأستاذ العام وميزان المراجعة وقائمة الدخل والميزانية من الحركات المرحلة.", "RPT", "واجهات API موجودة؛ شاشة التقارير قيد الربط"),
        };

    private readonly string _apiBaseUrl;
    private readonly ApiHealthService _apiHealthService;
    private readonly OidcAuthenticationService _oidcAuthenticationService;
    private HttpClient? _authenticatedHttpClient;
    private ErpApiClient? _erpApiClient;
    private bool _ribbonExpanded = true;
    private bool _navigationExpanded = true;

    public MainWindow()
    {
        InitializeComponent();
        SalesWorkspacePanel.WorkspaceContextChanged += (_, _) => UpdateWorkspaceContext();
        ReportsWorkspacePanel.WorkspaceContextChanged += (_, _) => UpdateWorkspaceContext();
        PurchasesWorkspacePanel.WorkspaceContextChanged += (_, _) => UpdateWorkspaceContext();
        _apiBaseUrl = LoadApiBaseUrl();
        _apiHealthService = new ApiHealthService(_apiBaseUrl);
        _oidcAuthenticationService = new OidcAuthenticationService(
            ReadSetting("Oidc", "Authority"),
            ReadSetting("Oidc", "ClientId"),
            ReadSetting("Oidc", "Scope"),
            ReadSetting("Oidc", "RedirectUri") ?? "http://127.0.0.1:7890/callback/");
        AuthStatusText.Text = _oidcAuthenticationService.IsConfigured ? "غير مسجل الدخول" : "إعداد OIDC مطلوب";
        LoginButton.ToolTip = _oidcAuthenticationService.ConfigurationError;
        ApiAddressText.Text = $"API: {_apiBaseUrl}";
        UpdateWorkspaceContext();
    }

    private static string LoadApiBaseUrl()
    {
        const string fallback = "http://localhost:5080";
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        try
        {
            if (!File.Exists(settingsPath))
                return fallback;

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("ApiBaseUrl", out var property) &&
                property.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                return uri.ToString().TrimEnd('/');
            }
        }
        catch (JsonException)
        {
            // Use a safe local default if a local configuration file is malformed.
        }
        catch (IOException)
        {
            // A missing or inaccessible local settings file must not crash the desktop shell.
        }

        return fallback;
    }

    private static string? ReadSetting(string section, string key)
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (!File.Exists(settingsPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.TryGetProperty(section, out var sectionElement) &&
                   sectionElement.ValueKind == JsonValueKind.Object &&
                   sectionElement.TryGetProperty(key, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_oidcAuthenticationService.IsConfigured)
        {
            MessageBox.Show(_oidcAuthenticationService.ConfigurationError ?? "لم يتم إعداد موفر الهوية.",
                "إعداد تسجيل الدخول", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        LoginButton.IsEnabled = false;
        AuthStatusText.Text = "جارٍ فتح موفر الهوية…";
        try
        {
            var result = await _oidcAuthenticationService.LoginAsync();
            if (result.IsError || string.IsNullOrWhiteSpace(result.AccessToken))
            {
                AuthStatusText.Text = "تعذر تسجيل الدخول";
                MessageBox.Show("لم تكتمل المصادقة. تحقق من إعداد موفر الهوية وإعداد تطبيق العميل. رمز الخطأ: " +
                                (string.IsNullOrWhiteSpace(result.Error) ? "غير متاح" : result.Error),
                    "تسجيل الدخول", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _authenticatedHttpClient?.Dispose();
            HttpMessageHandler handler = result.RefreshTokenHandler ??
                new StaticAccessTokenHandler(result.AccessToken);
            _authenticatedHttpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(_apiBaseUrl.TrimEnd('/') + "/")
            };

            LoginButton.Visibility = Visibility.Collapsed;
            LogoutButton.Visibility = Visibility.Visible;
            TenantIdTextBox.IsEnabled = true;
            ApplyTenantButton.IsEnabled = true;

            var displayName = result.User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = result.User.FindFirst("name")?.Value ??
                              result.User.FindFirst("preferred_username")?.Value ??
                              result.User.FindFirst("sub")?.Value ?? "مستخدم";
            AuthStatusText.Text = $"تم الدخول: {displayName}";
            RibbonStatusText.Text = "تم تسجيل الدخول · بانتظار تفعيل عضوية المستأجر";

            if (Guid.TryParse(TenantIdTextBox.Text.Trim(), out var tenantId) && tenantId != Guid.Empty)
                await ActivateTenantAsync(tenantId, showErrors: false);
            else
                AuthStatusText.Text = $"تم الدخول: {displayName} · أدخل معرّف المستأجر";
        }
        catch (Exception)
        {
            AuthStatusText.Text = "تعذر تسجيل الدخول";
            MessageBox.Show("تعذر إكمال تسجيل الدخول. تحقق من الاتصال وإعدادات OIDC وعنوان العودة المحلي.",
                "تسجيل الدخول", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void ApplyTenantButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authenticatedHttpClient is null)
        {
            MessageBox.Show("سجّل الدخول أولًا.", "تفعيل المستأجر", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Guid.TryParse(TenantIdTextBox.Text.Trim(), out var tenantId) || tenantId == Guid.Empty)
        {
            MessageBox.Show("أدخل معرّف مستأجر صحيحًا بصيغة GUID.", "تفعيل المستأجر", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyTenantButton.IsEnabled = false;
        try { await ActivateTenantAsync(tenantId, showErrors: true); }
        finally { ApplyTenantButton.IsEnabled = _authenticatedHttpClient is not null; }
    }

    private async Task ActivateTenantAsync(Guid tenantId, bool showErrors)
    {
        if (_authenticatedHttpClient is null) return;

        var candidateClient = new ErpApiClient(_authenticatedHttpClient, tenantId);
        try
        {
            // This real API request validates the authenticated user's membership server-side.
            await candidateClient.GetCompaniesAsync();
            _erpApiClient = candidateClient;
            await SalesWorkspacePanel.SetApiClientAsync(candidateClient);
            if (PurchasesWorkspacePanel.Visibility == Visibility.Visible)
                await PurchasesWorkspacePanel.SetApiClientAsync(candidateClient);
            if (ReportsWorkspacePanel.Visibility == Visibility.Visible)
                await ReportsWorkspacePanel.SetApiClientAsync(candidateClient);
            AuthStatusText.Text = $"تم تفعيل المستأجر · {tenantId.ToString("D")[..8]}";
            UpdateRibbonContext();
            RibbonStatusText.Text = $"API متصل · المستأجر {tenantId.ToString("D")[..8]}";
        }
        catch (ErpApiException ex)
        {
            _erpApiClient = null;
            await SalesWorkspacePanel.SetApiClientAsync(null);
            await ReportsWorkspacePanel.SetApiClientAsync(null);
            await PurchasesWorkspacePanel.SetApiClientAsync(null);
            UpdateWorkspaceContext();
            AuthStatusText.Text = "تم رفض المستأجر أو صلاحيات المستخدم";
            RibbonStatusText.Text = "تم رفض عضوية المستأجر أو صلاحيات المستخدم";
            if (showErrors)
                MessageBox.Show(ex.Message, "تفعيل المستأجر", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException)
        {
            _erpApiClient = null;
            await SalesWorkspacePanel.SetApiClientAsync(null);
            await ReportsWorkspacePanel.SetApiClientAsync(null);
            await PurchasesWorkspacePanel.SetApiClientAsync(null);
            UpdateWorkspaceContext();
            AuthStatusText.Text = "تعذر الاتصال بـ API";
            RibbonStatusText.Text = "تعذر الاتصال بـ API";
            if (showErrors)
                MessageBox.Show("تعذر الاتصال بالخادم. تحقق من اتصال الشبكة وعنوان API.", "تفعيل المستأجر",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        // Local logout clears the in-memory token handler. RP-initiated provider logout is not
        // claimed here and can be added once provider end-session policies are configured.
        _erpApiClient = null;
        await SalesWorkspacePanel.SetApiClientAsync(null);
        await ReportsWorkspacePanel.SetApiClientAsync(null);
        await PurchasesWorkspacePanel.SetApiClientAsync(null);
        UpdateWorkspaceContext();
        _authenticatedHttpClient?.Dispose();
        _authenticatedHttpClient = null;
        UpdateRibbonContext();
        TenantIdTextBox.IsEnabled = false;
        ApplyTenantButton.IsEnabled = false;
        LogoutButton.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Visible;
        AuthStatusText.Text = _oidcAuthenticationService.IsConfigured ? "تم إنهاء الجلسة المحلية" : "إعداد OIDC مطلوب";
        RibbonStatusText.Text = "غير متصل · تم إنهاء الجلسة المحلية";
    }

    private sealed class StaticAccessTokenHandler(string accessToken) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckApiHealthAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _authenticatedHttpClient?.Dispose();
        _apiHealthService.Dispose();
    }

    private async void CheckApiButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckApiHealthAsync();
    }

    private async Task CheckApiHealthAsync()
    {
        CheckApiButton.IsEnabled = false;
        CheckApiButton.Content = "جارٍ الفحص…";
        ApiStatusTitle.Text = "جارٍ فحص الاتصال بالخادم…";
        ApiStatusDot.Background = new SolidColorBrush(Color.FromRgb(217, 155, 36));

        try
        {
            var result = await _apiHealthService.CheckAsync();
            ApiStatusTitle.Text = result.Message;
            ApiStatusDetail.Text = result.IsOnline
                ? $"استجابة فحص الصحة: {result.Detail}. هذا لا يؤكد جاهزية قاعدة البيانات أو اكتمال وظائف النظام."
                : $"تعذر فحص الخادم ({result.Detail}). تأكد من تشغيل API وإعداد عنوانه في appsettings.json.";
            ApiStatusDot.Background = result.IsOnline
                ? new SolidColorBrush(Color.FromRgb(32, 160, 104))
                : new SolidColorBrush(Color.FromRgb(208, 74, 74));
            if (_erpApiClient is null)
                RibbonStatusText.Text = result.IsOnline ? "API متصل · سجّل الدخول لتفعيل المستأجر" : "API غير متصل";
            else if (!result.IsOnline)
                RibbonStatusText.Text = "API غير متصل · قد لا تتوفر عمليات الحفظ";
            ApiLastChecked.Text = $"آخر فحص: {DateTimeOffset.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.GetCultureInfo("ar-SA"))}";
        }
        finally
        {
            CheckApiButton.IsEnabled = true;
            CheckApiButton.Content = "إعادة فحص الاتصال";
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.F:
                e.Handled = true;
                GeneralSearchBox.Focus();
                GeneralSearchBox.SelectAll();
                break;
            case Key.N:
                e.Handled = true;
                await NavigateToKeyAsync("Sales");
                GeneralSearchBox.Clear();
                SalesWorkspacePanel.BeginNewDraft();
                RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
                break;
            case Key.R:
                e.Handled = true;
                await RibbonRefreshAsync();
                break;
            case Key.S when SalesWorkspacePanel.Visibility == Visibility.Visible && _erpApiClient is not null:
                e.Handled = true;
                await SalesWorkspacePanel.SaveCurrentDraftAsync();
                RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
                break;
        }
    }

    private async Task RibbonRefreshAsync()
    {
        if (SalesWorkspacePanel.Visibility == Visibility.Visible && _erpApiClient is not null)
        {
            await SalesWorkspacePanel.RefreshAsync();
            RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
        }
        if (PurchasesWorkspacePanel.Visibility == Visibility.Visible && _erpApiClient is not null)
        {
            await PurchasesWorkspacePanel.RefreshAsync();
            RibbonStatusText.Text = PurchasesWorkspacePanel.WorkspaceStatusMessage;
        }
        if (ReportsWorkspacePanel.Visibility == Visibility.Visible && _erpApiClient is not null)
        {
            await ReportsWorkspacePanel.RefreshAsync();
            RibbonStatusText.Text = ReportsWorkspacePanel.WorkspaceStatusMessage;
        }
        await CheckApiHealthAsync();
    }

    private void ToggleRibbon_Click(object sender, RoutedEventArgs e)
    {
        _ribbonExpanded = !_ribbonExpanded;
        RibbonActionsPanel.Visibility = _ribbonExpanded ? Visibility.Visible : Visibility.Collapsed;
        RibbonToggleButton.Content = _ribbonExpanded ? "تصغير الشريط ▴" : "توسيع الشريط ▾";
    }

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        _navigationExpanded = !_navigationExpanded;
        NavigationColumn.Width = _navigationExpanded ? new GridLength(264) : new GridLength(0);
    }

    private async void RibbonNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
            await NavigateToKeyAsync(key);
    }

    private async void RibbonNew_Click(object sender, RoutedEventArgs e)
    {
        if (PurchasesWorkspacePanel.Visibility == Visibility.Visible)
        {
            PurchasesWorkspacePanel.BeginNewPurchaseDraft();
            RibbonStatusText.Text = PurchasesWorkspacePanel.WorkspaceStatusMessage;
            return;
        }
        await NavigateToKeyAsync("Sales");
        GeneralSearchBox.Clear();
        SalesWorkspacePanel.BeginNewDraft();
        RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
    }

    private async void RibbonRefresh_Click(object sender, RoutedEventArgs e) =>
        await RibbonRefreshAsync();

    private async void RibbonSave_Click(object sender, RoutedEventArgs e)
    {
        if (_erpApiClient is null || SalesWorkspacePanel.Visibility != Visibility.Visible)
        {
            RibbonStatusText.Text = "افتح مساحة المبيعات وفعّل المستأجر قبل حفظ مسودة.";
            return;
        }
        await SalesWorkspacePanel.SaveCurrentDraftAsync();
        RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
    }

    private async void RibbonPostSalesDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_erpApiClient is null || SalesWorkspacePanel.Visibility != Visibility.Visible)
        {
            RibbonStatusText.Text = "افتح مساحة المبيعات وفعّل المستأجر قبل الترحيل.";
            return;
        }
        await SalesWorkspacePanel.PostSelectedDraftToLedgerAsync();
        RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
    }

    private void RibbonPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_erpApiClient is null || ReportsWorkspacePanel.Visibility != Visibility.Visible)
        {
            RibbonStatusText.Text = "افتح التقارير المالية وفعّل المستأجر قبل الطباعة.";
            return;
        }

        var message = ReportsWorkspacePanel.PrintCurrentReport();
        if (!string.IsNullOrWhiteSpace(message))
            RibbonStatusText.Text = message;
    }

    private void RibbonExportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (_erpApiClient is null || ReportsWorkspacePanel.Visibility != Visibility.Visible)
        {
            RibbonStatusText.Text = "افتح التقارير المالية وفعّل المستأجر قبل التصدير إلى Excel.";
            return;
        }

        var message = ReportsWorkspacePanel.ExportCurrentReportToExcel();
        if (!string.IsNullOrWhiteSpace(message))
            RibbonStatusText.Text = message;
    }

    private async void GeneralSearch_Click(object sender, RoutedEventArgs e) =>
        await RunGeneralSearchAsync();

    private async void GeneralSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunGeneralSearchAsync();
    }

    private async Task RunGeneralSearchAsync()
    {
        if (_erpApiClient is null)
        {
            RibbonStatusText.Text = "سجّل الدخول وفعّل المستأجر قبل البحث في البيانات.";
            return;
        }

        var search = GeneralSearchBox.Text?.Trim() ?? "";
        var salesMatch = SalesWorkspacePanel.SearchLoadedData(search);
        if (salesMatch)
        {
            await NavigateToKeyAsync("Sales");
            SalesWorkspacePanel.SearchLoadedData(search);
        }
        RibbonStatusText.Text = SalesWorkspacePanel.WorkspaceStatusMessage;
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;
        await NavigateToKeyAsync(key);
    }

    private void UpdateWorkspaceContext()
    {
        if (SalesWorkspacePanel.Visibility == Visibility.Visible)
            WorkspaceContextText.Text = SalesWorkspacePanel.WorkspaceContextSummary;
        else if (PurchasesWorkspacePanel.Visibility == Visibility.Visible)
            WorkspaceContextText.Text = PurchasesWorkspacePanel.WorkspaceContextSummary;
        else if (ReportsWorkspacePanel.Visibility == Visibility.Visible)
            WorkspaceContextText.Text = ReportsWorkspacePanel.WorkspaceContextSummary;
        else if (_erpApiClient is not null)
            WorkspaceContextText.Text = "المستأجر مفعل · لم يتم اختيار شاشة شركة/فترة";
        else
            WorkspaceContextText.Text = "الشركة: غير محددة · الفترة المالية: غير محددة";
    }

    private void UpdateWorkspaceTabs(string key)
    {
        SetWorkspaceTabStyle(OverviewWorkspaceTab, string.Equals(key, "Overview", StringComparison.OrdinalIgnoreCase));
        SetWorkspaceTabStyle(SalesWorkspaceTab, string.Equals(key, "Sales", StringComparison.OrdinalIgnoreCase));
        SetWorkspaceTabStyle(PurchasesWorkspaceTab, string.Equals(key, "Purchases", StringComparison.OrdinalIgnoreCase));
        SetWorkspaceTabStyle(ReportsWorkspaceTab, string.Equals(key, "Reports", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetWorkspaceTabStyle(Button tab, bool selected)
    {
        tab.Background = selected
            ? new SolidColorBrush(Color.FromRgb(220, 231, 255))
            : Brushes.Transparent;
        tab.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(53, 106, 230))
            : Brushes.Transparent;
        tab.Foreground = selected
            ? new SolidColorBrush(Color.FromRgb(35, 75, 164))
            : new SolidColorBrush(Color.FromRgb(52, 64, 84));
    }

    private void UpdateRibbonContext()
    {
        var salesIsActive = _erpApiClient is not null && SalesWorkspacePanel.Visibility == Visibility.Visible;
        RibbonSaveButton.IsEnabled = salesIsActive;
        RibbonPostButton.IsEnabled = salesIsActive;
        var reportsAreActive = _erpApiClient is not null &&
            ReportsWorkspacePanel.Visibility == Visibility.Visible;
        RibbonExportExcelButton.IsEnabled = reportsAreActive;
        RibbonPrintButton.IsEnabled = reportsAreActive;
    }

    private async Task NavigateToKeyAsync(string key)
    {
        UpdateWorkspaceTabs(key);
        if (string.Equals(key, "Overview", StringComparison.OrdinalIgnoreCase))
        {
            PageTitle.Text = "نظرة عامة";
            DashboardPanel.Visibility = Visibility.Visible;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Collapsed;
            PurchasesWorkspacePanel.Visibility = Visibility.Collapsed;
            ReportsWorkspacePanel.Visibility = Visibility.Collapsed;
            UpdateWorkspaceContext();
            UpdateRibbonContext();
            return;
        }

        if (!Modules.TryGetValue(key, out var module))
            return;

        PageTitle.Text = module.Name;
        if (string.Equals(key, "Reports", StringComparison.OrdinalIgnoreCase))
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Collapsed;
            PurchasesWorkspacePanel.Visibility = Visibility.Collapsed;
            ReportsWorkspacePanel.Visibility = Visibility.Visible;
            UpdateWorkspaceContext();
            if (_erpApiClient is null)
                await ReportsWorkspacePanel.SetApiClientAsync(null);
            else
                await ReportsWorkspacePanel.SetApiClientAsync(_erpApiClient);
            RibbonStatusText.Text = ReportsWorkspacePanel.WorkspaceStatusMessage;
            UpdateWorkspaceContext();
            UpdateRibbonContext();
            return;
        }

        if (string.Equals(key, "Sales", StringComparison.OrdinalIgnoreCase))
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            ReportsWorkspacePanel.Visibility = Visibility.Collapsed;
            PurchasesWorkspacePanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Visible;
            if (_erpApiClient is null)
                await SalesWorkspacePanel.SetApiClientAsync(null);
            else
                await SalesWorkspacePanel.RefreshAsync();
            UpdateWorkspaceContext();
            UpdateRibbonContext();
            return;
        }

        if (string.Equals(key, "Purchases", StringComparison.OrdinalIgnoreCase))
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Collapsed;
            ReportsWorkspacePanel.Visibility = Visibility.Collapsed;
            PurchasesWorkspacePanel.Visibility = Visibility.Visible;
            if (_erpApiClient is null)
                await PurchasesWorkspacePanel.SetApiClientAsync(null);
            else
                await PurchasesWorkspacePanel.SetApiClientAsync(_erpApiClient);
            RibbonStatusText.Text = PurchasesWorkspacePanel.WorkspaceStatusMessage;
            UpdateWorkspaceContext();
            UpdateRibbonContext();
            return;
        }

        SalesWorkspacePanel.Visibility = Visibility.Collapsed;
        PurchasesWorkspacePanel.Visibility = Visibility.Collapsed;
        ReportsWorkspacePanel.Visibility = Visibility.Collapsed;
        ModulePageTitle.Text = module.Name;
        ModuleCodeText.Text = module.Code;
        ModulePageDescription.Text = module.Description;
        ModuleStatusText.Text = $"الحالة الحالية: {module.Status}. لم تُفعّل عمليات الحفظ أو الترحيل من هذه الشاشة.";
        DashboardPanel.Visibility = Visibility.Collapsed;
        ModulePlaceholderPanel.Visibility = Visibility.Visible;
        UpdateWorkspaceContext();
        UpdateRibbonContext();
    }
}
