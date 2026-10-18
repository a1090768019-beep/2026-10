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
            ["Sales"] = new("Sales", "المبيعات والعملاء", "عروض الأسعار وفواتير المبيعات والتحصيل وأعمار ديون العملاء.", "AR", "لاحقًا"),
            ["Purchases"] = new("Purchases", "المشتريات والموردون", "أوامر الشراء وفواتير الموردين والاستحقاقات والمردودات.", "AP", "لاحقًا"),
            ["Inventory"] = new("Inventory", "المخزون والمستودعات", "الأصناف والوحدات والتحويلات والجرد وتقييم المخزون.", "ST", "لاحقًا"),
            ["CashBank"] = new("CashBank", "الصناديق والبنوك", "سندات القبض والصرف والتحويلات والتسويات البنكية.", "CB", "لاحقًا"),
            ["Tax"] = new("Tax", "الضرائب والفوترة", "إعدادات ضريبة القيمة المضافة والفوترة الإلكترونية والتكامل مع فاتورة.", "TX", "يتطلب تحققًا رسميًا"),
            ["Settings"] = new("Settings", "الإعدادات والصلاحيات", "إعدادات الشركة والمستخدمين والأدوار والفروع وسياسات الوصول.", "CFG", "لم تُربط بعد"),
        };

    private readonly string _apiBaseUrl;
    private readonly ApiHealthService _apiHealthService;

    public MainWindow()
    {
        InitializeComponent();
        _apiBaseUrl = LoadApiBaseUrl();
        _apiHealthService = new ApiHealthService(_apiBaseUrl);
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
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckApiHealthAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
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

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        if (string.Equals(key, "Overview", StringComparison.OrdinalIgnoreCase))
        {
            PageTitle.Text = "نظرة عامة";
            DashboardPanel.Visibility = Visibility.Visible;
            ModulePlaceholderPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (!Modules.TryGetValue(key, out var module))
            return;

        PageTitle.Text = module.Name;
        ModulePageTitle.Text = module.Name;
        ModuleCodeText.Text = module.Code;
        ModulePageDescription.Text = module.Description;
        ModuleStatusText.Text = $"الحالة الحالية: {module.Status}. لم تُفعّل عمليات الحفظ أو الترحيل من هذه الشاشة.";
        DashboardPanel.Visibility = Visibility.Collapsed;
        ModulePlaceholderPanel.Visibility = Visibility.Visible;
    }
}
