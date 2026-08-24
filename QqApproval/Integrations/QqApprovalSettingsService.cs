using Microsoft.EntityFrameworkCore;
using OhMyBot.Contracts.Grpc;
using OhMyBot.Plugins.QqApproval.Data.Entities;

namespace OhMyBot.Plugins.QqApproval.Integrations;

/// <summary>
/// 各请求类型的自动规则开关。表里没有行时回落到
/// <see cref="QqApprovalListenerSetting.DefaultRulesEnabled"/>，写入时才建行。
/// </summary>
public sealed class QqApprovalSettingsService(QqApprovalDbContext dbContext, TimeProvider timeProvider)
{
    public static readonly PlatformRequestKind[] SupportedKinds =
    [
        PlatformRequestKind.FriendAdd,
        PlatformRequestKind.GroupInvite,
        PlatformRequestKind.GroupAdd
    ];

    public async Task<QqApprovalListenerSetting> GetAsync(
        PlatformRequestKind kind,
        CancellationToken cancellationToken = default)
    {
        var stored = await dbContext.QqApprovalListenerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.Kind == kind, cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        return new QqApprovalListenerSetting
        {
            Kind = kind,
            RulesEnabled = QqApprovalListenerSetting.DefaultRulesEnabled,
            UpdatedAt = timeProvider.GetUtcNow()
        };
    }

    public async Task<IReadOnlyList<QqApprovalListenerSetting>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<QqApprovalListenerSetting>(SupportedKinds.Length);
        foreach (var kind in SupportedKinds)
        {
            result.Add(await GetAsync(kind, cancellationToken));
        }

        return result;
    }

    public async Task SetRulesEnabledAsync(PlatformRequestKind kind, bool enabled, CancellationToken cancellationToken = default)
    {
        await UpdateAsync(kind, setting => setting.RulesEnabled = enabled, cancellationToken);
    }

    private async Task UpdateAsync(
        PlatformRequestKind kind,
        Action<QqApprovalListenerSetting> mutate,
        CancellationToken cancellationToken)
    {
        var stored = await dbContext.QqApprovalListenerSettings
            .FirstOrDefaultAsync(setting => setting.Kind == kind, cancellationToken);
        if (stored is null)
        {
            stored = new QqApprovalListenerSetting
            {
                Kind = kind,
                RulesEnabled = QqApprovalListenerSetting.DefaultRulesEnabled
            };
            dbContext.QqApprovalListenerSettings.Add(stored);
        }

        mutate(stored);
        stored.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- 规则 ----

    public Task<List<QqApprovalRule>> ListRulesAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.QqApprovalRules
            .AsNoTracking()
            .OrderBy(rule => rule.Kind)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);
    }

    /// <returns>新增或更新后的规则。</returns>
    public async Task<QqApprovalRule> UpsertRuleAsync(
        PlatformRequestKind kind,
        QqApprovalRuleScope scope,
        string value,
        QqApprovalRuleAction action,
        string note,
        CancellationToken cancellationToken = default)
    {
        var rule = await dbContext.QqApprovalRules
            .FirstOrDefaultAsync(
                item => item.Kind == kind && item.Scope == scope && item.Value == value,
                cancellationToken);
        if (rule is null)
        {
            rule = new QqApprovalRule
            {
                Kind = kind,
                Scope = scope,
                Value = value,
                CreatedAt = timeProvider.GetUtcNow()
            };
            dbContext.QqApprovalRules.Add(rule);
        }

        rule.Action = action;
        rule.Note = note;
        await dbContext.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
    {
        var deleted = await dbContext.QqApprovalRules
            .Where(rule => rule.Id == ruleId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public static bool TryParseKind(string value, out PlatformRequestKind kind)
    {
        var parsed = value.Trim().ToLowerInvariant() switch
        {
            "friend" or "好友" => PlatformRequestKind.FriendAdd,
            "invite" or "邀请" => PlatformRequestKind.GroupInvite,
            "groupadd" or "add" or "入群" => PlatformRequestKind.GroupAdd,
            _ => (PlatformRequestKind?)null
        };

        kind = parsed ?? default;
        return parsed.HasValue;
    }

    public static string FormatKindKey(PlatformRequestKind kind) => kind switch
    {
        PlatformRequestKind.FriendAdd => "friend",
        PlatformRequestKind.GroupInvite => "invite",
        PlatformRequestKind.GroupAdd => "groupadd",
        _ => kind.ToString()
    };

    public static bool TryParseScope(string value, out QqApprovalRuleScope scope)
    {
        var parsed = value.Trim().ToLowerInvariant() switch
        {
            "user" or "qq" or "用户" => QqApprovalRuleScope.Requester,
            "group" or "群" => QqApprovalRuleScope.Group,
            _ => (QqApprovalRuleScope?)null
        };

        scope = parsed ?? default;
        return parsed.HasValue;
    }
}
