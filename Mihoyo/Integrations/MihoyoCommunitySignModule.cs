using Microsoft.Extensions.Options;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Mihoyo;

public sealed class MihoyoCommunitySignModule(
    MihoyoHttpClient client,
    MihoyoAccountService accountService,
    MihoyoSignCredentialService credentialService,
    IOptions<MihoyoOptions> optionsAccessor,
    SignInExecutor executor,
    TimeProvider? timeProvider = null)
{
    private readonly MihoyoOptions _options = optionsAccessor.Value;

    public async Task<MihoyoBbsSignResult> ExecuteAsync(
        MihoyoAccount account,
        long taskFlags,
        IReadOnlySet<string>? requestedActions = null,
        bool runAllWhenNoRequestedActions = false,
        CancellationToken cancellationToken = default)
    {
        if (account.Region == MihoyoRegion.Os)
        {
            return new MihoyoBbsSignResult(account, ["国际服(HoYoLAB) 无米游社任务，已跳过"]);
        }

        var credential = accountService.GetCredential(account);
        if (string.IsNullOrEmpty(credential.Stoken))
        {
            return new MihoyoBbsSignResult(account, ["未提供 stoken，无法执行米游社任务（绑定时请使用包含 stoken 的 Cookie）"]);
        }

        var cookie = credential.Cookie;
        var missions = await client.GetMissionsAsync(cookie, cancellationToken);
        if (missions.Retcode == MihoyoHttpClient.CookieExpiredCode)
        {
            cookie = await credentialService.RefreshCnCookieAsync(account, credential, cancellationToken);
            missions = await client.GetMissionsAsync(cookie, cancellationToken);
        }

        var lines = new List<string>();
        var signDone = false;
        var readDone = false;
        var likeDone = false;
        var shareDone = false;
        var readRemaining = 3;
        var likeRemaining = 5;
        if (missions.Ok && missions.Data is { } data)
        {
            if (data.CanGetPoints == 0)
            {
                signDone = readDone = likeDone = shareDone = true;
            }
            else
            {
                foreach (var state in data.States)
                {
                    switch (state.MissionId)
                    {
                        case 58 when state.IsGetAward: signDone = true; break;
                        case 59: readDone = state.IsGetAward; readRemaining -= state.HappenedTimes; break;
                        case 60: likeDone = state.IsGetAward; likeRemaining -= state.HappenedTimes; break;
                        case 61 when state.IsGetAward: shareDone = true; break;
                    }
                }
            }
        }

        var deviceId = credential.DeviceId;
        IReadOnlyList<MihoyoPostInfo>? posts = null;

        if (ShouldDoAction(taskFlags, MihoyoBbsTaskFlags.SignIn, requestedActions, "signin", runAllWhenNoRequestedActions))
        {
            var failures = await executor.RunModuleAsync("社区签到", async token =>
            {
                if (signDone)
                {
                    lines.Add("社区签到：今日已完成");
                }
                else
                {
                    var signResult = await client.BbsSignInAsync(credential.StokenCookie, deviceId, GidsForSign(), token);
                    await ThrowIfStokenDeadAsync(account, signResult.Retcode, token);
                    if (signResult.Retcode == 1034)
                    {
                        lines.Add("社区签到：触发验证码，已跳过（不支持自动打码）");
                    }
                    else
                    {
                        lines.Add($"社区签到：{(signResult.Ok ? "成功" : "失败：" + signResult.Message)}");
                    }

                    await DelayAsync(2000, 4000, token);
                }

                return Array.Empty<string>();
            }, cancellationToken);
            lines.AddRange(failures);
            if (string.IsNullOrEmpty(account.StokenCiphertext))
            {
                return new MihoyoBbsSignResult(account, lines);
            }
        }

        if (ShouldDoAction(taskFlags, MihoyoBbsTaskFlags.ViewPosts, requestedActions, "view", runAllWhenNoRequestedActions))
        {
            var success = 0;
            var failures = await executor.RunModuleAsync("浏览帖子", async token =>
            {
                if (readDone || readRemaining <= 0)
                {
                    lines.Add("浏览帖子：任务已完成");
                }
                else
                {
                    posts ??= await LoadPostsAsync(account, credential, deviceId, token);
                    for (var i = 0; i < readRemaining && posts.Count > 0; i++)
                    {
                        var result = await client.ReadPostAsync(credential.StokenCookie, deviceId, posts[i % posts.Count].PostId, token);
                        await ThrowIfStokenDeadAsync(account, result.Retcode, token);
                        if (result.Ok)
                        {
                            success++;
                        }

                        await DelayAsync(2000, 4000, token);
                    }

                    lines.Add($"浏览帖子：成功 {success} 次");
                }

                return Array.Empty<string>();
            }, cancellationToken);
            if (failures.Count > 0 && success > 0)
            {
                lines.Add($"浏览帖子：成功 {success} 次，剩余任务未完成");
            }
            lines.AddRange(failures);
            if (string.IsNullOrEmpty(account.StokenCiphertext))
            {
                return new MihoyoBbsSignResult(account, lines);
            }
        }

        if (ShouldDoAction(taskFlags, MihoyoBbsTaskFlags.LikePosts, requestedActions, "like", runAllWhenNoRequestedActions))
        {
            var success = 0;
            var failures = await executor.RunModuleAsync("点赞帖子", async token =>
            {
                if (likeDone || likeRemaining <= 0)
                {
                    lines.Add("点赞帖子：任务已完成");
                }
                else
                {
                    posts ??= await LoadPostsAsync(account, credential, deviceId, token);
                    var captcha = false;
                    for (var i = 0; i < likeRemaining && posts.Count > 0; i++)
                    {
                        var result = await client.LikePostAsync(credential.StokenCookie, deviceId, posts[i % posts.Count].PostId, token);
                        await ThrowIfStokenDeadAsync(account, result.Retcode, token);
                        if (result.Retcode == 1034)
                        {
                            captcha = true;
                            break;
                        }

                        if (result.Ok)
                        {
                            success++;
                        }

                        await DelayAsync(2000, 4000, token);
                    }

                    lines.Add(captcha
                        ? $"点赞帖子：成功 {success} 次后触发验证码，已跳过（不支持自动打码）"
                        : $"点赞帖子：成功 {success} 次");
                }

                return Array.Empty<string>();
            }, cancellationToken);
            if (failures.Count > 0 && success > 0)
            {
                lines.Add($"点赞帖子：成功 {success} 次，剩余任务未完成");
            }
            lines.AddRange(failures);
            if (string.IsNullOrEmpty(account.StokenCiphertext))
            {
                return new MihoyoBbsSignResult(account, lines);
            }
        }

        if (ShouldDoAction(taskFlags, MihoyoBbsTaskFlags.SharePosts, requestedActions, "share", runAllWhenNoRequestedActions))
        {
            var failures = await executor.RunModuleAsync("分享帖子", async token =>
            {
                if (shareDone)
                {
                    lines.Add("分享帖子：任务已完成");
                }
                else
                {
                    posts ??= await LoadPostsAsync(account, credential, deviceId, token);
                    if (posts.Count == 0)
                    {
                        lines.Add("分享帖子：无可用帖子");
                    }
                    else
                    {
                        var result = await client.SharePostAsync(credential.StokenCookie, deviceId, posts[0].PostId, token);
                        await ThrowIfStokenDeadAsync(account, result.Retcode, token);
                        lines.Add($"分享帖子：{(result.Ok ? "成功" : "失败：" + result.Message)}");
                    }
                }

                return Array.Empty<string>();
            }, cancellationToken);
            lines.AddRange(failures);
            if (string.IsNullOrEmpty(account.StokenCiphertext))
            {
                return new MihoyoBbsSignResult(account, lines);
            }
        }

        return new MihoyoBbsSignResult(account, lines);
    }

    private async Task<IReadOnlyList<MihoyoPostInfo>> LoadPostsAsync(MihoyoAccount account, MihoyoCredential credential, string deviceId, CancellationToken cancellationToken)
    {
        var response = await client.GetPostListAsync(credential.StokenCookie, deviceId, BbsForumId(), cancellationToken);
        await ThrowIfStokenDeadAsync(account, response.Retcode, cancellationToken);
        if (!response.Ok)
        {
            throw new CommandUserException("MihoyoPostsUnavailable", "获取社区帖子列表失败，请稍后重试");
        }

        return response.Data?.List
            .Select(wrapper => wrapper.Post)
            .Where(post => !string.IsNullOrEmpty(post.PostId))
            .ToArray() ?? [];
    }

    private async Task ThrowIfStokenDeadAsync(MihoyoAccount account, int retcode, CancellationToken cancellationToken)
    {
        if (retcode != MihoyoHttpClient.CookieExpiredCode)
        {
            return;
        }

        await accountService.ClearStokenAsync(account.Id, cancellationToken);
        account.StokenCiphertext = string.Empty;
        throw new CommandUserException("MihoyoCookieExpired", "社区 stoken 已失效，请重新绑定米游社账号；游戏 Cookie 已保留");
    }

    private string GidsForSign()
    {
        return _options.BbsSignGids;
    }

    private string BbsForumId()
    {
        return _options.BbsForumId;
    }

    private static bool ShouldDoAction(
        long configuredTasks,
        long target,
        IReadOnlySet<string>? requestedActions,
        string key,
        bool runAllWhenNoRequestedActions)
    {
        return (configuredTasks & target) != 0
               || (runAllWhenNoRequestedActions && (requestedActions == null || requestedActions.Count == 0))
               || (requestedActions?.Contains(key) ?? false);
    }

    private Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken)
    {
        return Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs)),
            timeProvider ?? TimeProvider.System, cancellationToken);
    }
}
