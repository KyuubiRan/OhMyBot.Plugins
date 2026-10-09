using Microsoft.Extensions.Logging;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Skland;

public sealed class SklandSignService(
    SklandHttpClient client,
    SklandAccountService accountService,
    ILogger<SklandSignService> logger,
    SignInExecutor signInExecutor)
{
    // ---- Public API ----

    /// <summary>执行指定账号下所有已启用角色的游戏签到（自动签到路径）。</summary>
    public async Task<SklandAutoSignResult> ExecuteAutoSignAsync(
        SklandAccount account,
        CancellationToken cancellationToken = default)
    {
        var game = await ExecuteGameSignAsync(account, onlyEnabledAutoSign: true, cancellationToken: cancellationToken);
        return new SklandAutoSignResult(account, game.Lines);
    }

    /// <summary>
    /// 执行游戏签到。
    /// requestedRoleIds 为空时签所有角色（受 onlyEnabledAutoSign 过滤）。
    /// </summary>
    public async Task<SklandGameSignResult> ExecuteGameSignAsync(
        SklandAccount account,
        IEnumerable<long>? requestedRoleIds = null,
        bool onlyEnabledAutoSign = false,
        bool includeMissingConfigMessage = false,
        CancellationToken cancellationToken = default,
        ICommandProgressReporter? progress = null)
    {
        var lines = new List<string>();
        var targets = ResolveRoleTargets(account, requestedRoleIds, onlyEnabledAutoSign, includeMissingConfigMessage, lines).ToArray();

        foreach (var role in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add($"[{role.GameName}] {role.NickName}（{role.Uid}）");
            var moduleLines = await signInExecutor.RunModuleAsync(
                $"{role.GameName}签到",
                token => signInExecutor.ExecuteWithRetryAsync(
                    $"Skland account {account.Id} role {role.Id}",
                    attemptToken => ProcessRoleAsync(role, account, attemptToken, progress),
                    token),
                cancellationToken);
            lines.AddRange(moduleLines);
        }

        return new SklandGameSignResult(account, lines);
    }

    // ---- Role processing ----

    /// <summary>每次尝试从签到状态开始，POST 丢失响应时先确认是否已完成。</summary>
    private async Task<IReadOnlyList<string>> ProcessRoleAsync(
        SklandGameRole role,
        SklandAccount account,
        CancellationToken cancellationToken,
        ICommandProgressReporter? progress)
    {
        var lines = new List<string>();
        try
        {
            if (role.GameId == SklandGameNames.Arknights)
            {
                await ProcessArknightsRoleAsync(role, account, lines, cancellationToken, progress);
            }
            else if (role.GameId == SklandGameNames.Endfield)
            {
                await ProcessEndfieldRoleAsync(role, account, lines, cancellationToken, progress);
            }
            else
            {
                lines.Add("不支持的游戏 ID：" + role.GameId);
            }
        }
        catch (SklandCredExpiredException exception)
        {
            throw new CommandUserException("SklandCredExpired", exception.Message);
        }

        return lines;
    }

    private async Task ProcessArknightsRoleAsync(
        SklandGameRole role,
        SklandAccount account,
        List<string> lines,
        CancellationToken cancellationToken,
        ICommandProgressReporter? progress)
    {
        // 查签到状态
        var statusResult = await RequestWithTokenRecoveryAsync(
            account,
            (signToken, cred, token) => client.GetArknightsAttendanceAsync(role.Uid, signToken, cred, account.DeviceId, token),
            cancellationToken,
            progress);

        ThrowIfApiFailed(statusResult, "获取签到状态失败");

        if (IsArknightsAlreadySigned(statusResult.Data))
        {
            var todayAward = GetTodayArknightsAward(statusResult.Data);
            lines.Add("签到结果：今日已签到");
            if (!string.IsNullOrEmpty(todayAward))
            {
                lines.Add("今日奖励：" + todayAward);
            }

            return;
        }

        // 执行签到
        await DelayAsync(1000, 3000, cancellationToken);
        var signResult = await RequestWithTokenRecoveryAsync(
            account,
            (signToken, cred, token) => client.SignArknightsAsync(role.Uid, signToken, cred, account.DeviceId, token),
            cancellationToken,
            progress);
        ThrowIfApiFailed(signResult, "明日方舟签到失败");
        lines.Add("签到结果：成功");
        var awards = FormatArknightsAwards(signResult.Data?.Awards);
        if (!string.IsNullOrEmpty(awards))
        {
            lines.Add("今日奖励：" + awards);
        }

        logger.LogInformation(
            "Skland Arknights sign finished for account {AccountId}, uid={Uid}.",
            account.Id, role.Uid);
    }

    private async Task ProcessEndfieldRoleAsync(
        SklandGameRole role,
        SklandAccount account,
        List<string> lines,
        CancellationToken cancellationToken,
        ICommandProgressReporter? progress)
    {
        if (string.IsNullOrEmpty(role.RoleId) || string.IsNullOrEmpty(role.ServerId))
        {
            lines.Add("跳过：终末地角色缺少 roleId/serverId，请使用 /skland game init 同步角色");
            return;
        }

        // 查签到状态
        var statusResult = await RequestWithTokenRecoveryAsync(
            account,
            (signToken, cred, token) => client.GetEndfieldAttendanceAsync(role.RoleId, role.ServerId, signToken, cred, account.DeviceId, token),
            cancellationToken,
            progress);

        ThrowIfApiFailed(statusResult, "获取终末地签到状态失败");

        if (statusResult.Data?.HasToday == true)
        {
            lines.Add("签到结果：今日已签到");
            var award = FormatEndfieldAwards(statusResult.Data);
            if (!string.IsNullOrEmpty(award))
            {
                lines.Add("今日奖励：" + award);
            }

            return;
        }

        await DelayAsync(1000, 3000, cancellationToken);
        var signResult = await RequestWithTokenRecoveryAsync(
            account,
            (signToken, cred, token) => client.SignEndfieldAsync(role.RoleId, role.ServerId, signToken, cred, account.DeviceId, token),
            cancellationToken,
            progress);
        ThrowIfApiFailed(signResult, "终末地签到失败");
        lines.Add("签到结果：成功");
        var signAward = FormatEndfieldSignAwards(signResult.Data);
        if (!string.IsNullOrEmpty(signAward))
        {
            lines.Add("今日奖励：" + signAward);
        }

        logger.LogInformation(
            "Skland Endfield sign finished for account {AccountId}, roleId={RoleId}.",
            account.Id, role.RoleId);
    }

    // ---- Token expiry handling ----

    /// <summary>
    /// 认证失效时刷新一次后重发；凭据可能同时更新，始终重新读取 token 和 cred。
    /// </summary>
    private async Task<SklandApiResponse<T>> RequestWithTokenRecoveryAsync<T>(
        SklandAccount account,
        Func<string, string, CancellationToken, Task<SklandApiResponse<T>>> request,
        CancellationToken cancellationToken,
        ICommandProgressReporter? progress)
    {
        for (var authenticationAttempt = 0; ; authenticationAttempt++)
        {
            var (signToken, cred) = await accountService.GetValidTokensAsync(account, cancellationToken, progress);
            SklandApiResponse<T> response;
            try
            {
                response = await request(signToken, cred, cancellationToken);
            }
            catch (SklandUnauthorizedException) when (authenticationAttempt == 0)
            {
                await accountService.RefreshSignTokenAsync(account, cancellationToken, progress);
                continue;
            }
            catch (SklandUnauthorizedException)
            {
                throw new CommandUserException("SklandAuthenticationFailed", "森空岛身份验证失败，请重新绑定账号。");
            }

            if (response.Code == SklandHttpClient.CredExpiredCode)
            {
                await accountService.ClearCredAsync(account.Id, cancellationToken);
                account.CredCiphertext = string.Empty;
                account.SignTokenCiphertext = string.Empty;
                throw new SklandCredExpiredException(account.Id);
            }

            if (response.Code == SklandHttpClient.TokenExpiredCode)
            {
                if (authenticationAttempt > 0)
                {
                    throw new CommandUserException("SklandAuthenticationFailed", "森空岛身份验证失效，请重新绑定账号。");
                }

                await accountService.RefreshSignTokenAsync(account, cancellationToken, progress);
                continue;
            }

            return response;
        }
    }

    // ---- Role target resolution ----

    private static IEnumerable<SklandGameRole> ResolveRoleTargets(
        SklandAccount account,
        IEnumerable<long>? requestedRoleIds,
        bool onlyEnabledAutoSign,
        bool includeMissingConfigMessage,
        ICollection<string> lines)
    {
        var requestedList = requestedRoleIds?.Distinct().ToArray();
        if (requestedList is { Length: > 0 })
        {
            foreach (var roleId in requestedList)
            {
                var role = account.Roles.FirstOrDefault(r => r.Id == roleId);
                if (role is null)
                {
                    if (includeMissingConfigMessage)
                    {
                        lines.Add($"未找到角色 #{roleId}，请使用 /skland game init {account.Id} 同步角色");
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

    // ---- Award formatting ----

    private static bool IsArknightsAlreadySigned(SklandAttendanceData? data)
    {
        if (data is null)
        {
            return false;
        }

        var today = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date;
        return data.Records.Any(r =>
            DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).ToOffset(TimeSpan.FromHours(8)).Date == today);
    }

    private static string GetTodayArknightsAward(SklandAttendanceData? data)
    {
        if (data is null || data.Awards.Count == 0)
        {
            return string.Empty;
        }

        var record = data.Records
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();
        if (record is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(record.ResourceName) ? string.Empty : record.ResourceName;
    }

    private static string FormatArknightsAwards(IReadOnlyList<SklandAwardItem>? awards)
    {
        if (awards is null or { Count: 0 })
        {
            return string.Empty;
        }

        return string.Join("、", awards.Select(a => $"{a.Resource.Name} x{a.Count}"));
    }

    private static string FormatEndfieldAwards(SklandEndfieldAttendanceData? data)
    {
        if (data is null || data.ResourceInfoMap.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("、", data.ResourceInfoMap.Values.Select(r => $"{r.Name} x{r.Count}"));
    }

    private static string FormatEndfieldSignAwards(SklandEndfieldAttendanceSignResult? data)
    {
        if (data is null || data.ResourceInfoMap.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("、", data.ResourceInfoMap.Values.Select(r => $"{r.Name} x{r.Count}"));
    }

    private void ThrowIfApiFailed(SklandBaseResponse response, string prefix)
    {
        if (response.IsOk)
        {
            return;
        }

        logger.LogWarning("Skland sign API failed: operation={Operation}, code={Code}, message={Message}.",
            prefix, response.Code, response.Message);
        if (response.Code is SklandHttpClient.TokenExpiredCode or SklandHttpClient.DeviceInvalidCode)
        {
            throw new CommandUserException("SklandAuthenticationFailed", "森空岛身份或设备验证失败，请重新绑定账号。");
        }

        throw new SignInTransientException($"{prefix}（接口错误 {response.Code}）");
    }

    private static Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken)
    {
        return Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);
    }
}

public sealed record SklandGameSignResult(SklandAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}

public sealed record SklandAutoSignResult(SklandAccount Account, IReadOnlyList<string> Lines)
{
    public bool HasResult => Lines.Count > 0;
}
