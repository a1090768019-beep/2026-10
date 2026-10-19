# Business ERP — C# Accounting Core

مشروع ERP SaaS قيد التطوير للسوق السعودي، مبني من WPF وASP.NET Core وPostgreSQL متعدد المستأجرين. أحدث CI يبني API وWPF ويختبر مسارات محاسبية على PostgreSQL، لكن **المنتج غير جاهز للبيع**: لا توجد فاتورة ضريبية/نهائية متوافقة مع ZATCA، ولا تكامل مصرفي أو بوابة دفع SaaS أو تشغيل إنتاجي مُثبت.

## ما هو موجود في الشفرة الآن

- تطبيق Windows أصلي بـ WPF/XAML/C#، مع تدفق OIDC عبر متصفح النظام، وشاشات عربية RTL للمبيعات والمشتريات والمخزون والتقارير والتسويات، واتصال فعلي بـ ASP.NET Core API.
- API بـ ASP.NET Core على .NET 10 وقاعدة PostgreSQL متعددة المستأجرين. يتحقق الخادم من هوية OIDC وربط claim `sub` بمستخدم محلي غير معطّل وعضوية مستأجر نشطة قبل السماح بمسارات العمل.
- نواة المحاسبة: دليل الحسابات، السنوات والفترات المالية، قيود يومية ومسارات الاعتماد والترحيل والإلغاء والعكس، الأستاذ العام، ميزان المراجعة، وقائمة دخل وميزانية أوليتان مبنيتان على القيود المرحلة.
- المبيعات: العملاء ومسودات فواتير داخلية، حفظ تصنيف VAT وحسابها داخل المسودة، وترحيل قيد داخلي متوازن مرتبط بالمصدر. هذه ليست فاتورة ضريبية قانونية ولا يوجد إرسال إلى ZATCA/FATOORA.
- المشتريات: الموردون ومسودات فواتير شراء، تصنيف VAT وتقارير تفاصيل البنود وترحيل GL مزدوج مرتبط بالمصدر. VAT المشتريات لا تثبت صلاحية مستند المورد أو قابلية استرداد ضريبة المدخلات.
- المخزون: الأصناف والمستودعات، الاستلام والصرف والتحويل وتتبع الأرقام التسلسلية وتقييم مخزون تاريخي، وربط بعض ترحيلات مستندات الشراء/البيع بحركات المخزون وتكلفة البضاعة المباعة. الجرد الفعلي والتسويات والمرتجعات والمطابقة الكاملة لا تزال غير مكتملة.
- النقدية والتسويات: سندات قبض وصرف داخلية وتخصيصها على الفواتير المرحلة وترحيلها إلى GL؛ لا يوجد تكامل مصرفي أو مطابقة كشف حساب آلية.
- تقارير أعمار الذمم المدينة والدائنة حتى تاريخ محدد؛ تحتاج مصالحة مستقلة مع الأستاذ العام قبل الاستخدام الرسمي.
- عزل قاعدة البيانات عبر PostgreSQL RLS، وقيود للتوازن وعدم تعديل القيود المرحلة، وسجل تدقيق append-only، وقيود idempotency وأقفال معاملات ضد بعض السباقات.
- اشتراكات SaaS: يمنع API العمل عند غياب اشتراك ACTIVE/TRIALING ساري، ويُفرض حد الشركات، ويُفرض حد القيود الشهري على مستوى مشغّل PostgreSQL، كما تُفرض حصة المستخدمين النشطين عند إسناد/إعادة تفعيل عضوية موجودة. توجد واجهات OWNER/ADMIN لإدارة عضويات المستخدمين المجهزين مسبقًا، لكنها لا تنشئ حسابات لدى موفر الهوية ولا تنفذ دعوات موثقة أو onboarding ذاتيًا. لا يوجد checkout أو تحصيل أو webhooks أو تجديد اشتراك.
- مسار CI يبني API وWPF، ويطبّق ترحيلات SQL على PostgreSQL اختباري، ويشغّل اختبارات تكامل ومحاسبة، ويبني صورة Docker ويتحقق من ملف Compose، وينشر حزمة Windows ذاتية التشغيل كأثر CI.

