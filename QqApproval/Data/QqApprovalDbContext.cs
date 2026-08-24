using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OhMyBot.Plugins.QqApproval.Data.Entities;

namespace OhMyBot.Plugins.QqApproval.Data;

public sealed class QqApprovalDbContext(DbContextOptions<QqApprovalDbContext> options) : DbContext(options)
{
    public DbSet<QqApprovalRequest> QqApprovalRequests => Set<QqApprovalRequest>();
    public DbSet<QqApprovalRule> QqApprovalRules => Set<QqApprovalRule>();
    public DbSet<QqApprovalListenerSetting> QqApprovalListenerSettings => Set<QqApprovalListenerSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QqApprovalRequest>(builder =>
        {
            builder.ToTable("QqApprovalRequests");
            builder.HasKey(request => request.Id);
            builder.Property(request => request.Flag).HasMaxLength(256).IsRequired();
            builder.Property(request => request.BotInstanceId).HasMaxLength(64).IsRequired();
            builder.Property(request => request.RequesterId).HasMaxLength(32).IsRequired();
            builder.Property(request => request.RequesterName).HasMaxLength(256).IsRequired();
            builder.Property(request => request.GroupId).HasMaxLength(32).IsRequired();
            builder.Property(request => request.Comment).HasMaxLength(1024).IsRequired();
            builder.Property(request => request.RequesterProfileJson).HasMaxLength(2048).HasDefaultValue(string.Empty);
            builder.Property(request => request.DecidedReason).HasMaxLength(256).IsRequired();
            builder.Property(request => request.OccurredAt).IsRequired();
            builder.Property(request => request.CreatedAt).IsRequired();
            // 待审列表按状态过滤、按时间倒序，是唯一的高频查询。
            builder.HasIndex(request => new { request.Status, request.CreatedAt });
            // 同一条 QQ 请求可能被重复上报（网关重连后 NapCat 会重推），flag 唯一即可幂等。
            builder.HasIndex(request => request.Flag).IsUnique();
        });

        modelBuilder.Entity<QqApprovalRule>(builder =>
        {
            builder.ToTable("QqApprovalRules");
            builder.HasKey(rule => rule.Id);
            builder.Property(rule => rule.Value).HasMaxLength(32).IsRequired();
            builder.Property(rule => rule.Note).HasMaxLength(256).IsRequired();
            builder.Property(rule => rule.CreatedAt).IsRequired();
            builder.HasIndex(rule => new { rule.Kind, rule.Scope, rule.Value }).IsUnique();
        });

        modelBuilder.Entity<QqApprovalListenerSetting>(builder =>
        {
            builder.ToTable("QqApprovalListenerSettings");
            builder.HasKey(setting => setting.Kind);
            builder.Property(setting => setting.Kind).ValueGeneratedNever();
            builder.Property(setting => setting.UpdatedAt).IsRequired();
        });
    }
}

public sealed class QqApprovalDbContextFactory : IDesignTimeDbContextFactory<QqApprovalDbContext>
{
    public QqApprovalDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<QqApprovalDbContext>()
            .UseNpgsql("Host=localhost;Database=ohmybot_v2;Username=ohmybot;Password=ohmybot",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_QqApproval"))
            .Options);
}
