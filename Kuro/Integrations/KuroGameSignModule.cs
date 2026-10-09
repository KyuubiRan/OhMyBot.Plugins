using System.Globalization;
using Microsoft.Extensions.Logging;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Kuro;

public sealed class KuroGameSignModule(
    KuroHttpClient client,
    KuroAccountService accountService,
    ILogger<KuroGameSignModule> logger,
    SignInExecutor executor)
{
    public async Task<KuroGameSignResult> ExecuteAsync(
        KuroAccount account,
        IEnumerable<long>? requestedGames = null,
        bool onlyEnabledAutoSign = false,
        bool includeMissingConfigMessage = false,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        var roles = ResolveGameTargets(account, requestedGames, onlyEnabledAutoSign, includeMissingConfigMessage, lines).ToArray();
        foreach (var role in roles)
        {
            lines.Add($"[{role.GameName}] {role.RoleName}");
            lines.AddRange(await executor.RunModuleAsync("游戏签到", token =>
                executor.ExecuteWithRetryAsync<IReadOnlyList<string>>($"库街区/{role.GameName}/{role.RoleId}",
                    attemptToken => ExecuteRoleAsync(account, role, attemptToken), token), cancellationToken));
        }

        return new KuroGameSignResult(account, lines);
    }

    private async Task<IReadOnlyList<string>> ExecuteRoleAsync(KuroAccount account, KuroGameRole role, CancellationToken cancellationToken)
    {
        var credential = accountService.GetCredential(account);
        var init = await client.GameSignInInitAsync(credential, role.GameId, role.ServerId, role.RoleId, account.BbsUserId, cancellationToken);
        await ThrowIfTokenExpiredAsync(account, init, cancellationToken);
        if (!init.Success || init.Data is not { } signData)
        {
            throw new SignInTransientException("库街区签到初始化失败，请稍后手动重试。");
        }

        if (signData.IsSignIn)
        {
            return ["签到结果：今日已签到", "签到天数：" + signData.SignInNum.ToString(CultureInfo.InvariantCulture),
                "奖励：" + GetSignInReward(signData, signData.SignInNum)];
        }

        await DelayAsync(1000, 3000, cancellationToken);
        var signResult = await client.GameSignInAsync(credential, role.GameId, role.ServerId, role.RoleId, account.BbsUserId, cancellationToken);
        await ThrowIfTokenExpiredAsync(account, signResult, cancellationToken);
        if (!signResult.Success)
        {
            throw new SignInTransientException("库街区游戏签到未完成，请稍后手动重试。");
        }

        logger.LogInformation("Kuro game sign finished for account {AccountId}, game={GameId}, role={RoleId}, success=True.",
            account.Id, role.GameId, role.RoleId);
        await DelayAsync(1000, 3000, cancellationToken);
        return ["签到结果：成功", "签到天数：" + (signData.SignInNum + 1).ToString(CultureInfo.InvariantCulture),
            "奖励：" + GetSignInReward(signData, signData.SignInNum + 1)];
    }

    private async Task ThrowIfTokenExpiredAsync(KuroAccount account, KuroBaseResponse response, CancellationToken cancellationToken)
    {
        if (response.Code != KuroHttpClient.TokenExpiredCode) return;
        account.TokenCiphertext = string.Empty;
        await accountService.ClearTokenAsync(account.Id, cancellationToken);
        throw new CommandUserException("KuroTokenExpired", "Token 已失效，请重新绑定库街区账号");
    }

    private static IEnumerable<KuroGameRole> ResolveGameTargets(
        KuroAccount account,
        IEnumerable<long>? requestedGames,
        bool onlyEnabledAutoSign,
        bool includeMissingConfigMessage,
        ICollection<string> lines)
    {
        var requestedList = requestedGames?.Distinct().ToArray();
        if (requestedList is { Length: > 0 })
        {
            foreach (var gameId in requestedList)
            {
                var role = account.Roles.FirstOrDefault(item => item.GameId == gameId);
                if (role is null)
                {
                    if (includeMissingConfigMessage)
                    {
                        lines.Add($"未找到 {KuroGameNames.Format(gameId)} 角色信息，请使用 /kuro game init {account.Id} 同步角色");
                    }

                    continue;
                }

                if (onlyEnabledAutoSign && !role.AutoSignEnabled)
                {
                    continue;
                }

                yield return role;
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

    private static string GetSignInReward(KuroGameSignInInitData signData, int day)
    {
        return string.Join(", ", signData.SignInGoodsConfigs
            .Where(item => item.SerialNum == day - 1)
            .Select(item => $"{item.GoodsName} x{item.GoodsNum}"));
    }

    private static Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken)
    {
        return Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
    }
}