**قيود تمنع البيع حاليًا:** لا توجد فاتورة ضريبية نهائية أو تكامل ZATCA/FATOORA مثبت، أو بوابة دفع/تجديد اشتراك، أو موفر OIDC حي مهيأ، أو نشر إنتاجي مع backup/restore مجرّب، ولا مراجعة محاسبية/ضريبية أو اختبار اختراق مستقل. المراجعة الكاملة موجودة في `docs/RELEASE-CHECKLIST.md` و`docs/KNOWN-LIMITATIONS.md`.

## متطلبات البناء

- Windows 10/11 لتطبيق WPF.
- **Visual Studio 2026 18.0 أو أحدث** مع Workloads: `.NET desktop development` و`ASP.NET and web development`؛ أو .NET 10 SDK للبناء من الطرفية. بحسب جدول Microsoft، يتطلب تطوير مشاريع تستهدف .NET 10 بواسطة Visual Studio إصدار 2026 18.0 أو أحدث. [مرجع Microsoft](https://learn.microsoft.com/en-us/dotnet/core/install/windows?tabs=net70).
- PostgreSQL (اختبر إصدار قاعدة البيانات المعتمد على بيئة منفصلة قبل الإنتاج).
- موفر هوية OIDC متوافق مع JWT، مع `Authority` و`Audience` معروفين، وسجلات مستخدمين/عضويات مهيأة.
- Node.js 22 أو أحدث لتشغيل فحوص العقود.

## إعداد قاعدة البيانات

1. أنشئ قاعدة بيانات فارغة ودورًا منفصلًا لترحيلات المخطط ودور تشغيل للتطبيق. **لا** تجعل دور التطبيق `SUPERUSER` أو `BYPASSRLS` أو مالك الجداول.
2. `MIGRATIONS_DATABASE_URL` يجب أن يكون بصيغة اتصال PostgreSQL/libpq أو URI، وليس صيغة إعدادات .NET التي تستخدم فاصلة منقوطة. طبّق جميع ملفات الترحيل بالتسلسل على قاعدة جديدة فقط:

```powershell
$env:MIGRATIONS_DATABASE_URL = "postgresql://erp_migrator:ENCODED_PASSWORD@localhost:5432/business_erp"
Get-ChildItem "database/migrations/*.sql" | Sort-Object Name | ForEach-Object {
  Write-Host "Applying $($_.Name)"
  & psql "$env:MIGRATIONS_DATABASE_URL" -v ON_ERROR_STOP=1 -f $_.FullName
  if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($_.Name)" }
}
```

استبدل `ENCODED_PASSWORD` بكلمة مرور URI-encoded ولا تضعها في Git. هذه الملفات ليست مصممة لإعادة التطبيق على قاعدة سبق ترحيلها؛ افشل عند أول خطأ ولا تتابع إلى الملفات التالية. عند الترقية من إصدار موجود، استخدم مسار ترقية معتمدًا ومختبرًا بدل إعادة تشغيل الحلقة.

3. امنح دور التشغيل الصلاحيات اللازمة فقط، واضبط صلاحيات `audit_logs` بحيث لا يُسمح بالتعديل أو الحذف، واترك سجل التدقيق append-only. راجع `database/README.md`.
4. جهّز أول مستأجر ومستخدم وعضوية من خلال إجراء إداري موثوق، لا من العميل. يوجد نموذج SQL في `database/seeds/first-tenant-provisioning-template.sql`؛ استبدل بياناته بقيم صحيحة من موفر الهوية قبل تشغيله، ولا تستخدم القيم الوهمية.

## إعداد وتشغيل API محليًا

من PowerShell في جذر المستودع:

```powershell
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=business_erp;Username=business_erp_app;Password=ضع_كلمة_مرور_محلية"
$env:Authentication__Authority = "https://عنوان-موفر-الهوية-المعتمد/"
$env:Authentication__Audience = "business-erp-api"

dotnet restore src/server/BusinessERP.Api/BusinessERP.Api.csproj
dotnet test tests/BusinessERP.Domain.Tests/BusinessERP.Domain.Tests.csproj --configuration Release
dotnet run --project src/server/BusinessERP.Api/BusinessERP.Api.csproj --urls http://localhost:5080
```

للوصول إلى بيانات الأعمال يجب إرسال رمز وصول صالح في `Authorization: Bearer ...`، وإرسال `X-Tenant-Id: <tenant-guid>` لكل طلب. هذا الرأس يحدد المستأجر المطلوب فحسب؛ الخادم يتحقق من الهوية والعضوية والدور في قاعدة البيانات قبل السماح بالوصول. لا توجد مصادقة تجريبية أو تجاوز محلي مفعّل افتراضيًا.

- `/health/live` يتحقق من أن العملية حية.
- `/health/ready` يتحقق من إمكانية الوصول إلى PostgreSQL.
- مستند OpenAPI متاح في بيئة Development.

## إعداد تسجيل الدخول المكتبي (OIDC)

تدفق WPF يستخدم عميل OIDC عامًا عبر متصفح النظام وcallback محلي، ولا يتضمن client secret. قبل تسجيل الدخول يجب تهيئة موفر OIDC وتسجيل تطبيق Native/Public Client:

1. في `apps/BusinessERP.Desktop/appsettings.json` اضبط `Oidc:Authority` و`Oidc:ClientId` و`Oidc:Scope` بحسب موفر الهوية. سجّل عنوان العودة التالي بالضبط لدى الموفر: `http://127.0.0.1:7890/callback/`.
2. في API اضبط `Authentication:Authority` و`Authentication:Audience` على الجهة والجمهور اللذين يصدران Access Token للـ API. لا تضع secret أو كلمة مرور داخل تطبيق WPF أو Git.
3. هيّئ صف `users` بحيث يطابق `oidc_subject` قيمة claim `sub` الصادرة للمستخدم، ثم أضف `tenant_memberships` بحالة `ACTIVE` ودور معتمد. لا يوجد self-service provisioning.
4. شغّل API عبر HTTPS في البيئات غير المحلية، وغيّر `ApiBaseUrl` إلى عنوان HTTPS. يسمح تطبيق WPF بـ HTTP للـ loopback المحلي فقط.
5. بعد الدخول أدخل GUID المستأجر الذي خصّصه المشغل؛ الخادم يعيد فحص العضوية من قاعدة البيانات، ورأس `X-Tenant-Id` وحده لا يمنح صلاحية.

إنهاء الجلسة في هذه النسخة يمسح الجلسة المحلية والرموز الموجودة في ذاكرة التطبيق؛ لا يوجد بعد تدفق end-session مع موفر الهوية. لم يتم اختبار الدخول ضد موفر خارجي حي لأن بيانات موفر الهوية لم تُحدد بعد.


## مسارات المحاسبة الأساسية

كل المسارات تبدأ بـ `/api/v1`:

| الطريقة | المسار | الوظيفة |
|---|---|---|
| GET / POST | `/companies` | عرض الشركات وإنشاء شركة |
| GET / POST | `/companies/{companyId}/accounts` | دليل الحسابات |
| GET / POST | `/companies/{companyId}/fiscal-years` | السنوات المالية؛ إنشاء الفترات الشهرية تلقائيًا |
| GET | `/companies/{companyId}/fiscal-periods` | عرض الفترات |
| GET / POST | `/companies/{companyId}/journal-entries` | عرض القيود وإنشاء قيد مسودة |
| GET | `/journal-entries/{entryId}` | تفاصيل القيد وسطره |
| POST | `/journal-entries/{entryId}/approve` | اعتماد المسودة |
| POST | `/journal-entries/{entryId}/post` | ترحيل المعتمد |
| POST | `/journal-entries/{entryId}/cancel` | إلغاء مسودة أو قيد معتمد غير مرحل |
| POST | `/journal-entries/{entryId}/reverse` | إنشاء قيد عكسي وترحيله ضمن معاملة واحدة |
| GET | `/companies/{companyId}/reports/trial-balance?fromDate=2026-01-01&toDate=2026-12-31` | ميزان المراجعة: الحركات للفترة والأرصدة حتى تاريخ النهاية |
| GET | `/companies/{companyId}/reports/income-statement?fromDate=YYYY-MM-DD&toDate=YYYY-MM-DD` | قائمة دخل أولية من قيود الإيراد والمصروف المرحلة فقط |
| GET | `/companies/{companyId}/reports/balance-sheet?asOfDate=YYYY-MM-DD` | ميزانية أولية من الأرصدة المرحلة حتى التاريخ، مع الأرباح غير المقفلة ضمن حقوق الملكية |
| GET | `/subscription-plans` | كتالوج الخطط النشطة الذي يضبطه مشغل المنصة |
| GET | `/subscription` | حالة اشتراك المستأجر والخطة والتواريخ والحدود المسجلة |
| GET / POST | `/companies/{companyId}/customers` | عرض/إنشاء العملاء |
| GET / POST | `/companies/{companyId}/sales-invoice-drafts` | عرض/إنشاء مسودات مبيعات داخلية |
| GET | `/companies/{companyId}/sales-invoice-drafts/{invoiceId}` | تفاصيل المسودة وبنودها |
| POST | `/companies/{companyId}/sales-invoice-drafts/{invoiceId}/calculate-vat` | حفظ تصنيف VAT وحسابها على مسودة داخلية |
| POST | `/companies/{companyId}/sales-invoice-drafts/{invoiceId}/post-to-ledger` | إنشاء قيد داخلي متوازن وترحيله إلى GL وربطه بالمصدر مرة واحدة |
| POST | `/companies/{companyId}/sales-vat-preview` | حساب VAT مؤقت لكل بند؛ لا يحفظ المستند أو الضريبة |
| GET / POST | `/companies/{companyId}/customer-receipts` | عرض/إنشاء سندات قبض داخلية مخصصة لفواتير مبيعات مرحلة |
| GET | `/companies/{companyId}/customer-receipts/outstanding-invoices` | الفواتير المرحلة التي لها رصيد متبقٍ بعد سندات القبض المرحلة |
| POST | `/companies/{companyId}/customer-receipts/{receiptId}/post-to-ledger` | ترحيل سند القبض إلى النقدية/البنك والعملاء بقيد متوازن |
| GET / POST | `/companies/{companyId}/supplier-payments` | عرض/إنشاء سندات صرف داخلية مخصصة لفواتير شراء مرحلة |
| GET | `/companies/{companyId}/supplier-payments/outstanding-invoices` | فواتير الشراء المرحلة التي لها رصيد متبقٍ بعد سندات الصرف المرحلة |
| POST | `/companies/{companyId}/supplier-payments/{paymentId}/post-to-ledger` | ترحيل سند الصرف إلى الموردين والنقدية/البنك بقيد متوازن |
| GET | `/companies/{companyId}/reports/receivables-aging?asOfDate=YYYY-MM-DD&page=1&pageSize=100` | أعمار الذمم المدينة بالفواتير المفتوحة حتى تاريخ التقرير، استنادًا إلى تاريخ القيد المرحلة والتسويات المرحلة، مع ملخص إجمالي وترقيم صفحات |
| GET | `/companies/{companyId}/reports/payables-aging?asOfDate=YYYY-MM-DD&page=1&pageSize=100` | أعمار الذمم الدائنة بالفواتير المفتوحة حتى تاريخ التقرير، استنادًا إلى تاريخ القيد المرحلة والتسويات المرحلة، مع ملخص إجمالي وترقيم صفحات |

تفاصيل DTOs، وقواعد صلاحيات الأدوار، ومتطلبات الإعداد موجودة في `docs/accounting-core-api.md`. تفاصيل واجهات الاشتراك وحدودها موجودة في `docs/subscriptions.md`: حالة الأهلية لا تثبت تحصيل دفعة، لكن الخادم يمنع مسارات العمل عند الاشتراك غير الساري، ويفرض حد الشركات وحد القيود الشهري. الحسابات تتطلب رمزًا واسمًا عربيًا وإنجليزيًا ونوعًا ورصيدًا طبيعيًا. إنشاء القيد يتطلب فترة مفتوحة، وحسابات نشطة وقابلة للترحيل، وسطرين على الأقل ومجموع مدين مساويًا لمجموع دائن.


**حدود المبيعات/الضريبة:** يمكن حفظ حساب VAT وترحيل أثره المحاسبي الداخلي إلى دفتر الأستاذ، لكن المستند ليس فاتورة ضريبية صادرة، ولا يملك XML/QR/توقيع/تجزئة أو تكامل ZATCA. توجد تقارير أعمار الذمم المدينة والدائنة حتى تاريخ محدد، بالاعتماد على تاريخ القيد المرحلة لا مجرد تاريخ المستند، مع خصم التسويات المرحلة فقط. يجب مطابقتها مستقلاً مع الأستاذ العام قبل الاستخدام الرسمي. لا توجد بعد إشعارات دائنة/مدينة. سندات القبض/الصرف في وحدة الصناديق والبنوك هي قيود داخلية وليست بوابة دفع أو تحويلًا مصرفيًا آليًا أو مطابقة بنكية.

**حدود التقارير المالية:** تقارير الدخل والميزانية أولية وتجمع الحركات المرحلة فقط. لم ينفذ تقرير التدفقات النقدية لأن تصنيف التدفقات والحركات غير النقدية لم يُعتمد بعد، كما تحتاج القوائم إلى مراجعة سياسات الإقفال المحاسبي قبل الاستخدام الرسمي.

### قوالب دليل الحسابات

- `GET /api/v1/account-templates` يعرض القوالب النشطة وعدد حساباتها.
- `POST /api/v1/companies/{companyId}/accounts/apply-template` مع جسم مثل `{"templateCode":"TRADE"}` يطبق قالبًا على شركة لا تحتوي حسابات بعد.
- الأدوار `OWNER` و`ADMIN` فقط يمكنها تطبيق القالب. لن يدمج القالب تلقائيًا مع دليل موجود ولن يحذف حسابات.
- القوالب نقطة بداية للمراجعة وليست اعتمادًا محاسبيًا؛ ينبغي تعديلها بما يناسب النشاط والتزامات المنشأة. حسابات VAT لا تُنشئ وحدها معاملة ضريبية أو إقرارًا متوافقًا.

## تشغيل الفحوص والبناء

```powershell
node --test tests/contracts/*.test.mjs
dotnet test tests/BusinessERP.Domain.Tests/BusinessERP.Domain.Tests.csproj --configuration Release
dotnet build src/server/BusinessERP.Api/BusinessERP.Api.csproj --configuration Release
dotnet build apps/BusinessERP.Desktop/BusinessERP.Desktop.csproj --configuration Release
```

لإنشاء EXE خاص بالواجهة المكتبية من جهاز Windows:

```powershell
dotnet publish apps/BusinessERP.Desktop/BusinessERP.Desktop.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

نشر EXE وحده لا ينشر الخادم أو PostgreSQL أو موفر الهوية أو التطبيق السحابي.

## حواجز الإطلاق التجاري

قبل استقبال العملاء أو بيانات مالية حقيقية، يجب إكمال واختبار: ربط واجهة WPF بتسجيل دخول OIDC وآليات إدارة الرموز، تدفق إنشاء المستأجر/دعوات المستخدمين/الاشتراكات، صلاحيات تفصيلية ومراجعة فصل المهام، اختبار حي لموفر OIDC وإدارة عمر الرموز وخروج موفر الهوية، اختبارات RLS بين مستأجرين، الترحيلات التزايدية، النسخ الاحتياطي والاستعادة، فحص أمني واختبار اختراق، مراجعة محاسبية، ثم اختبار الامتثال للفوترة الإلكترونية في بيئة ZATCA الرسمية عند إضافة الفواتير. لم يُنفذ تكامل فاتورة ولم يُثبت اعتماد أو توافق كامل.
