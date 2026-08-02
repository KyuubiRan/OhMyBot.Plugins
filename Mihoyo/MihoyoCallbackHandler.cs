using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Integrations.Mihoyo;

namespace OhMyBot.Plugins.Mihoyo;

public sealed class MihoyoCallbackHandler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MihoyoCallbackHandler> logger) : IPluginCallbackHandler
{
    public IReadOnlyCollection<string> ActionTypes { get; } =
    [
        "mihoyo-bbs-sign-select",
        "mihoyo-bbs-sign-all",
        "mihoyo-game-sign-select",
        "mihoyo-game-sign-panel",
        "mihoyo-game-sign-run",
        "mihoyo-game-sign-back",
        "mihoyo-game-sign-all",
        "mihoyo-autosign-root-menu",
        "mihoyo-autosign-account-menu",
        "mihoyo-autosign-bbs-menu",
        "mihoyo-autosign-game-menu",
        "mihoyo-auto-sign-toggle",
        "mihoyo-bbs-task-toggle",
        "mihoyo-bbs-task-toggle-all",
        "mihoyo-game-auto-sign-toggle",
        "mihoyo-game-auto-sign-toggle-all",
        "mihoyo-delete-select",
        "mihoyo-delete-confirm"
    ];

    public async Task<CommandResponse> ExecuteAsync(
        string actionType,
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = timeProvider.GetTimestamp();
        try
        {
            return actionType switch
            {
                "mihoyo-bbs-sign-select" => await ExecuteBbsSignSelectAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-bbs-sign-all" => await ExecuteBbsSignAllAsync(context, editMessageId, cancellationToken),
                "mihoyo-game-sign-select" => await ExecuteGameSignSelectAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-game-sign-panel" => await ExecuteGameSignPanelAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-game-sign-run" => await ExecuteGameSignRunAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-game-sign-back" => await ExecuteGameSignBackAsync(context, editMessageId, cancellationToken),
                "mihoyo-game-sign-all" => await ExecuteGameSignAllAsync(context, editMessageId, cancellationToken),
                "mihoyo-autosign-root-menu" => await ExecuteAutoSignRootMenuAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-autosign-account-menu" => await ExecuteAutoSignAccountMenuAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-autosign-bbs-menu" => await ExecuteAutoSignBbsMenuAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-autosign-game-menu" => await ExecuteAutoSignGameMenuAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-auto-sign-toggle" => await ExecuteAutoSignToggleAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-bbs-task-toggle" => await ExecuteBbsTaskToggleAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-bbs-task-toggle-all" => await ExecuteBbsTaskToggleAllAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-game-auto-sign-toggle" => await ExecuteGameAutoSignToggleAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-game-auto-sign-toggle-all" => await ExecuteGameAutoSignToggleAllAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-delete-select" => await ExecuteDeleteSelectAsync(context, action, editMessageId, cancellationToken),
                "mihoyo-delete-confirm" => await ExecuteDeleteConfirmAsync(context, action, editMessageId, cancellationToken),
                _ => CallbackError(context, editMessageId, "未知米游社按钮操作。")
            };
        }
        finally
        {
            logger.LogDebug(
                "Mihoyo callback {ActionType} completed in {ElapsedMs} ms.",
                actionType,
                timeProvider.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }

    private async Task<CommandResponse> ExecuteBbsSignSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoBbsSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<MihoyoSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var result = await signService.ExecuteBbsSignAsync(
            account,
            taskFlags: 0,
            requestedActions: data.Actions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            runAllWhenNoRequestedActions: true,
            cancellationToken: cancellationToken);
        var response = builder.BuildBbsSignResult(context, result);
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private async Task<CommandResponse> ExecuteBbsSignAllAsync(
        CommandContext context,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<MihoyoSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        var cnAccounts = accounts.Where(account => account.Region == MihoyoRegion.Cn).ToArray();
        if (cnAccounts.Length == 0)
        {
            return CallbackError(context, editMessageId, "未找到国服米游社账号。");
        }

        var results = new List<(MihoyoAccount, IReadOnlyList<string>)>();
        foreach (var account in cnAccounts)
        {
            try
            {
                var result = await signService.ExecuteBbsSignAsync(
                    account,
                    taskFlags: 0,
                    requestedActions: null,
                    runAllWhenNoRequestedActions: true,
                    cancellationToken: cancellationToken);
                results.Add((account, result.Lines));
            }
            catch (Exception exception)
            {
                results.Add((account, ["签到失败：" + exception.GetBaseException().Message]));
            }
        }

        return builder.BuildCombinedResult(context, "[米游社-手动社区签到 - 全部账号]", results, editMessageId);
    }

    private async Task<CommandResponse> ExecuteGameSignSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoGameSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<MihoyoSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var response = builder.BuildGameSignResult(context, await signService.ExecuteGameSignAsync(
            account,
            data.GameKeys,
            includeMissingConfigMessage: true,
            cancellationToken: cancellationToken));
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private async Task<CommandResponse> ExecuteGameSignPanelAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var selected = string.IsNullOrEmpty(data.Toggle)
            ? MihoyoResponseBuilder.ResolveGameSignSelection(account)
            : await accountService.ToggleGameSignSelectionAsync(
                context.Identity.CoreUserId,
                account.Id,
                data.Toggle,
                cancellationToken);
        return await builder.BuildGameSignPanelAsync(context, account, selected, editMessageId, cancellationToken);
    }

    private async Task<CommandResponse> ExecuteGameSignRunAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<MihoyoSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var gameKeys = MihoyoResponseBuilder.ResolveGameSignSelection(account);
        if (gameKeys.Count == 0)
        {
            var panel = await builder.BuildGameSignPanelAsync(context, account, [], editMessageId, cancellationToken);
            panel.CallbackAnswerText = "请至少勾选一个游戏";
            panel.CallbackAnswerAlert = true;
            return panel;
        }

        var response = builder.BuildGameSignResult(context, await signService.ExecuteGameSignAsync(
            account,
            gameKeys,
            includeMissingConfigMessage: true,
            cancellationToken: cancellationToken));
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private async Task<CommandResponse> ExecuteGameSignBackAsync(
        CommandContext context,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        if (accounts.Count <= 1)
        {
            var canceled = CommandResponses.Text("已取消游戏签到", context);
            canceled.AsTelegramEdit(editMessageId);
            return canceled;
        }

        return await builder.BuildGameSignSelectionAsync(context, accounts, editMessageId, cancellationToken);
    }

    private async Task<CommandResponse> ExecuteGameSignAllAsync(
        CommandContext context,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<MihoyoSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到米游社账号。");
        }

        var results = new List<(MihoyoAccount, IReadOnlyList<string>)>();
        foreach (var account in accounts)
        {
            try
            {
                var result = await signService.ExecuteGameSignAsync(
                    account,
                    includeMissingConfigMessage: true,
                    cancellationToken: cancellationToken);
                results.Add((account, result.Lines));
            }
            catch (Exception exception)
            {
                results.Add((account, ["签到失败：" + exception.GetBaseException().Message]));
            }
        }

        return builder.BuildCombinedResult(context, "[米游社-手动游戏签到 - 全部账号]", results, editMessageId);
    }

