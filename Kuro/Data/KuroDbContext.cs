using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Infrastructure.Data.Entities;

namespace OhMyBot.Plugins.Kuro.Data;

public sealed class KuroDbContext(DbContextOptions<KuroDbContext> options) : DbContext(options)
{
    public DbSet<KuroAccount> KuroAccounts => Set<KuroAccount>();
    public DbSet<KuroGameRole> KuroGameRoles => Set<KuroGameRole>();
    public DbSet<PluginCoreUser> CoreUsers => Set<PluginCoreUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PluginCoreUser>(builder =>
        {
            builder.ToTable("CoreUsers", table => table.ExcludeFromMigrations());
            builder.HasKey(user => user.Id);
            builder.Property(user => user.Privilege);
        });
        modelBuilder.Entity<KuroAccount>(builder =>
        {
            builder.ToTable("KuroAccounts");
            builder.HasKey(account => account.Id);
            builder.Property(account => account.DisplayName).HasMaxLength(256).IsRequired();
            builder.Property(account => account.TokenCiphertext).HasMaxLength(2048).IsRequired();
            builder.Property(account => account.DevCode).HasMaxLength(255);
            builder.Property(account => account.DistinctId).HasMaxLength(255);
            builder.Property(account => account.CreatedAt).IsRequired();
            builder.Property(account => account.UpdatedAt).IsRequired();
            builder.Property(account => account.GameSignSelection).HasDefaultValue(string.Empty);
            builder.HasIndex(account => account.BbsUserId).IsUnique();
            builder.HasOne<PluginCoreUser>().WithMany().HasForeignKey(account => account.CoreUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<KuroGameRole>(builder =>
        {
            builder.ToTable("KuroGameRoles");
            builder.HasKey(role => role.Id);
            builder.Property(role => role.GameName).HasMaxLength(128).IsRequired();
            builder.Property(role => role.ServerId).HasMaxLength(128).IsRequired();
            builder.Property(role => role.ServerName).HasMaxLength(128).IsRequired();
            builder.Property(role => role.RoleName).HasMaxLength(256).IsRequired();
            builder.Property(role => role.GameLevel).HasMaxLength(64).IsRequired();
            builder.Property(role => role.CreatedAt).IsRequired();
            builder.Property(role => role.UpdatedAt).IsRequired();
            builder.HasIndex(role => new { role.KuroAccountId, role.GameId, role.ServerId, role.RoleId }).IsUnique();
            builder.HasOne(role => role.KuroAccount).WithMany(account => account.Roles).HasForeignKey(role => role.KuroAccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class PluginCoreUser { public long Id { get; set; } public UserPrivilege Privilege { get; set; } }

public sealed class KuroDbContextFactory : IDesignTimeDbContextFactory<KuroDbContext>
{
    public KuroDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<KuroDbContext>()
            .UseNpgsql("Host=localhost;Database=ohmybot_v2;Username=ohmybot;Password=ohmybot",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Kuro"))
            .Options);
}
