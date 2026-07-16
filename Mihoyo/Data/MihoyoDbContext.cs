using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Infrastructure.Data.Entities;

namespace OhMyBot.Plugins.Mihoyo.Data;

public sealed class MihoyoDbContext(DbContextOptions<MihoyoDbContext> options) : DbContext(options)
{
    public DbSet<MihoyoAccount> MihoyoAccounts => Set<MihoyoAccount>();
    public DbSet<MihoyoGameRole> MihoyoGameRoles => Set<MihoyoGameRole>();
    public DbSet<PluginCoreUser> CoreUsers => Set<PluginCoreUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PluginCoreUser>(builder =>
        {
            builder.ToTable("CoreUsers", table => table.ExcludeFromMigrations());
            builder.HasKey(user => user.Id);
            builder.Property(user => user.Privilege);
        });
        modelBuilder.Entity<MihoyoAccount>(builder =>
        {
            builder.ToTable("MihoyoAccounts");
            builder.HasKey(account => account.Id);
            builder.Property(account => account.Region);
            builder.Property(account => account.DisplayName).HasMaxLength(256).IsRequired();
            builder.Property(account => account.CookieCiphertext).HasMaxLength(4096).IsRequired();
            builder.Property(account => account.StokenCiphertext).HasMaxLength(2048).IsRequired();
            builder.Property(account => account.Mid).HasMaxLength(255).IsRequired();
            builder.Property(account => account.CreatedAt).IsRequired();
            builder.Property(account => account.UpdatedAt).IsRequired();
            builder.Property(account => account.GameSignSelection).HasDefaultValue(string.Empty);
            builder.HasIndex(account => new { account.Region, account.Stuid }).IsUnique();
            builder.HasOne<PluginCoreUser>().WithMany().HasForeignKey(account => account.CoreUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<MihoyoGameRole>(builder =>
        {
            builder.ToTable("MihoyoGameRoles");
            builder.HasKey(role => role.Id);
            builder.Property(role => role.GameBiz).HasMaxLength(64).IsRequired();
            builder.Property(role => role.GameName).HasMaxLength(128).IsRequired();
            builder.Property(role => role.Region).HasMaxLength(128).IsRequired();
            builder.Property(role => role.Nickname).HasMaxLength(256).IsRequired();
            builder.Property(role => role.Level).HasMaxLength(64).IsRequired();
            builder.Property(role => role.CreatedAt).IsRequired();
            builder.Property(role => role.UpdatedAt).IsRequired();
            builder.HasIndex(role => new { role.MihoyoAccountId, role.GameBiz, role.GameUid }).IsUnique();
            builder.HasOne(role => role.MihoyoAccount).WithMany(account => account.Roles).HasForeignKey(role => role.MihoyoAccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class PluginCoreUser { public long Id { get; set; } public UserPrivilege Privilege { get; set; } }

public sealed class MihoyoDbContextFactory : IDesignTimeDbContextFactory<MihoyoDbContext>
{
    public MihoyoDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<MihoyoDbContext>()
            .UseNpgsql("Host=localhost;Database=ohmybot_v2;Username=ohmybot;Password=ohmybot",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Mihoyo"))
            .Options);
}
