using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Infrastructure.Data.Entities;

namespace OhMyBot.Plugins.Skland.Data;

public sealed class SklandDbContext(DbContextOptions<SklandDbContext> options) : DbContext(options)
{
    public DbSet<SklandAccount> SklandAccounts => Set<SklandAccount>();
    public DbSet<SklandGameRole> SklandGameRoles => Set<SklandGameRole>();
    public DbSet<PluginCoreUser> CoreUsers => Set<PluginCoreUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PluginCoreUser>(builder =>
        {
            builder.ToTable("CoreUsers", table => table.ExcludeFromMigrations());
            builder.HasKey(user => user.Id);
            builder.Property(user => user.Privilege);
        });
        modelBuilder.Entity<SklandAccount>(builder =>
        {
            builder.ToTable("SklandAccounts");
            builder.HasKey(account => account.Id);
            builder.Property(account => account.SklandUserId).HasMaxLength(128).IsRequired();
            builder.Property(account => account.DeviceId).HasMaxLength(256).IsRequired();
            builder.Property(account => account.DisplayName).HasMaxLength(256).IsRequired();
            builder.Property(account => account.HgTokenCiphertext).HasMaxLength(2048).IsRequired();
            builder.Property(account => account.CredCiphertext).HasMaxLength(2048).IsRequired();
            builder.Property(account => account.SignTokenCiphertext).HasMaxLength(2048).IsRequired();
            builder.Property(account => account.CreatedAt).IsRequired();
            builder.Property(account => account.UpdatedAt).IsRequired();
            builder.Property(account => account.GameSignSelection).HasDefaultValue(string.Empty);
            builder.HasIndex(account => account.SklandUserId).IsUnique();
            builder.HasOne<PluginCoreUser>().WithMany().HasForeignKey(account => account.CoreUserId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SklandGameRole>(builder =>
        {
            builder.ToTable("SklandGameRoles");
            builder.HasKey(role => role.Id);
            builder.Property(role => role.AppCode).HasMaxLength(64).IsRequired();
            builder.Property(role => role.GameName).HasMaxLength(128).IsRequired();
            builder.Property(role => role.Uid).HasMaxLength(128).IsRequired();
            builder.Property(role => role.NickName).HasMaxLength(256).IsRequired();
            builder.Property(role => role.Level).HasMaxLength(64).IsRequired();
            builder.Property(role => role.ChannelName).HasMaxLength(128).IsRequired();
            builder.Property(role => role.ServerId).HasMaxLength(128).IsRequired();
            builder.Property(role => role.RoleId).HasMaxLength(128).IsRequired();
            builder.Property(role => role.CreatedAt).IsRequired();
            builder.Property(role => role.UpdatedAt).IsRequired();
            builder.HasIndex(role => new { role.SklandAccountId, role.GameId, role.Uid, role.RoleId }).IsUnique();
            builder.HasOne(role => role.SklandAccount).WithMany(account => account.Roles).HasForeignKey(role => role.SklandAccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class PluginCoreUser { public long Id { get; set; } public UserPrivilege Privilege { get; set; } }

public sealed class SklandDbContextFactory : IDesignTimeDbContextFactory<SklandDbContext>
{
    public SklandDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SklandDbContext>()
            .UseNpgsql("Host=localhost;Database=ohmybot_v2;Username=ohmybot;Password=ohmybot",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Skland"))
            .Options);
}
