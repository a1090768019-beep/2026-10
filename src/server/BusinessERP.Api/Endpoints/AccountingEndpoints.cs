using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Domain.Accounting;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

public static class AccountingEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AccountTypes = ["ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE"];
    private static readonly HashSet<string> NormalBalances = ["DEBIT", "CREDIT"];
    private static readonly string[] MasterDataRoles = ["OWNER", "ADMIN"];
    private static readonly string[] AccountingRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapAccountingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithTags("Business accounting");

        api.MapGet("/companies", GetCompanies).WithName("GetCompanies");
        api.MapPost("/companies", CreateCompany).WithName("CreateCompany");
        api.MapGet("/companies/{companyId:guid}/accounts", GetAccounts).WithName("GetAccounts");
        api.MapPost("/companies/{companyId:guid}/accounts", CreateAccount).WithName("CreateAccount");
        api.MapGet("/companies/{companyId:guid}/fiscal-years", GetFiscalYears).WithName("GetFiscalYears");
        api.MapPost("/companies/{companyId:guid}/fiscal-years", CreateFiscalYear).WithName("CreateFiscalYear");
        api.MapGet("/companies/{companyId:guid}/fiscal-periods", GetFiscalPeriods).WithName("GetFiscalPeriods");
        api.MapPost("/fiscal-periods/{periodId:guid}/close", CloseFiscalPeriod).WithName("CloseFiscalPeriod");
        api.MapGet("/companies/{companyId:guid}/accounts/{accountId:guid}/ledger", GetGeneralLedger).WithName("GetGeneralLedger");
        api.MapGet("/companies/{companyId:guid}/journal-entries", GetJournalEntries).WithName("GetJournalEntries");
        api.MapPost("/companies/{companyId:guid}/journal-entries", CreateJournalEntry).WithName("CreateJournalEntry");
        api.MapPut("/journal-entries/{entryId:guid}", UpdateDraftJournalEntry).WithName("UpdateDraftJournalEntry");
        api.MapGet("/journal-entries/{entryId:guid}", GetJournalEntry).WithName("GetJournalEntry");
        api.MapPost("/journal-entries/{entryId:guid}/approve", ApproveJournalEntry).WithName("ApproveJournalEntry");
        api.MapPost("/journal-entries/{entryId:guid}/post", PostJournalEntry).WithName("PostJournalEntry");
        api.MapPost("/journal-entries/{entryId:guid}/cancel", CancelJournalEntry).WithName("CancelJournalEntry");
        api.MapPost("/journal-entries/{entryId:guid}/reverse", ReverseJournalEntry).WithName("ReverseJournalEntry");
        api.MapGet("/companies/{companyId:guid}/reports/trial-balance", GetTrialBalance).WithName("GetTrialBalance");

        return endpoints;
    }

    private static async Task<IResult> GetCompanies(TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var companies = await db.Companies.AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => new CompanyDto(x.Id, x.Name, x.BaseCurrency, x.TaxNumber, x.CreatedAt))
                .ToListAsync(token);
            return Results.Ok(companies);
        }, ct);

    private static async Task<IResult> CreateCompany(
        CreateCompanyRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, MasterDataRoles)) return Results.Forbid();
        var errors = new Dictionary<string, string[]>();
        var name = request.Name?.Trim() ?? "";
        var currency = (request.BaseCurrency ?? "SAR").Trim().ToUpperInvariant();
        if (name.Length is < 2 or > 200) errors["name"] = ["اسم الشركة مطلوب (2-200 حرف)."];
        if (currency.Length != 3 || currency.Any(ch => ch is < 'A' or > 'Z')) errors["baseCurrency"] = ["العملة يجب أن تكون رمزًا لاتينيًا من ثلاثة أحرف."];
        if (request.TaxNumber?.Length > 32) errors["taxNumber"] = ["الرقم الضريبي لا يتجاوز 32 حرفًا."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = new CompanyRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, Name = name,
                BaseCurrency = currency, TaxNumber = string.IsNullOrWhiteSpace(request.TaxNumber) ? null : request.TaxNumber.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            db.Companies.Add(company);
            db.AuditLogs.Add(CreateAudit(tenant, http, "COMPANY_CREATED", "Company", company.Id.ToString("D"), new { company.Name, company.BaseCurrency }));
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/companies/{company.Id}", ToDto(company));
        }, ct);
    }

    private static async Task<IResult> GetAccounts(Guid companyId, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == companyId)
                .OrderBy(x => x.Code)
                .Select(x => new AccountDto(x.Id, x.Code, x.NameAr, x.NameEn, x.AccountType, x.NormalBalance, x.ParentAccountId, x.IsActive, x.AllowPosting))
                .ToListAsync(token);
            return Results.Ok(accounts);
        }, ct);

    private static async Task<IResult> CreateAccount(
        Guid companyId, CreateAccountRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        var code = request.Code?.Trim() ?? "";
        var nameAr = request.NameAr?.Trim() ?? "";
        var nameEn = request.NameEn?.Trim() ?? "";
        var type = request.AccountType?.Trim().ToUpperInvariant() ?? "";
        var normal = request.NormalBalance?.Trim().ToUpperInvariant() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (code.Length is < 1 or > 64) errors["code"] = ["رمز الحساب مطلوب ولا يتجاوز 64 حرفًا."];
        if (nameAr.Length is < 1 or > 200) errors["nameAr"] = ["اسم الحساب بالعربية مطلوب."];
        if (nameEn.Length is < 1 or > 200) errors["nameEn"] = ["اسم الحساب بالإنجليزية مطلوب."];
        if (!AccountTypes.Contains(type)) errors["accountType"] = ["نوع الحساب غير صالح."];
        if (!NormalBalances.Contains(normal)) errors["normalBalance"] = ["الرصيد الطبيعي يجب أن يكون DEBIT أو CREDIT."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            if (request.ParentAccountId is Guid parentId)
            {
                var parent = await db.Accounts.SingleOrDefaultAsync(x => x.Id == parentId && x.CompanyId == companyId, token);
                if (parent is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentAccountId"] = ["الحساب الأب غير موجود في هذه الشركة."] });
                if (parent.AllowPosting) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentAccountId"] = ["اجعل الحساب الأب تجميعيًا وغير قابل للترحيل قبل إضافة حسابات فرعية."] });
            }
            if (await db.Accounts.AnyAsync(x => x.CompanyId == companyId && x.Code == code, token))
                return Results.Conflict(new { error = "account_code_exists", message = "رمز الحساب مستخدم مسبقًا في هذه الشركة." });

            var account = new AccountRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                ParentAccountId = request.ParentAccountId, Code = code, NameAr = nameAr, NameEn = nameEn,
                AccountType = type, NormalBalance = normal, IsActive = true,
                AllowPosting = request.AllowPosting, CreatedAt = DateTime.UtcNow
            };
            db.Accounts.Add(account);
            db.AuditLogs.Add(CreateAudit(tenant, http, "ACCOUNT_CREATED", "Account", account.Id.ToString("D"), new { account.Code, account.NameAr, account.AccountType }));
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/companies/{companyId}/accounts/{account.Id}", ToDto(account));
        }, ct);
    }

    private static async Task<IResult> GetFiscalYears(Guid companyId, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var years = await db.FiscalYears.AsNoTracking().Where(x => x.CompanyId == companyId)
                .OrderByDescending(x => x.StartDate)
                .Select(x => new FiscalYearDto(x.Id, x.Name, x.StartDate, x.EndDate, x.Status))
                .ToListAsync(token);
            return Results.Ok(years);
        }, ct);

    private static async Task<IResult> CreateFiscalYear(
        Guid companyId, CreateFiscalYearRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        var name = request.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (name.Length is < 1 or > 100) errors["name"] = ["اسم السنة المالية مطلوب."];
        if (request.StartDate > request.EndDate) errors["dateRange"] = ["تاريخ البداية يجب ألا يتجاوز تاريخ النهاية."];
        if (request.StartDate < new DateOnly(1900, 1, 1) || request.EndDate > new DateOnly(2200, 12, 31)) errors["dateRange"] = ["نطاق التاريخ خارج الحدود المدعومة."];
        if (request.EndDate.DayNumber - request.StartDate.DayNumber > 3660) errors["dateRange"] = ["الحد الأقصى للسنة المالية عشر سنوات."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var overlaps = await db.FiscalYears.AnyAsync(x => x.CompanyId == companyId && request.StartDate <= x.EndDate && request.EndDate >= x.StartDate, token);
            if (overlaps) return Results.Conflict(new { error = "fiscal_year_overlap", message = "تتداخل السنة المالية مع سنة موجودة." });

            var year = new FiscalYearRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                Name = name, StartDate = request.StartDate, EndDate = request.EndDate, Status = "OPEN"
            };
            db.FiscalYears.Add(year);
            var periodStart = request.StartDate;
            var periodNumber = 1;
            while (periodStart <= request.EndDate)
            {
                var monthLastDay = new DateOnly(periodStart.Year, periodStart.Month, DateTime.DaysInMonth(periodStart.Year, periodStart.Month));
                var periodEnd = monthLastDay < request.EndDate ? monthLastDay : request.EndDate;
                db.FiscalPeriods.Add(new FiscalPeriodRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                    FiscalYearId = year.Id, PeriodNumber = periodNumber++, StartDate = periodStart,
                    EndDate = periodEnd, Status = "OPEN"
                });
                periodStart = periodEnd.AddDays(1);
            }
            db.AuditLogs.Add(CreateAudit(tenant, http, "FISCAL_YEAR_CREATED", "FiscalYear", year.Id.ToString("D"), new { year.Name, year.StartDate, year.EndDate, Periods = periodNumber - 1 }));
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/companies/{companyId}/fiscal-years/{year.Id}", new FiscalYearCreatedDto(year.Id, year.Name, year.StartDate, year.EndDate, periodNumber - 1));
        }, ct);
    }

    private static async Task<IResult> GetFiscalPeriods(Guid companyId, Guid? fiscalYearId, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var query = db.FiscalPeriods.AsNoTracking().Where(x => x.CompanyId == companyId);
            if (fiscalYearId.HasValue) query = query.Where(x => x.FiscalYearId == fiscalYearId.Value);
            var periods = await query.OrderBy(x => x.StartDate)
                .Select(x => new FiscalPeriodDto(x.Id, x.FiscalYearId, x.PeriodNumber, x.StartDate, x.EndDate, x.Status))
                .ToListAsync(token);
            return Results.Ok(periods);
        }, ct);

    private static async Task<IResult> CreateJournalEntry(
        Guid companyId, CreateJournalEntryRequest request, HttpContext http, ITenantContext tenant,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey)) idempotencyKey = null;
        if (idempotencyKey is { Length: > 200 }) return Results.BadRequest(new { error = "idempotency_key_too_long" });
        var normalizedCurrency = request.CurrencyCode?.Trim().ToUpperInvariant() ?? "";
        var description = request.Description?.Trim() ?? "";
        var entryNumber = request.EntryNumber?.Trim() ?? "";
        var lines = request.Lines ?? [];
        var domainDraft = new JournalEntryDraft(tenant.TenantId, companyId, normalizedCurrency,
            lines.Select(x => new JournalEntryLineDraft(x.AccountId, x.Debit, x.Credit, x.Description)).ToArray());
        var validationErrors = JournalEntryValidator.Validate(domainDraft);
        var errors = new Dictionary<string, string[]>();
        if (validationErrors.Count > 0) errors["lines"] = validationErrors.ToArray();
        if (entryNumber.Length is < 1 or > 80) errors["entryNumber"] = ["رقم القيد مطلوب ولا يتجاوز 80 حرفًا."];
        if (description.Length is < 1 or > 1000) errors["description"] = ["وصف القيد مطلوب."];
        if (lines.Count > 500) errors["lines"] = ["الحد الأقصى 500 سطر للقيد."];
        if (lines.Any(x => !HasSupportedMoneyPrecision(x.Debit) || !HasSupportedMoneyPrecision(x.Credit))) errors["amounts"] = ["المبالغ يجب أن تكون بدقة لا تتجاوز أربع منازل عشرية وضمن الحد المحاسبي المدعوم."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var requestHash = ComputeHash(request);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.SingleOrDefaultAsync(x => x.Id == companyId, token);
            if (company is null) return Results.NotFound();
            if (!string.Equals(normalizedCurrency, company.BaseCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencyCode"] = [$"النسخة الحالية تقبل القيود بعملة الشركة الأساسية فقط ({company.BaseCurrency.Trim()}). لا تستخدم عملة أخرى قبل تفعيل محاسبة العملات وأسعار الصرف."] });
            if (idempotencyKey is not null)
            {
                var replay = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId && x.IdempotencyKey == idempotencyKey, token);
                if (replay is not null)
                {
                    if (!string.Equals(replay.IdempotencyRequestHash, requestHash, StringComparison.Ordinal))
                        return Results.Conflict(new { error = "idempotency_key_reused", message = "أُعيد استخدام مفتاح العملية مع محتوى مختلف." });
                    return Results.Ok(await LoadJournalEntryDtoAsync(db, replay, token));
                }
            }
            if (await db.JournalEntries.AnyAsync(x => x.CompanyId == companyId && x.EntryNumber == entryNumber, token))
                return Results.Conflict(new { error = "journal_entry_number_exists", message = "رقم القيد مستخدم مسبقًا." });

            var period = await db.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == request.FiscalPeriodId && x.CompanyId == companyId, token);
            if (period is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة لهذه الشركة."] });
            if (period.Status != "OPEN" || request.EntryDate < period.StartDate || request.EntryDate > period.EndDate)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["entryDate"] = ["الفترة مغلقة أو تاريخ القيد خارج نطاقها."] });

            var accountIds = lines.Select(x => x.AccountId).Distinct().ToArray();
            var accounts = await db.Accounts.Where(x => x.CompanyId == companyId && accountIds.Contains(x.Id)).ToListAsync(token);
            if (accounts.Count != accountIds.Length) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines.accountId"] = ["يحتوي القيد على حساب غير موجود في الشركة."] });
            if (accounts.Any(x => !x.IsActive || !x.AllowPosting)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines.accountId"] = ["كل الحسابات المستخدمة يجب أن تكون نشطة وقابلة للترحيل."] });

            var entry = new JournalEntryRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                FiscalPeriodId = period.Id, EntryNumber = entryNumber, EntryDate = request.EntryDate,
                Description = description, CurrencyCode = normalizedCurrency, Status = "DRAFT",
                IdempotencyKey = idempotencyKey, IdempotencyRequestHash = idempotencyKey is null ? null : requestHash,
                CreatedAt = DateTime.UtcNow
            };
            db.JournalEntries.Add(entry);
            var lineNumber = 1;
            foreach (var line in lines)
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                    JournalEntryId = entry.Id, AccountId = line.AccountId, LineNumber = lineNumber++,
                    Description = line.Description?.Trim() ?? "", Debit = line.Debit, Credit = line.Credit
                });
            }
            db.AuditLogs.Add(CreateAudit(tenant, http, "JOURNAL_ENTRY_CREATED", "JournalEntry", entry.Id.ToString("D"), new { entry.EntryNumber, entry.EntryDate, LineCount = lines.Count, TotalDebit = lines.Sum(x => x.Debit), TotalCredit = lines.Sum(x => x.Credit) }));
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/journal-entries/{entry.Id}", await LoadJournalEntryDtoAsync(db, entry, token));
        }, ct);
    }

    private static async Task<IResult> UpdateDraftJournalEntry(
        Guid entryId, UpdateJournalEntryRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        var description = request.Description?.Trim() ?? "";
        var currency = request.CurrencyCode?.Trim().ToUpperInvariant() ?? "";
        var number = request.EntryNumber?.Trim() ?? "";
        var lines = request.Lines ?? [];
        var errors = new Dictionary<string, string[]>();
        if (number.Length is < 1 or > 80) errors["entryNumber"] = ["رقم القيد مطلوب ولا يتجاوز 80 حرفًا."];
        if (description.Length is < 1 or > 1000) errors["description"] = ["وصف القيد مطلوب."];
        if (lines.Count > 500) errors["lines"] = ["الحد الأقصى 500 سطر للقيد."];
        if (lines.Any(x => !HasSupportedMoneyPrecision(x.Debit) || !HasSupportedMoneyPrecision(x.Credit))) errors["amounts"] = ["المبالغ يجب أن تكون بدقة لا تتجاوز أربع منازل عشرية وضمن الحد المحاسبي المدعوم."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var entry = await db.JournalEntries.SingleOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is null) return Results.NotFound();
            if (entry.Status != "DRAFT") return Results.Conflict(new { error = "only_draft_entries_can_be_edited" });
            var companyBaseCurrency = await db.Companies.Where(x => x.Id == entry.CompanyId)
                .Select(x => x.BaseCurrency).SingleOrDefaultAsync(token);
            if (companyBaseCurrency is null) return Results.NotFound();
            if (!string.Equals(currency, companyBaseCurrency.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencyCode"] = [$"النسخة الحالية تقبل القيود بعملة الشركة الأساسية فقط ({companyBaseCurrency.Trim()})."] });
            var validation = JournalEntryValidator.Validate(new JournalEntryDraft(tenant.TenantId, entry.CompanyId, currency,
                lines.Select(x => new JournalEntryLineDraft(x.AccountId, x.Debit, x.Credit, x.Description)).ToArray()));
            if (validation.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = validation.ToArray() });
            if (await db.JournalEntries.AnyAsync(x => x.CompanyId == entry.CompanyId && x.Id != entry.Id && x.EntryNumber == number, token))
                return Results.Conflict(new { error = "journal_entry_number_exists" });
            var period = await db.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == request.FiscalPeriodId && x.CompanyId == entry.CompanyId, token);
            if (period is null || period.Status != "OPEN" || request.EntryDate < period.StartDate || request.EntryDate > period.EndDate)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة أو مغلقة أو تاريخ القيد خارجها."] });
            var accountIds = lines.Select(x => x.AccountId).Distinct().ToArray();
            var accounts = await db.Accounts.Where(x => x.CompanyId == entry.CompanyId && accountIds.Contains(x.Id)).ToListAsync(token);
            if (accounts.Count != accountIds.Length) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines.accountId"] = ["يحتوي القيد على حساب غير موجود في الشركة."] });
            if (accounts.Any(x => !x.IsActive || !x.AllowPosting)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines.accountId"] = ["كل الحسابات المستخدمة يجب أن تكون نشطة وقابلة للترحيل."] });

            var oldLines = await db.JournalEntryLines.Where(x => x.JournalEntryId == entry.Id).ToListAsync(token);
            db.JournalEntryLines.RemoveRange(oldLines);
            await db.SaveChangesAsync(token);
            entry.EntryNumber = number;
            entry.EntryDate = request.EntryDate;
            entry.FiscalPeriodId = period.Id;
            entry.Description = description;
            entry.CurrencyCode = currency;
            var lineNumber = 1;
            foreach (var line in lines)
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = entry.CompanyId,
                    JournalEntryId = entry.Id, AccountId = line.AccountId, LineNumber = lineNumber++,
                    Description = line.Description?.Trim() ?? "", Debit = line.Debit, Credit = line.Credit
                });
            }
            db.AuditLogs.Add(CreateAudit(tenant, http, "JOURNAL_ENTRY_DRAFT_UPDATED", "JournalEntry", entry.Id.ToString("D"), new { entry.EntryNumber, entry.EntryDate, LineCount = lines.Count }));
            await db.SaveChangesAsync(token);
            return Results.Ok(await LoadJournalEntryDtoAsync(db, entry, token));
        }, ct);
    }

    private static async Task<IResult> CloseFiscalPeriod(
        Guid periodId, ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var period = await db.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == periodId, token);
            if (period is null) return Results.NotFound();
            if (period.Status != "OPEN")
                return Results.Conflict(new { error = "fiscal_period_not_open", currentStatus = period.Status });

            var unpostedExists = await db.JournalEntries.AnyAsync(x => x.FiscalPeriodId == period.Id &&
                (x.Status == "DRAFT" || x.Status == "APPROVED"), token);
            if (unpostedExists)
                return Results.Conflict(new { error = "fiscal_period_has_unposted_entries", message = "لا يمكن إقفال الفترة قبل ترحيل القيود المعتمدة أو إلغاء المسودات." });

            period.Status = "CLOSED";
            db.AuditLogs.Add(CreateAudit(tenant, http, "FISCAL_PERIOD_CLOSED", "FiscalPeriod", period.Id.ToString("D"),
                new { period.CompanyId, period.PeriodNumber, period.StartDate, period.EndDate }));
            await db.SaveChangesAsync(token);
            return Results.Ok(new FiscalPeriodDto(period.Id, period.FiscalYearId, period.PeriodNumber,
                period.StartDate, period.EndDate, period.Status));
        }, ct);
    }

    private static async Task<IResult> GetGeneralLedger(
        Guid companyId, Guid accountId, DateOnly fromDate, DateOnly toDate,
        TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (fromDate > toDate)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["تاريخ البداية يجب ألا يتجاوز تاريخ النهاية."] });
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var account = await db.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == accountId && x.CompanyId == companyId, token);
            if (account is null) return Results.NotFound();

            var movements = await (from line in db.JournalEntryLines.AsNoTracking()
                                   join entry in db.JournalEntries.AsNoTracking()
                                     on new { line.TenantId, line.CompanyId, Id = line.JournalEntryId }
                                     equals new { entry.TenantId, entry.CompanyId, Id = entry.Id }
                                   where line.CompanyId == companyId && line.AccountId == accountId &&
                                         entry.Status == "POSTED" && entry.EntryDate <= toDate
                                   orderby entry.EntryDate, entry.EntryNumber, line.LineNumber
                                   select new
                                   {
                                       EntryId = entry.Id, entry.EntryNumber, entry.EntryDate, entry.Description,
                                       entry.CurrencyCode, line.LineNumber, LineDescription = line.Description,
                                       line.Debit, line.Credit
                                   }).ToListAsync(token);

            decimal openingBalance = 0m;
            decimal runningBalance = 0m;
            decimal totalDebit = 0m;
            decimal totalCredit = 0m;
            var output = new List<GeneralLedgerLineDto>();
            foreach (var movement in movements)
            {
                var delta = movement.Debit - movement.Credit;
                if (movement.EntryDate < fromDate)
                {
                    openingBalance = checked(openingBalance + delta);
                    runningBalance = openingBalance;
                    continue;
                }

                runningBalance = checked(runningBalance + delta);
                totalDebit = checked(totalDebit + movement.Debit);
                totalCredit = checked(totalCredit + movement.Credit);
                output.Add(new GeneralLedgerLineDto(movement.EntryId, movement.EntryNumber, movement.EntryDate,
                    movement.Description, movement.CurrencyCode.Trim(), movement.LineNumber, movement.LineDescription,
                    movement.Debit, movement.Credit, runningBalance));
            }

            return Results.Ok(new GeneralLedgerDto(companyId, accountId, account.Code, account.NameAr,
                fromDate, toDate, openingBalance, output, runningBalance, totalDebit, totalCredit));
        }, ct);

    private static async Task<IResult> GetJournalEntries(Guid companyId, int? limit, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var entries = await db.JournalEntries.AsNoTracking().Where(x => x.CompanyId == companyId)
                .OrderByDescending(x => x.EntryDate).ThenByDescending(x => x.EntryNumber).Take(take).ToListAsync(token);
            var ids = entries.Select(x => x.Id).ToArray();
            var sums = await db.JournalEntryLines.AsNoTracking().Where(x => x.CompanyId == companyId && ids.Contains(x.JournalEntryId))
                .GroupBy(x => x.JournalEntryId)
                .Select(g => new { EntryId = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit), Count = g.Count() })
                .ToDictionaryAsync(x => x.EntryId, token);
            var response = entries.Select(x =>
            {
                sums.TryGetValue(x.Id, out var summary);
                return new JournalEntryListDto(x.Id, x.EntryNumber, x.EntryDate, x.Description, x.CurrencyCode.Trim(), x.Status,
                    summary?.Debit ?? 0m, summary?.Credit ?? 0m, summary?.Count ?? 0);
            });
            return Results.Ok(response);
        }, ct);

    private static async Task<IResult> GetJournalEntry(Guid entryId, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var entry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is null) return Results.NotFound();
            return Results.Ok(await LoadJournalEntryDtoAsync(db, entry, token));
        }, ct);

    private static Task<IResult> ApproveJournalEntry(Guid entryId, ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct) =>
        ChangeJournalStatus(entryId, "APPROVED", "JOURNAL_ENTRY_APPROVED", tenant, http, runner, ct);

    private static Task<IResult> PostJournalEntry(Guid entryId, ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct) =>
        ChangeJournalStatus(entryId, "POSTED", "JOURNAL_ENTRY_POSTED", tenant, http, runner, ct);

    private static Task<IResult> CancelJournalEntry(Guid entryId, ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct) =>
        ChangeJournalStatus(entryId, "CANCELLED", "JOURNAL_ENTRY_CANCELLED", tenant, http, runner, ct);

    private static async Task<IResult> ChangeJournalStatus(
        Guid entryId, string targetStatus, string auditAction, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var entry = await db.JournalEntries.SingleOrDefaultAsync(x => x.Id == entryId, token);
            if (entry is null) return Results.NotFound();
            var allowed = targetStatus switch
            {
                "APPROVED" => entry.Status == "DRAFT",
                "POSTED" => entry.Status == "APPROVED",
                "CANCELLED" => entry.Status is "DRAFT" or "APPROVED",
                _ => false
            };
            if (!allowed) return Results.Conflict(new { error = "invalid_journal_state_transition", currentStatus = entry.Status, requestedStatus = targetStatus });

            if (targetStatus is "APPROVED" or "POSTED")
            {
                var period = await db.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == entry.FiscalPeriodId && x.CompanyId == entry.CompanyId, token);
                if (period is null || period.Status != "OPEN" || entry.EntryDate < period.StartDate || entry.EntryDate > period.EndDate)
                    return Results.Conflict(new { error = "fiscal_period_not_open" });
                var lines = await db.JournalEntryLines.Where(x => x.JournalEntryId == entry.Id).ToListAsync(token);
                if (lines.Count < 2 || lines.Sum(x => x.Debit) != lines.Sum(x => x.Credit))
                    return Results.Conflict(new { error = "journal_entry_not_balanced" });
                var accountIds = lines.Select(x => x.AccountId).Distinct().ToArray();
                var accounts = await db.Accounts.Where(x => x.CompanyId == entry.CompanyId && accountIds.Contains(x.Id)).ToListAsync(token);
                if (accounts.Count != accountIds.Length || accounts.Any(x => !x.IsActive || !x.AllowPosting))
                    return Results.Conflict(new { error = "journal_entry_has_invalid_accounts" });
            }
            entry.Status = targetStatus;
            if (targetStatus == "POSTED") entry.PostedAt = DateTime.UtcNow;
            db.AuditLogs.Add(CreateAudit(tenant, http, auditAction, "JournalEntry", entry.Id.ToString("D"), new { entry.EntryNumber, Status = targetStatus }));
            await db.SaveChangesAsync(token);
            return Results.Ok(await LoadJournalEntryDtoAsync(db, entry, token));
        }, ct);
    }

    private static async Task<IResult> ReverseJournalEntry(
        Guid entryId, ReverseJournalEntryRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!HasRole(tenant, AccountingRoles)) return Results.Forbid();
        var number = request.EntryNumber?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (number.Length is < 1 or > 80) errors["entryNumber"] = ["رقم قيد العكس مطلوب."];
        if (description.Length is < 1 or > 1000) errors["description"] = ["وصف قيد العكس مطلوب."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var original = await db.JournalEntries.SingleOrDefaultAsync(x => x.Id == entryId, token);
            if (original is null) return Results.NotFound();
            if (original.Status != "POSTED" || original.ReversalOfEntryId is not null)
                return Results.Conflict(new { error = "only_original_posted_entries_can_be_reversed" });
            if (await db.JournalEntries.AnyAsync(x => x.CompanyId == original.CompanyId && x.ReversalOfEntryId == original.Id, token))
                return Results.Conflict(new { error = "journal_entry_already_reversed" });
            if (await db.JournalEntries.AnyAsync(x => x.CompanyId == original.CompanyId && x.EntryNumber == number, token))
                return Results.Conflict(new { error = "journal_entry_number_exists" });
            var period = await db.FiscalPeriods.SingleOrDefaultAsync(x => x.Id == request.FiscalPeriodId && x.CompanyId == original.CompanyId, token);
            if (period is null || period.Status != "OPEN" || request.EntryDate < period.StartDate || request.EntryDate > period.EndDate)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة أو مغلقة أو تاريخ العكس خارجها."] });

            var originalLines = await db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntryId == original.Id).OrderBy(x => x.LineNumber).ToListAsync(token);
            var reversal = new JournalEntryRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = original.CompanyId,
                FiscalPeriodId = period.Id, ReversalOfEntryId = original.Id, EntryNumber = number,
                EntryDate = request.EntryDate, Description = description, CurrencyCode = original.CurrencyCode.Trim(),
                Status = "DRAFT", CreatedAt = DateTime.UtcNow
            };
            db.JournalEntries.Add(reversal);
            foreach (var line in originalLines)
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = original.CompanyId,
                    JournalEntryId = reversal.Id, AccountId = line.AccountId, LineNumber = line.LineNumber,
                    Description = string.IsNullOrWhiteSpace(line.Description) ? "عكس " + original.EntryNumber : line.Description,
                    Debit = line.Credit, Credit = line.Debit
                });
            }
            await db.SaveChangesAsync(token);
            reversal.Status = "APPROVED";
            await db.SaveChangesAsync(token);
            reversal.Status = "POSTED";
            reversal.PostedAt = DateTime.UtcNow;
            db.AuditLogs.Add(CreateAudit(tenant, http, "JOURNAL_ENTRY_REVERSED", "JournalEntry", reversal.Id.ToString("D"), new { OriginalEntryId = original.Id, OriginalEntryNumber = original.EntryNumber, reversal.EntryNumber }));
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/journal-entries/{reversal.Id}", await LoadJournalEntryDtoAsync(db, reversal, token));
        }, ct);
    }

    private static async Task<IResult> GetTrialBalance(
        Guid companyId, DateOnly fromDate, DateOnly toDate, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (fromDate > toDate) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["تاريخ البداية يجب ألا يتجاوز تاريخ النهاية."] });
            if (!await db.Companies.AnyAsync(x => x.Id == companyId, token)) return Results.NotFound();
            var periodMovement = await (from line in db.JournalEntryLines.AsNoTracking()
                                        join entry in db.JournalEntries.AsNoTracking() on new { line.TenantId, line.CompanyId, Id = line.JournalEntryId } equals new { entry.TenantId, entry.CompanyId, Id = entry.Id }
                                        where line.CompanyId == companyId && entry.Status == "POSTED" && entry.EntryDate >= fromDate && entry.EntryDate <= toDate
                                        group line by line.AccountId into g
                                        select new { AccountId = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(token);
            var balancesToDate = await (from line in db.JournalEntryLines.AsNoTracking()
                                        join entry in db.JournalEntries.AsNoTracking() on new { line.TenantId, line.CompanyId, Id = line.JournalEntryId } equals new { entry.TenantId, entry.CompanyId, Id = entry.Id }
                                        where line.CompanyId == companyId && entry.Status == "POSTED" && entry.EntryDate <= toDate
                                        group line by line.AccountId into g
                                        select new { AccountId = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(token);
            var movements = periodMovement.ToDictionary(x => x.AccountId, x => x);
            var balances = balancesToDate.ToDictionary(x => x.AccountId, x => x);
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == companyId).OrderBy(x => x.Code).ToListAsync(token);
            var rows = accounts.Select(a =>
            {
                movements.TryGetValue(a.Id, out var movement);
                balances.TryGetValue(a.Id, out var balance);
                var debitMovement = movement?.Debit ?? 0m;
                var creditMovement = movement?.Credit ?? 0m;
                var debitTotal = balance?.Debit ?? 0m;
                var creditTotal = balance?.Credit ?? 0m;
                var net = debitTotal - creditTotal;
                return new TrialBalanceRowDto(a.Id, a.Code, a.NameAr, a.NameEn, a.AccountType,
                    debitMovement, creditMovement, Math.Max(net, 0m), Math.Max(-net, 0m));
            }).ToList();
            return Results.Ok(new TrialBalanceDto(companyId, fromDate, toDate, rows,
                rows.Sum(x => x.DebitMovement), rows.Sum(x => x.CreditMovement),
                rows.Sum(x => x.DebitBalance), rows.Sum(x => x.CreditBalance)));
        }, ct);

    private static async Task<JournalEntryDto> LoadJournalEntryDtoAsync(ErpDbContext db, JournalEntryRecord entry, CancellationToken ct)
    {
        var lines = await db.JournalEntryLines.AsNoTracking().Where(x => x.JournalEntryId == entry.Id).OrderBy(x => x.LineNumber)
            .Join(db.Accounts.AsNoTracking(), line => line.AccountId, account => account.Id,
                (line, account) => new JournalEntryLineDto(line.Id, line.LineNumber, account.Id, account.Code, account.NameAr, line.Description, line.Debit, line.Credit))
            .ToListAsync(ct);
        return new JournalEntryDto(entry.Id, entry.CompanyId, entry.FiscalPeriodId, entry.ReversalOfEntryId,
            entry.EntryNumber, entry.EntryDate, entry.Description, entry.CurrencyCode.Trim(), entry.Status,
            entry.CreatedAt, entry.PostedAt, lines, lines.Sum(x => x.Debit), lines.Sum(x => x.Credit));
    }

    private static AuditLogRecord CreateAudit(ITenantContext tenant, HttpContext http, string action, string entityType, string entityId, object details) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId, Action = action,
        EntityType = entityType, EntityId = entityId, IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        DetailsJson = JsonSerializer.Serialize(details, JsonOptions), CreatedAt = DateTime.UtcNow
    };

    private static bool HasRole(ITenantContext tenant, params string[] allowedRoles) => allowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase);
    private static bool HasSupportedMoneyPrecision(decimal value) => value >= 0m && value <= 9_999_999_999_999_999.9999m && decimal.Round(value, 4, MidpointRounding.ToEven) == value;
    private static string ComputeHash(CreateJournalEntryRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions))));
    private static CompanyDto ToDto(CompanyRecord x) => new(x.Id, x.Name, x.BaseCurrency.Trim(), x.TaxNumber, x.CreatedAt);
    private static AccountDto ToDto(AccountRecord x) => new(x.Id, x.Code, x.NameAr, x.NameEn, x.AccountType, x.NormalBalance, x.ParentAccountId, x.IsActive, x.AllowPosting);

    public sealed record CreateCompanyRequest(string Name, string BaseCurrency = "SAR", string? TaxNumber = null);
    public sealed record CompanyDto(Guid Id, string Name, string BaseCurrency, string? TaxNumber, DateTime CreatedAt);
    public sealed record CreateAccountRequest(string Code, string NameAr, string NameEn, string AccountType, string NormalBalance, Guid? ParentAccountId = null, bool AllowPosting = true);
    public sealed record AccountDto(Guid Id, string Code, string NameAr, string NameEn, string AccountType, string NormalBalance, Guid? ParentAccountId, bool IsActive, bool AllowPosting);
    public sealed record CreateFiscalYearRequest(string Name, DateOnly StartDate, DateOnly EndDate);
    public sealed record FiscalYearDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
    public sealed record FiscalYearCreatedDto(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, int PeriodsCreated);
    public sealed record FiscalPeriodDto(Guid Id, Guid FiscalYearId, int PeriodNumber, DateOnly StartDate, DateOnly EndDate, string Status);
    public sealed record CreateJournalEntryRequest(Guid FiscalPeriodId, string EntryNumber, DateOnly EntryDate, string Description, string CurrencyCode, IReadOnlyList<CreateJournalLineRequest> Lines);
    public sealed record CreateJournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Description = null);
    public sealed record UpdateJournalEntryRequest(Guid FiscalPeriodId, string EntryNumber, DateOnly EntryDate, string Description, string CurrencyCode, IReadOnlyList<CreateJournalLineRequest> Lines);
    public sealed record JournalEntryListDto(Guid Id, string EntryNumber, DateOnly EntryDate, string Description, string CurrencyCode, string Status, decimal TotalDebit, decimal TotalCredit, int LineCount);
    public sealed record JournalEntryLineDto(Guid Id, int LineNumber, Guid AccountId, string AccountCode, string AccountName, string Description, decimal Debit, decimal Credit);
    public sealed record JournalEntryDto(Guid Id, Guid CompanyId, Guid FiscalPeriodId, Guid? ReversalOfEntryId, string EntryNumber, DateOnly EntryDate, string Description, string CurrencyCode, string Status, DateTime CreatedAt, DateTime? PostedAt, IReadOnlyList<JournalEntryLineDto> Lines, decimal TotalDebit, decimal TotalCredit);
    public sealed record ReverseJournalEntryRequest(Guid FiscalPeriodId, string EntryNumber, DateOnly EntryDate, string Description);
    public sealed record TrialBalanceRowDto(Guid AccountId, string AccountCode, string AccountNameAr, string AccountNameEn, string AccountType, decimal DebitMovement, decimal CreditMovement, decimal DebitBalance, decimal CreditBalance);
    public sealed record TrialBalanceDto(Guid CompanyId, DateOnly FromDate, DateOnly ToDate, IReadOnlyList<TrialBalanceRowDto> Rows, decimal TotalDebitMovement, decimal TotalCreditMovement, decimal TotalDebitBalance, decimal TotalCreditBalance);
    public sealed record GeneralLedgerLineDto(Guid JournalEntryId, string EntryNumber, DateOnly EntryDate, string EntryDescription, string CurrencyCode, int LineNumber, string LineDescription, decimal Debit, decimal Credit, decimal RunningBalance);
    public sealed record GeneralLedgerDto(Guid CompanyId, Guid AccountId, string AccountCode, string AccountNameAr, DateOnly FromDate, DateOnly ToDate, decimal OpeningBalance, IReadOnlyList<GeneralLedgerLineDto> Lines, decimal ClosingBalance, decimal TotalDebit, decimal TotalCredit);
}