    private async Task<CommandResponse> ExecuteAutoSignRootMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignPanelAsync(context, accounts, editMessageId, cancellationToken, data.Page);
    }

    private async Task<CommandResponse> ExecuteAutoSignAccountMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignAccountPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteAutoSignBbsMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignBbsPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteAutoSignGameMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignGamePanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken,
            data.Page);
    }

    private async Task<CommandResponse> ExecuteAutoSignToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();

        // 主菜单的「开启/关闭全部」：翻转后回到主菜单本身，而不是某个账号详情。
        if (data.ToggleAll)
        {
            var all = await accountService.ToggleAllAutoSignAsync(context.Identity.CoreUserId, cancellationToken);
            return all.Count == 0
                ? CallbackError(context, editMessageId, "未找到指定米游社账号。")
                : await builder.BuildAutoSignPanelAsync(context, all, editMessageId, cancellationToken, data.Page);
        }

        var accounts = await accountService.ToggleAutoSignAsync(context.Identity.CoreUserId, data.AccountId, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        return await builder.BuildAutoSignAccountPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteBbsTaskToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoBbsTaskCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ToggleBbsTaskAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            data.TaskFlag,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        return await builder.BuildAutoSignBbsPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteBbsTaskToggleAllAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoBbsTaskToggleAllCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ToggleAllBbsTasksAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        return await builder.BuildAutoSignBbsPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteGameAutoSignToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoGameAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ToggleGameAutoSignAsync(context.Identity.CoreUserId, data.RoleId, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社角色。");
        }

        var accountId = data.AccountId == 0
            ? accounts.FirstOrDefault(account => account.Roles.Any(role => role.Id == data.RoleId))?.Id ?? 0
            : data.AccountId;
        if (accountId == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        return await builder.BuildAutoSignGamePanelAsync(
            context,
            accounts,
            accountId,
            editMessageId,
            cancellationToken,
            data.Page);
    }

    private async Task<CommandResponse> ExecuteGameAutoSignToggleAllAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoGameAutoSignToggleAllCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<MihoyoResponseBuilder>();
        var accounts = await accountService.ToggleAllGameAutoSignAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        return await builder.BuildAutoSignGamePanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken,
            data.Page);
    }

    private async Task<CommandResponse> ExecuteDeleteSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoAccountCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var callbackStore = scope.ServiceProvider.GetRequiredService<CallbackActionStore>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var response = CommandResponses.Text($"确认删除米游社账号绑定？\n账号：`{account.DisplayName}`", context);
        response.AsTelegramEdit(editMessageId);
        response.AddButtonRow(new ResponseButtonRow
        {
            Buttons =
            {
                new ResponseButton
                {
                    Text = "确认删除",
                    Payload = await callbackStore.PutAsync(
                        "mihoyo-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new MihoyoDeleteConfirmCallbackData(account.Id, Confirm: true),
                        cancellationToken: cancellationToken)
                },
                new ResponseButton
                {
                    Text = "取消",
                    Payload = await callbackStore.PutAsync(
                        "mihoyo-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new MihoyoDeleteConfirmCallbackData(account.Id, Confirm: false),
                        cancellationToken: cancellationToken)
                }
            }
        });
        return response;
    }

    private async Task<CommandResponse> ExecuteDeleteConfirmAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<MihoyoDeleteConfirmCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context, editMessageId, "按钮数据无效。");
        }

        if (!data.Confirm)
        {
            var canceled = CommandResponses.Text("删除操作已取消", context);
            canceled.AsTelegramEdit(editMessageId);
            return canceled;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<MihoyoAccountService>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context, editMessageId, "未找到指定米游社账号。");
        }

        var deleted = await accountService.DeleteAsync(context.Identity.CoreUserId, data.AccountId, cancellationToken);
        var response = CommandResponses.Text(
            deleted ? $"已删除米游社账号绑定：`{account.DisplayName}`" : "未找到指定米游社账号",
            context);
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private static CommandResponse CallbackError(CommandContext context, string editMessageId, string message)
    {
        return new CommandResponse
        {
            Code = 1,
            ErrorCode = "CallbackRejected",
            CallbackAnswerText = message,
            CallbackAnswerAlert = false,
            Context = new CommandResponseContext
            {
                CallerCoreUserId = context.Identity.CoreUserId,
                CallerPrivilege = context.Identity.Privilege,
                Platform = context.Identity.Platform
            },
            Telegram = new TelegramResponse
            {
                Messages =
                {
                    new TelegramMessage
                    {
                        Text = $"错误：{message}（CallbackRejected）",
                        ParseMode = TelegramParseMode.None,
                        EditMessageId = editMessageId
                    }
                }
            }
        };
    }
}
