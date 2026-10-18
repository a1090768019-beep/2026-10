namespace BusinessERP.Infrastructure.Persistence;

public sealed class TenantRecord
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTime CreatedAt { get; set; }
}

public sealed class UserRecord
{
    public Guid Id { get; set; }
    public string OidcSubject { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? DisabledAt { get; set; }
}

public sealed class TenantMembershipRecord
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Status { get; set; } = "ACTIVE";
    public string RoleCode { get; set; } = "ACCOUNTANT";
    public DateTime CreatedAt { get; set; }
}


public sealed class SubscriptionPlanRecord
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string BillingInterval { get; set; } = "MONTHLY";
    public decimal Price { get; set; }
    public string Currency { get; set; } = "SAR";
    public string LimitsJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public sealed class TenantSubscriptionRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SubscriptionPlanId { get; set; }
    public string Status { get; set; } = "TRIALING";
    public DateTime StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public string? ExternalCustomerId { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AccountTemplateRecord
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string IndustryCode { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string DescriptionAr { get; set; } = "";
    public bool IsActive { get; set; }
    public List<AccountTemplateAccountRecord> Accounts { get; set; } = [];
}

public sealed class AccountTemplateAccountRecord
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string Code { get; set; } = "";
    public string? ParentCode { get; set; }
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string NormalBalance { get; set; } = "DEBIT";
    public bool AllowPosting { get; set; }
    public int SortOrder { get; set; }
    public AccountTemplateRecord Template { get; set; } = null!;
}

public sealed class CompanyRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string BaseCurrency { get; set; } = "SAR";
    public string? TaxNumber { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AccountRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? ParentAccountId { get; set; }
    public string Code { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string NormalBalance { get; set; } = "DEBIT";
    public bool IsActive { get; set; } = true;
    public bool AllowPosting { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public sealed class FiscalYearRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "OPEN";
}

public sealed class FiscalPeriodRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FiscalYearId { get; set; }
    public int PeriodNumber { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Status { get; set; } = "OPEN";
}

public sealed class JournalEntryRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public Guid? ReversalOfEntryId { get; set; }
    public string EntryNumber { get; set; } = "";
    public DateOnly EntryDate { get; set; }
    public string Description { get; set; } = "";
    public string CurrencyCode { get; set; } = "SAR";
    public string Status { get; set; } = "DRAFT";
    public string? IdempotencyKey { get; set; }
    public string? IdempotencyRequestHash { get; set; }
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PostedAt { get; set; }
}

public sealed class JournalEntryLineRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public sealed class AuditLogRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? IpAddress { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
