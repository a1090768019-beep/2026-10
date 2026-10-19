# Business ERP — C# Accounting Core

مشروع ERP SaaS قيد التطوير للسوق السعودي، مبني من WPF وASP.NET Core وPostgreSQL متعدد المستأجرين. أحدث CI يبني API وWPF ويختبر مسارات محاسبية على PostgreSQL، لكن **المنتج غير جاهز للبيع**: لا توجد فاتورة ضريبية/نهائية متوافقة مع ZATCA، ولا تكامل مصرفي أو بوابة دفع SaaS أو تشغيل إنتاجي مُثبت.

## ما هو موجود في الشفرة الآن

- تطبيق Windows أصلي بـ WPF/XAML/C# مع تدفق OIDC عبر متصفح النظام، وتفعيل المستأجر بعد تحقق API من العضوية، وشاشات مبيعات ومشتريات RTL متصلة فعليًا بالـ API، وشريط أدوات علوي مستوحى من Office قابل للطي، وتنقل جانبي وتبويبات مساحة عمل. المشتريات تدعم الموردين ومسودات شراء مع VAT داخلي وتقرير بنود وترحيل قيد GL مزدوج مرتبط بالمصدر؛ يمكن فصل VAT في حساب أصل يختاره المستخدم أو تحميلها ضمن تكلفة الشراء. قوالب REPX القديمة مفهرسة في مساحة التقارير لكنها لا تعمل كعارض حتى ربط مصادر البيانات وتوفير محرك DevExpress مناسب. موفر الهوية غير مهيأ/غير مختبر حيًا؛ توجد وظائف مخزون وربط تكلفة البضاعة بالمبيعات، وسندات قبض/صرف داخلية عبر API فقط دون واجهات WPF مستقلة أو تكامل مصرفي. الفواتير الرسمية وبقية الشاشات ما زالت غير مكتملة.
- خادم ASP.NET Core / C# وREST API.
- PostgreSQL schema أولي: المستأجرون والشركات والحسابات والفترات والقيود وسطورها وسجل التدقيق.
- تحقق من القيد المزدوج باستخدام `decimal` وPostgreSQL `numeric(20,4)`.
- مسارات لحفظ الشركات والحسابات والسنوات والفترات، وإنشاء القيود كمسودات، واعتمادها وترحيلها وإلغاؤها وعكس القيود المرحلة.
- ميزان مراجعة للحركات ضمن نطاق التاريخ وأرصدة كل حساب حتى تاريخ النهاية.
- ستة قوالب دليل حسابات محفوظة كبيانات مرجعية في PostgreSQL: التجارة والخدمات والمقاولات والمطاعم والتصنيع والعقارات، مع نقطة تطبيق تتحقق من أن دليل الشركة فارغ وتكتب سجل تدقيق.
- مطابقة هوية OIDC مع مستخدم داخلي وعضوية نشطة ودور الخادم قبل تنفيذ أي مسار `/api/*`.
- حماية RLS وفحوص في قاعدة البيانات لمنع ترحيل القيد غير المتوازن، وتعديل القيود المرحلة، وتعديل سجلات التدقيق.
- مفتاح idempotency مع بصمة طلب، وفحوص عقود محلية تُشغّل بواسطة Node.js.
- واجهات قراءة لخطة الاشتراك الحالية وكتالوج الخطط النشطة؛ لا يوجد حتى الآن checkout أو تحصيل دفع SaaS أو فرض تلقائي للحصص. هذا منفصل عن سندات القبض/الصرف الداخلية.
- واجهات عملاء ومسودات مبيعات داخلية بعملة الشركة الأساسية، مع RLS وسجل تدقيق وقيود DB على الإجماليات. يحفظ النظام تصنيف VAT والمبالغ، ويمكن ترحيل قيد يومية متوازن مربوط بمصدر المسودة. لا يصدر فاتورة قانونية ولا يرسل إلى ZATCA.
- واجهات موردين ومسودات فواتير شراء داخلية مع RLS، تصنيفات VAT وتقرير صفحات، وقيود PostgreSQL تتحقق من مجاميع البنود. يمكن ترحيل المسودة المحسوبة إلى قيد مزدوج مرتبط مرة واحدة بـ GL، مع تجميد تعديل رأس/بنود المصدر بعد الترحيل. حساب VAT كأصل اختياري يختاره المستخدم؛ وإلا تُحمّل ضمن تكلفة الشراء. توجد أيضًا سندات صرف الموردين عبر API وتخصيصها على فواتير مرحلة وترحيلها إلى GL؛ لا توجد لها شاشة WPF مستقلة أو مطابقة مصرفية. هذا لا يثبت صلاحية فاتورة المورد أو قابلية استرداد VAT.
- أقفال PostgreSQL على مستوى المعاملة لتفادي سباقات إنشاء أرقام القيود ومفاتيح idempotency والعكس وتداخل السنوات المالية.

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
| POST | `/companies/{companyId}/customer-receipts/{receiptId}/post-to-ledger` | ترحيل سند القبض إلى النقدية/البنك والعملاء بقيد متوازن |
| GET / POST | `/companies/{companyId}/supplier-payments` | عرض/إنشاء سندات صرف داخلية مخصصة لفواتير شراء مرحلة |
| POST | `/companies/{companyId}/supplier-payments/{paymentId}/post-to-ledger` | ترحيل سند الصرف إلى الموردين والنقدية/البنك بقيد متوازن |

تفاصيل DTOs، وقواعد صلاحيات الأدوار، ومتطلبات الإعداد موجودة في `docs/accounting-core-api.md`. تفاصيل واجهات الاشتراك وحدودها موجودة في `docs/subscriptions.md`؛ حالة الأهلية هناك للعرض فقط ولا تمنع الوصول ولا تثبت تحصيل دفعة. الحسابات تتطلب رمزًا واسمًا عربيًا وإنجليزيًا ونوعًا ورصيدًا طبيعيًا. إنشاء القيد يتطلب فترة مفتوحة، وحسابات نشطة وقابلة للترحيل، وسطرين على الأقل ومجموع مدين مساويًا لمجموع دائن.


**حدود المبيعات/الضريبة:** يمكن حفظ حساب VAT وترحيل أثره المحاسبي الداخلي إلى دفتر الأستاذ، لكن المستند ليس فاتورة ضريبية صادرة، ولا يملك XML/QR/توقيع/تجزئة أو تكامل ZATCA. لا توجد بعد إشعارات دائنة/مدينة أو أعمار ديون. سندات القبض/الصرف المتاحة عبر API قيود تسوية داخلية وليست بوابة دفع أو تكاملًا مصرفيًا، ولا توجد لها شاشات WPF مستقلة.

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
