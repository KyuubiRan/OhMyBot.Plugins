using System.Globalization;
using Microsoft.Extensions.Logging;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Plugin.Commanding.SignIn;

namespace OhMyBot.Core.Integrations.Kuro;

public sealed class KuroCommunitySignModule(
    KuroHttpClient client,
    KuroAccountService accountService,
    ILogger<KuroCommunitySignModule> logger,
    SignInExecutor executor)
{
    public async Task<KuroBbsSignResult> ExecuteAsync(
        KuroAccount account,
        long taskFlags,
        IReadOnlySet<string>? requestedActions = null,
        bool runAllWhenNoRequestedActions = false,
        CancellationToken cancellationToken = default)
    {
        var credential = accountService.GetCredential(account);
        var progressResponse = await client.GetTaskProgressAsync(credential, account.BbsUserId, cancellationToken: cancellationToken);
        await ThrowIfFatalResponseAsync(account, progressResponse, "获取任务进度失败：", cancellationToken);
        var currentProgress = progressResponse.Data?.DailyTask ?? [];
        var progress = currentProgress
            .Select(item => new KuroBbsTaskProgressSnapshot(item.Remark, item.CompleteTimes, item.NeedActionTimes, item.GainGold, item.Finished))
            .ToArray();
        var lines = new List<string>();
        KuroApiResponse<KuroPostListData>? lazyPosts = null;

        if (ShouldDoAction(taskFlags, KuroBbsTaskFlags.SignIn, requestedActions, "signin", runAllWhenNoRequestedActions))
        {
            await ExecuteTaskAsync("社区签到", account, async () =>
            {
                var signTask = currentProgress.FirstOrDefault(item => item.Remark == "用户签到");
                if (signTask?.Finished == true)
                {
                    lines.Add("社区签到结果：今日已签到");
                }
                else
                {
                    var signResult = await client.BbsSignInAsync(credential, cancellationToken: cancellationToken);
                    await ThrowIfTokenExpiredAsync(account, signResult, cancellationToken);
                    lines.Add($"社区签到结果：{(signResult.Success ? "成功" : signResult.Msg.Contains("重复签到", StringComparison.Ordinal) ? "今日已签到" : "失败")}");
                    if (!signResult.Success)
                    {
                        lines.Add("原因：" + signResult.Msg);
                        if (!signResult.Msg.Contains("重复签到", StringComparison.Ordinal))
                            OhMyBot.Core.Infrastructure.ScheduledTasks.ManagedTaskExecutionContext.ReportFailure(
                                $"库街区账号 {account.Id} 社区签到失败：{signResult.Msg}");
                    }

                    logger.Log(signResult.Success ? LogLevel.Information : LogLevel.Warning,
                        "Kuro BBS sign in finished for account {AccountId}, success={Success}, message={Message}",
                        account.Id,
                        signResult.Success,
                        signResult.Msg);
                    await DelayAsync(1000, 2000, cancellationToken);
                }
            }, lines, cancellationToken);
        }

        if (ShouldDoAction(taskFlags, KuroBbsTaskFlags.ViewPosts, requestedActions, "view", runAllWhenNoRequestedActions))
        {
            await ExecuteTaskAsync("浏览帖子", account, async () =>
            {
                var viewTask = currentProgress.FirstOrDefault(item => item.Remark == "浏览3篇帖子");
                if (viewTask?.Finished == true)
                {
                    lines.Add("浏览帖子结果：任务已完成");
                }
                else
                {
                    lazyPosts ??= await client.GetPostsAsync(credential, cancellationToken: cancellationToken);
                    await ThrowIfFatalResponseAsync(account, lazyPosts, "获取帖子列表失败：", cancellationToken);
                    var posts = lazyPosts.Data?.PostList ?? [];
                    var (success, failed) = await ExecutePostLoopAsync(
                        viewTask,
                        posts,
                        post => client.GetPostDetailAsync(credential, post.PostId, cancellationToken),
                        account,
                        lines,
                        "浏览帖子",
                        cancellationToken);
                    lines.Add($"浏览帖子结果：成功 {success}/{success + failed}");
                }
            }, lines, cancellationToken);
        }

        if (ShouldDoAction(taskFlags, KuroBbsTaskFlags.LikePosts, requestedActions, "like", runAllWhenNoRequestedActions))
        {
            await ExecuteTaskAsync("点赞帖子", account, async () =>
            {
                var likeTask = currentProgress.FirstOrDefault(item => item.Remark == "点赞5次");
                if (likeTask?.Finished == true)
                {
                    lines.Add("点赞帖子结果：任务已完成");
                }
                else
                {
                    lazyPosts ??= await client.GetPostsAsync(credential, cancellationToken: cancellationToken);
                    await ThrowIfFatalResponseAsync(account, lazyPosts, "获取帖子列表失败：", cancellationToken);
                    var posts = lazyPosts.Data?.PostList ?? [];
                    var (success, failed) = await ExecutePostLoopAsync(
                        likeTask,
                        posts,
                        post => client.LikePostAsync(credential, post.GameId, post.GameForumId, post.PostType, post.PostId, post.UserId, cancellationToken),
                        account,
                        lines,
                        "点赞帖子",
                        cancellationToken,
                        delayMinMs: 2000,
                        delayMaxMs: 5000);
                    lines.Add($"点赞帖子结果：成功 {success}/{success + failed}");
                }
            }, lines, cancellationToken);
        }

        if (ShouldDoAction(taskFlags, KuroBbsTaskFlags.SharePosts, requestedActions, "share", runAllWhenNoRequestedActions))
        {
            await ExecuteTaskAsync("分享帖子", account, async () =>
            {
                var shareTask = currentProgress.FirstOrDefault(item => item.Remark == "分享1次帖子");
                if (shareTask?.Finished == true)
                {
                    lines.Add("分享帖子结果：任务已完成");
                }
                else
                {
                    var shareResult = await client.SharePostAsync(credential, cancellationToken: cancellationToken);
                    await ThrowIfTokenExpiredAsync(account, shareResult, cancellationToken);
                    lines.Add($"分享帖子结果：{(shareResult.Success ? "成功" : "失败")}");
                    if (!shareResult.Success)
                    {
                        lines.Add("原因：" + shareResult.Msg);
                        OhMyBot.Core.Infrastructure.ScheduledTasks.ManagedTaskExecutionContext.ReportFailure(
                            $"库街区账号 {account.Id} 分享帖子失败：{shareResult.Msg}");
                    }

                    logger.Log(shareResult.Success ? LogLevel.Information : LogLevel.Warning,
                        "Kuro BBS share post finished for account {AccountId}, success={Success}, message={Message}",
                        account.Id,
                        shareResult.Success,
                        shareResult.Msg);
                    await DelayAsync(1000, 2000, cancellationToken);
                }
            }, lines, cancellationToken);
        }

        return new KuroBbsSignResult(account, progress, lines);
    }

    private async Task ExecuteTaskAsync(string name, KuroAccount account, Func<Task> action, List<string> lines, CancellationToken cancellationToken)
    {
        lines.AddRange(await executor.RunModuleAsync(name, async _ =>
        {
            accountService.GetCredential(account);
            await action();
            return Array.Empty<string>();
        }, cancellationToken));
    }

    private static Task DelayAsync(int minMs, int maxMs, CancellationToken cancellationToken)
        => Task.Delay(Random.Shared.Next(minMs, maxMs), cancellationToken);

    private async Task<(int Success, int Failed)> ExecutePostLoopAsync<T>(
        KuroTaskProgressItem? task,
        IReadOnlyList<KuroPostItem> posts,
        Func<KuroPostItem, Task<KuroApiResponse<T>>> action,
        KuroAccount account,
        ICollection<string> lines,
        string name,
        CancellationToken cancellationToken,
        int delayMinMs = 1000,
        int delayMaxMs = 2000)
    {
        var success = 0;
        var failed = 0;
        var start = task?.CompleteTimes ?? 0;
        var end = task?.NeedActionTimes ?? 0;
        for (var i = start; i < end; i++)
        {
            if (posts.Count == 0)
            {
                break;
            }

            var failures = await executor.RunModuleAsync(name, async _ =>
            {
                var result = await action(posts[i % posts.Count]);
                await ThrowIfTokenExpiredAsync(account, result, cancellationToken);
                if (result.Success)
                {
                    success++;
                }
                else
                {
                    failed++;
                    OhMyBot.Core.Infrastructure.ScheduledTasks.ManagedTaskExecutionContext.ReportFailure(
                        $"库街区账号 {account.Id} {name}失败：{result.Msg}");
                }

                return Array.Empty<string>();
            }, cancellationToken);
            if (failures.Count > 0)
            {
                foreach (var failure in failures) lines.Add(failure);
                failed++;
                break;
            }

            await DelayAsync(delayMinMs, delayMaxMs, cancellationToken);
        }

        return (success, failed);
    }

    private async Task ThrowIfFatalResponseAsync(
        KuroAccount account,
        KuroBaseResponse response,
        string messagePrefix,
        CancellationToken cancellationToken)
    {
        if (response.Success)
        {
            return;
        }

        if (response.Code == KuroHttpClient.TokenExpiredCode)
        {
            account.TokenCiphertext = string.Empty;
            await accountService.ClearTokenAsync(account.Id, cancellationToken);
            throw new CommandUserException("KuroTokenExpired", "Token 已失效，请重新绑定库街区账号");
        }

        throw new CommandUserException("KuroApiRejected", messagePrefix + response.Msg);
    }

    private async Task ThrowIfTokenExpiredAsync(KuroAccount account, KuroBaseResponse response, CancellationToken cancellationToken)
    {
        if (response.Code != KuroHttpClient.TokenExpiredCode)
        {
            return;
        }

        account.TokenCiphertext = string.Empty;
        await accountService.ClearTokenAsync(account.Id, cancellationToken);
        throw new CommandUserException("KuroTokenExpired", "Token 已失效，请重新绑定库街区账号");
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

}
