using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using IdentityModel.OidcClient;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BusinessERP.Desktop.Models;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, ModuleDefinition> Modules =
        new Dictionary<string, ModuleDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accounting"] = new("Accounting", "المحاسبة العامة", "دليل الحسابات والقيود ودفتر الأستاذ وميزان المراجعة.", "GL", "قيد التأسيس"),
            ["Sales"] = new("Sales", "المبيعات والعملاء", "إدارة العملاء ومسودات المبيعات الداخلية المحفوظة عبر API.", "AR", "مسودات أولية متصلة بالخادم"),
            ["Purchases"] = new("Purchases", "المشتريات والموردون", "أوامر الشراء وفواتير الموردين والاستحقاقات والمردودات.", "AP", "لاحقًا"),
            ["Inventory"] = new("Inventory", "المخزون والمستودعات", "الأصناف والوحدات والتحويلات والجرد وتقييم المخزون.", "ST", "لاحقًا"),
            ["CashBank"] = new("CashBank", "الصناديق والبنوك", "سندات القبض والصرف والتحويلات والتسويات البنكية.", "CB", "لاحقًا"),
            ["Tax"] = new("Tax", "الضرائب والفوترة", "إعدادات ضريبة القيمة المضافة والفوترة الإلكترونية والتكامل مع فاتورة.", "TX", "يتطلب تحققًا رسميًا"),
            ["Settings"] = new("Settings", "الإعدادات والصلاحيات", "إعدادات الشركة والمستخدمين والأدوار والفروع وسياسات الوصول.", "CFG", "لم تُربط بعد"),
        };

    private readonly string _apiBaseUrl;
    private readonly ApiHealthService _apiHealthService;
    private readonly OidcAuthenticationService _oidcAuthenticationService;
    private LoginResult? _loginResult;
    private HttpClient? _authenticatedHttpClient;
    private ErpApiClient? _erpApiClient;

    public MainWindow()
    {
        InitializeComponent();
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
            _loginResult = result;
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
            AuthStatusText.Text = $"تم تفعيل المستأجر · {tenantId.ToString("D")[..8]}";
        }
        catch (ErpApiException ex)
        {
            _erpApiClient = null;
            await SalesWorkspacePanel.SetApiClientAsync(null);
            AuthStatusText.Text = "تم رفض المستأجر أو صلاحيات المستخدم";
            if (showErrors)
                MessageBox.Show(ex.Message, "تفعيل المستأجر", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (HttpRequestException)
        {
            _erpApiClient = null;
            await SalesWorkspacePanel.SetApiClientAsync(null);
            AuthStatusText.Text = "تعذر الاتصال بـ API";
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
        _authenticatedHttpClient?.Dispose();
        _authenticatedHttpClient = null;
        _loginResult = null;
        TenantIdTextBox.IsEnabled = false;
        ApplyTenantButton.IsEnabled = false;
        LogoutButton.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Visible;
        AuthStatusText.Text = _oidcAuthenticationService.IsConfigured ? "تم إنهاء الجلسة المحلية" : "إعداد OIDC مطلوب";
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
            ApiLastChecked.Text = $"آخر فحص: {DateTimeOffset.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.GetCultureInfo("ar-SA"))}";
        }
        finally
        {
            CheckApiButton.IsEnabled = true;
            CheckApiButton.Content = "إعادة فحص الاتصال";
        }
    }

    private async void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        if (string.Equals(key, "Overview", StringComparison.OrdinalIgnoreCase))
        {
            PageTitle.Text = "نظرة عامة";
            DashboardPanel.Visibility = Visibility.Visible;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!Modules.TryGetValue(key, out var module))
            return;

        PageTitle.Text = module.Name;
        if (string.Equals(key, "Sales", StringComparison.OrdinalIgnoreCase))
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            SalesWorkspacePanel.Visibility = Visibility.Visible;
            if (_erpApiClient is null)
                await SalesWorkspacePanel.SetApiClientAsync(null);
            else
                await SalesWorkspacePanel.RefreshAsync();
            return;
        }

        SalesWorkspacePanel.Visibility = Visibility.Collapsed;
        ModulePageTitle.Text = module.Name;
        ModuleCodeText.Text = module.Code;
        ModulePageDescription.Text = module.Description;
        ModuleStatusText.Text = $"الحالة الحالية: {module.Status}. لم تُفعّل عمليات الحفظ أو الترحيل من هذه الشاشة.";
        DashboardPanel.Visibility = Visibility.Collapsed;
        ModulePlaceholderPanel.Visibility = Visibility.Visible;
    }
}
