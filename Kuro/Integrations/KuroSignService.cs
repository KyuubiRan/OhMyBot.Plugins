using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Kuro;

public sealed class KuroSignService(
    KuroCommunitySignModule community,
    KuroGameSignModule game,
    SignInExecutor executor)
{
    public Task<KuroBbsSignResult> ExecuteBbsSignAsync(
        KuroAccount account, long taskFlags, IReadOnlySet<string>? requestedActions = null,
        bool runAllWhenNoRequestedActions = false, CancellationToken cancellationToken = default)
        => community.ExecuteAsync(account, taskFlags, requestedActions, runAllWhenNoRequestedActions, cancellationToken);

    public Task<KuroGameSignResult> ExecuteGameSignAsync(
        KuroAccount account, IEnumerable<long>? requestedGames = null,
        bool onlyEnabledAutoSign = false, bool includeMissingConfigMessage = false,
        CancellationToken cancellationToken = default)
        => game.ExecuteAsync(account, requestedGames, onlyEnabledAutoSign, includeMissingConfigMessage, cancellationToken);

    public async Task<KuroAutoSignResult> ExecuteAutoSignAsync(KuroAccount account, CancellationToken cancellationToken = default)
    {
        var modules = new List<SignInModule>();
        if (account.BbsTaskFlags != 0)
        {
            modules.Add(new("社区签到", async token =>
                (await community.ExecuteAsync(account, account.BbsTaskFlags, cancellationToken: token)).Lines));
        }

        modules.Add(new("游戏签到", async token =>
            (await game.ExecuteAsync(account, onlyEnabledAutoSign: true, cancellationToken: token)).Lines));
        var lines = await executor.RunModulesAsync(modules, cancellationToken);
        return new KuroAutoSignResult(account, lines);
    }
}

public sealed record KuroBbsTaskProgressSnapshot(string Remark, int CompleteTimes, int NeedActionTimes, int GainGold, bool Finished);

public sealed record KuroBbsSignResult(KuroAccount Account, IReadOnlyList<KuroBbsTaskProgressSnapshot> Progress, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed record KuroGameSignResult(KuroAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed record KuroAutoSignResult(KuroAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed class KuroTokenExpiredException(long accountId) : Exception("Token 已失效，请重新绑定库街区账号")
{
    public long AccountId { get; } = accountId;
}
