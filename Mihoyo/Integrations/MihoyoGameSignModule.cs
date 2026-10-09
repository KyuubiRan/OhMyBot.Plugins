using System.Globalization;
using Microsoft.Extensions.Logging;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Mihoyo;

public sealed class MihoyoGameSignModule(
    MihoyoHttpClient client,
    MihoyoAccountService accountService,
    MihoyoSignCredentialService credentialService,
    SignInExecutor executor,
    ILogger<MihoyoGameSignModule> logger,
    TimeProvider? timeProvider = null)
{
    public async Task<MihoyoGameSignResult> ExecuteAsync(
        MihoyoAccount account,
        IEnumerable<string>? requestedGameKeys = null,
        bool onlyEnabledAutoSign = false,
        bool includeMissingConfigMessage = false,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var roles = ResolveGameTargets(account, requestedGameKeys, onlyEnabledAutoSign, includeMissingConfigMessage, lines).ToArray();
        if (roles.Length == 0)
        {
            return new MihoyoGameSignResult(account, lines);
        }

        var credential = accountService.GetCredential(account);
        var cookie = credential.Cookie;

        foreach (var role in roles)
        {
            var game = MihoyoGameCatalog.FindByGameBiz(role.GameBiz);
            if (game is null)
            {
                continue;
            }

            lines.Add($"[{role.GameName}]{(role.GameUid > 0 ? " " + role.Nickname + " (" + role.GameUid + ")" : string.Empty)}");
            var roleLines = new List<string>();
            var failures = await executor.RunModuleAsync($"{role.GameName}签到", async token =>
            {
                credential = accountService.GetCredential(account);
                cookie = credential.Cookie;
                if (account.Region == MihoyoRegion.Cn)
                {
                    cookie = await SignCnGameAsync(account, credential, cookie, game, role, roleLines, token);
                }
                else
                {
                    await SignOsGameAsync(credential, game, roleLines, token);
                }

                return Array.Empty<string>();
            }, cancellationToken);
            lines.AddRange(roleLines);
            lines.AddRange(failures);

            await DelayAsync(2000, 5000, cancellationToken);
        }

        return new MihoyoGameSignResult(account, lines);
    }

    private async Task<string> SignCnGameAsync(
        MihoyoAccount account,
        MihoyoCredential credential,
        string cookie,
        MihoyoGameDef game,
        MihoyoGameRole role,
        ICollection<string> lines,
        CancellationToken cancellationToken)
    {
        var uid = role.GameUid.ToString(CultureInfo.InvariantCulture);
        var info = await client.GetCnGameInfoAsync(cookie, game, role.Region, uid, cancellationToken);
        if (info.Retcode == MihoyoHttpClient.CookieExpiredCode)
        {
            cookie = await credentialService.RefreshCnCookieAsync(account, credential, cancellationToken);
            info = await client.GetCnGameInfoAsync(cookie, game, role.Region, uid, cancellationToken);
        }

        if (!info.Ok || info.Data is not { } infoData)
        {
            lines.Add("获取签到信息失败：" + info.Message);
            return cookie;
        }

        if (infoData.FirstBind)
        {
            lines.Add("首次绑定米游社，请先手动签到一次");
            return cookie;
        }

        var awards = await GetAwardsAsync(token => client.GetCnGameHomeAsync(cookie, game, token), lines, cancellationToken);

        if (infoData.IsSign)
        {
            lines.Add("签到结果：今日已签到");
            lines.Add($"连续签到：{infoData.TotalSignDay} 天");
            lines.Add("今日奖励：" + GetAward(awards, infoData.TotalSignDay - 1));
            return cookie;
        }

        await DelayAsync(2000, 5000, cancellationToken);
        var sign = await client.CnGameSignAsync(cookie, game, role.Region, uid, cancellationToken);
        if (sign.Retcode == MihoyoHttpClient.AlreadySignedCode)
        {
            lines.Add("签到结果：今日已签到");
            lines.Add($"连续签到：{infoData.TotalSignDay} 天");
            return cookie;
        }

        if (sign.Ok && sign.Data is { Success: 1 })
        {
            lines.Add("签到结果：触发验证码，已跳过（不支持自动打码）");
            return cookie;
        }

        if (!sign.Ok)
        {
            lines.Add("签到结果：失败：" + sign.Message);
            return cookie;
        }

        lines.Add("签到结果：成功");
        lines.Add($"连续签到：{infoData.TotalSignDay + 1} 天");
        lines.Add("今日奖励：" + GetAward(awards, infoData.TotalSignDay));
        logger.LogInformation("Mihoyo CN game sign success for account {AccountId}, game {Game}.", account.Id, game.Key);
        return cookie;
    }

    private async Task SignOsGameAsync(
        MihoyoCredential credential,
        MihoyoGameDef game,
        ICollection<string> lines,
        CancellationToken cancellationToken)
    {
        var info = await client.GetOsGameInfoAsync(credential.Cookie, game, cancellationToken);
        if (!info.Ok || info.Data is not { } infoData)
        {
            lines.Add("获取签到信息失败：" + info.Message);
            return;
        }

        if (infoData.FirstBind)
        {
            lines.Add("首次绑定，请先在 HoYoLAB 手动签到一次");
            return;
        }

        var awards = await GetAwardsAsync(token => client.GetOsGameHomeAsync(credential.Cookie, game, token), lines, cancellationToken);

        if (infoData.IsSign)
        {
            lines.Add("签到结果：今日已签到");
            lines.Add($"连续签到：{infoData.TotalSignDay} 天");
            lines.Add("今日奖励：" + GetAward(awards, infoData.TotalSignDay - 1));
            return;
        }

        await DelayAsync(2000, 6000, cancellationToken);
        var sign = await client.OsGameSignAsync(credential.Cookie, game, cancellationToken);
        if (sign.Retcode == MihoyoHttpClient.AlreadySignedCode)
        {
            lines.Add("签到结果：今日已签到");
            return;
        }

        if (!sign.Ok)
        {
            lines.Add("签到结果：失败：" + sign.Message);
            return;
        }

        lines.Add("签到结果：成功");
        lines.Add($"连续签到：{infoData.TotalSignDay + 1} 天");
        lines.Add("今日奖励：" + GetAward(awards, infoData.TotalSignDay));
    }

    private async Task<IReadOnlyList<MihoyoLunaAward>> GetAwardsAsync(
        Func<CancellationToken, Task<MihoyoApiResponse<MihoyoLunaHomeData>>> getHome,
        ICollection<string> lines,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MihoyoLunaAward> awards = [];
        var failures = await executor.RunModuleAsync("游戏奖励查询", async token =>
        {
            var home = await getHome(token);
            awards = home.Data?.Awards ?? [];
            return Array.Empty<string>();
        }, cancellationToken);
        foreach (var failure in failures)
        {
            lines.Add(failure);
        }

        return awards;
    }

    private IEnumerable<MihoyoGameRole> ResolveGameTargets(
        MihoyoAccount account,
        IEnumerable<string>? requestedGameKeys,
        bool onlyEnabledAutoSign,
        bool includeMissingConfigMessage,
        ICollection<string> lines)
    {
        var requested = requestedGameKeys?
            .Select(key => MihoyoGameCatalog.FindByKey(key)?.CnGameBiz)
            .Where(biz => biz is not null)
            .Select(biz => biz!)
            .Distinct()
            .ToArray();

        if (requested is { Length: > 0 })
        {
            foreach (var gameBiz in requested)
            {
                var roles = account.Roles.Where(role => role.GameBiz == gameBiz).ToArray();
                if (roles.Length == 0)
                {
                    if (includeMissingConfigMessage)
                    {
                        lines.Add($"未找到 {MihoyoGameCatalog.NameForGameBiz(gameBiz)} 角色，请使用 /mihoyo game init {account.Id} 同步");
                    }

                    continue;
                }

                foreach (var role in roles)
                {
                    if (onlyEnabledAutoSign && !role.AutoSignEnabled)
                    {
                        continue;
                    }

                    yield return role;
                }
            }

            yield break;
        }

        foreach (var role in account.Roles)
        {
            if (onlyEnabledAutoSign && !role.AutoSignEnabled)
            {
                continue;
            }

            yield return role;
        }
    }

    private static string GetAward(IReadOnlyList<MihoyoLunaAward> awards, int index)
    {
        if (index < 0 || index >= awards.Count)
        {
            return "未知";
        }

        var award = awards[index];
        return $"「{award.Name}」x{award.Cnt}";
    }

    private Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs)),
            timeProvider ?? TimeProvider.System, cancellationToken);
    }
}
