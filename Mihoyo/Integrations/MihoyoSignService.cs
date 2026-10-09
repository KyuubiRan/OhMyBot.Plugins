using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Mihoyo;

public sealed class MihoyoSignService(
    MihoyoCommunitySignModule communityModule,
    MihoyoGameSignModule gameModule,
    SignInExecutor executor)
{
    public async Task<MihoyoBbsSignResult> ExecuteBbsSignAsync(
        MihoyoAccount account,
        long taskFlags,
        IReadOnlySet<string>? requestedActions = null,
        bool runAllWhenNoRequestedActions = false,
        CancellationToken cancellationToken = default)
    {
        var lines = await executor.RunModuleAsync("社区签到", async token =>
            (await communityModule.ExecuteAsync(account, taskFlags, requestedActions, runAllWhenNoRequestedActions, token)).Lines,
            cancellationToken);
        return new MihoyoBbsSignResult(account, lines);
    }

    public async Task<MihoyoGameSignResult> ExecuteGameSignAsync(
        MihoyoAccount account,
        IEnumerable<string>? requestedGameKeys = null,
        bool onlyEnabledAutoSign = false,
        bool includeMissingConfigMessage = false,
        CancellationToken cancellationToken = default)
    {
        var lines = await executor.RunModuleAsync("游戏签到", async token =>
            (await gameModule.ExecuteAsync(account, requestedGameKeys, onlyEnabledAutoSign, includeMissingConfigMessage, token)).Lines,
            cancellationToken);
        return new MihoyoGameSignResult(account, lines);
    }

    public async Task<MihoyoAutoSignResult> ExecuteAutoSignAsync(MihoyoAccount account, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        if (account.Region == MihoyoRegion.Cn && account.BbsTaskFlags != 0)
        {
            var community = await ExecuteBbsSignAsync(account, account.BbsTaskFlags, cancellationToken: cancellationToken);
            lines.AddRange(community.Lines);
        }

        var game = await ExecuteGameSignAsync(account, onlyEnabledAutoSign: true, cancellationToken: cancellationToken);
        lines.AddRange(game.Lines);
        return new MihoyoAutoSignResult(account, lines);
    }
}

public sealed record MihoyoBbsSignResult(MihoyoAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed record MihoyoGameSignResult(MihoyoAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed record MihoyoAutoSignResult(MihoyoAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}
