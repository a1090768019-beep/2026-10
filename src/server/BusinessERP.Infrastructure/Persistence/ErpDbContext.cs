using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Infrastructure.Persistence;

public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<TenantMembershipRecord> TenantMemberships => Set<TenantMembershipRecord>();
    public DbSet<SubscriptionPlanRecord> SubscriptionPlans => Set<SubscriptionPlanRecord>();
    public DbSet<TenantSubscriptionRecord> TenantSubscriptions => Set<TenantSubscriptionRecord>();
    public DbSet<CompanyRecord> Companies => Set<CompanyRecord>();
    public DbSet<AccountTemplateRecord> AccountTemplates => Set<AccountTemplateRecord>();
    public DbSet<AccountTemplateAccountRecord> AccountTemplateAccounts => Set<AccountTemplateAccountRecord>();
    public DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    public DbSet<FiscalYearRecord> FiscalYears => Set<FiscalYearRecord>();
    public DbSet<FiscalPeriodRecord> FiscalPeriods => Set<FiscalPeriodRecord>();
    public DbSet<JournalEntryRecord> JournalEntries => Set<JournalEntryRecord>();
    public DbSet<JournalEntryLineRecord> JournalEntryLines => Set<JournalEntryLineRecord>();
    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRecord>(e =>
        {
            e.ToTable("tenants"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(80).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasIndex(x => x.Slug).IsUnique();
        });
        modelBuilder.Entity<UserRecord>(e =>
        {
            e.ToTable("users"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OidcSubject).HasColumnName("oidc_subject").HasMaxLength(300).IsRequired();
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.Property(x => x.DisabledAt).HasColumnName("disabled_at").HasColumnType("timestamp with time zone");
            e.HasIndex(x => x.OidcSubject).IsUnique();
        });
        modelBuilder.Entity<TenantMembershipRecord>(e =>
        {
            e.ToTable("tenant_memberships"); e.HasKey(x => new { x.TenantId, x.UserId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
            e.Property(x => x.RoleCode).HasColumnName("role_code").HasMaxLength(24).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasOne<TenantRecord>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserRecord>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SubscriptionPlanRecord>(e =>
        {
            e.ToTable("subscription_plans"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(120).IsRequired();
            e.Property(x => x.BillingInterval).HasColumnName("billing_interval").HasMaxLength(12).IsRequired();
            e.Property(x => x.Price).HasColumnName("price").HasPrecision(20, 4);
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").IsRequired();
            e.Property(x => x.LimitsJson).HasColumnName("limits_json").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<TenantSubscriptionRecord>(e =>
        {
            e.ToTable("tenant_subscriptions"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SubscriptionPlanId).HasColumnName("subscription_plan_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(24).IsRequired();
            e.Property(x => x.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.ExternalCustomerId).HasColumnName("external_customer_id").HasMaxLength(200);
            e.Property(x => x.ExternalSubscriptionId).HasColumnName("external_subscription_id").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasOne<TenantRecord>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SubscriptionPlanRecord>().WithMany().HasForeignKey(x => x.SubscriptionPlanId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AccountTemplateRecord>(e =>
        {
            e.ToTable("account_templates"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.IndustryCode).HasColumnName("industry_code").HasMaxLength(64).IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
            e.Property(x => x.DescriptionAr).HasColumnName("description_ar").HasMaxLength(1000).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.HasIndex(x => x.Code).IsUnique();
            e.HasMany(x => x.Accounts).WithOne(x => x.Template).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AccountTemplateAccountRecord>(e =>
        {
            e.ToTable("account_template_accounts"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TemplateId).HasColumnName("template_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.ParentCode).HasColumnName("parent_code").HasMaxLength(64);
            e.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
            e.Property(x => x.AccountType).HasColumnName("account_type").HasMaxLength(24).IsRequired();
            e.Property(x => x.NormalBalance).HasColumnName("normal_balance").HasMaxLength(6).IsRequired();
            e.Property(x => x.AllowPosting).HasColumnName("allow_posting");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.HasIndex(x => new { x.TemplateId, x.Code }).IsUnique();
        });
        modelBuilder.Entity<CompanyRecord>(e =>
        {
            e.ToTable("companies"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.BaseCurrency).HasColumnName("base_currency").HasColumnType("char(3)");
            e.Property(x => x.TaxNumber).HasColumnName("tax_number").HasMaxLength(32);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasAlternateKey(x => new { x.TenantId, x.Id });
            e.HasOne<TenantRecord>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AccountRecord>(e =>
        {
            e.ToTable("accounts"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CompanyId).HasColumnName("company_id"); e.Property(x => x.ParentAccountId).HasColumnName("parent_account_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(200).IsRequired();
            e.Property(x => x.NameEn).HasColumnName("name_en").HasMaxLength(200).IsRequired();
            e.Property(x => x.AccountType).HasColumnName("account_type").HasMaxLength(24).IsRequired();
            e.Property(x => x.NormalBalance).HasColumnName("normal_balance").HasMaxLength(6).IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.AllowPosting).HasColumnName("allow_posting");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.Code }).IsUnique();
            e.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ParentAccountId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FiscalYearRecord>(e =>
        {
            e.ToTable("fiscal_years"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id"); e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            e.Property(x => x.StartDate).HasColumnName("start_date"); e.Property(x => x.EndDate).HasColumnName("end_date"); e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            e.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FiscalPeriodRecord>(e =>
        {
            e.ToTable("fiscal_periods"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id"); e.Property(x => x.CompanyId).HasColumnName("company_id"); e.Property(x => x.FiscalYearId).HasColumnName("fiscal_year_id");
            e.Property(x => x.PeriodNumber).HasColumnName("period_number"); e.Property(x => x.StartDate).HasColumnName("start_date"); e.Property(x => x.EndDate).HasColumnName("end_date"); e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            e.HasOne<FiscalYearRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.FiscalYearId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<JournalEntryRecord>(e =>
        {
            e.ToTable("journal_entries"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id"); e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.FiscalPeriodId).HasColumnName("fiscal_period_id"); e.Property(x => x.ReversalOfEntryId).HasColumnName("reversal_of_entry_id");
            e.Property(x => x.EntryNumber).HasColumnName("entry_number").HasMaxLength(80).IsRequired(); e.Property(x => x.EntryDate).HasColumnName("entry_date");
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000).IsRequired(); e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasColumnType("char(3)");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired(); e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200); e.Property(x => x.IdempotencyRequestHash).HasColumnName("idempotency_request_hash").HasMaxLength(64);
            e.Property(x => x.SourceType).HasColumnName("source_type").HasMaxLength(64); e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()"); e.Property(x => x.PostedAt).HasColumnName("posted_at").HasColumnType("timestamp with time zone");
            e.HasAlternateKey(x => new { x.TenantId, x.CompanyId, x.Id });
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.EntryNumber }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.ReversalOfEntryId }).IsUnique().HasFilter("reversal_of_entry_id IS NOT NULL");
            e.HasOne<CompanyRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FiscalPeriodRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.FiscalPeriodId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.ReversalOfEntryId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<JournalEntryLineRecord>(e =>
        {
            e.ToTable("journal_entry_lines"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id"); e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.JournalEntryId).HasColumnName("journal_entry_id"); e.Property(x => x.AccountId).HasColumnName("account_id"); e.Property(x => x.LineNumber).HasColumnName("line_number");
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000).IsRequired(); e.Property(x => x.Debit).HasColumnName("debit").HasPrecision(20, 4); e.Property(x => x.Credit).HasColumnName("credit").HasPrecision(20, 4);
            e.HasOne<JournalEntryRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.JournalEntryId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.AccountId }).HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.CompanyId, x.JournalEntryId, x.LineNumber }).IsUnique();
        });
        modelBuilder.Entity<AuditLogRecord>(e =>
        {
            e.ToTable("audit_logs"); e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id"); e.Property(x => x.TenantId).HasColumnName("tenant_id"); e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(100).IsRequired(); e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id").HasMaxLength(200); e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            e.Property(x => x.DetailsJson).HasColumnName("details_json").HasColumnType("jsonb").IsRequired(); e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
            e.HasOne<TenantRecord>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });
    }
}
