using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Identity;
using OhMyBot.Core.Integrations.Kuro;

namespace OhMyBot.Plugins.Kuro;

public sealed class KuroCallbackHandler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IPluginCallbackHandler
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public IReadOnlyCollection<string> ActionTypes { get; } =
    [
        "kuro-bbs-sign-select",
        "kuro-bbs-sign-all",
        "kuro-game-sign-select",
        "kuro-game-sign-panel",
        "kuro-game-sign-run",
        "kuro-game-sign-back",
        "kuro-game-sign-all",
        "kuro-autosign-root-menu",
        "kuro-autosign-account-menu",
        "kuro-autosign-bbs-menu",
        "kuro-autosign-game-menu",
        "kuro-auto-sign-toggle",
        "kuro-bbs-task-toggle",
        "kuro-bbs-task-toggle-all",
        "kuro-game-auto-sign-toggle",
        "kuro-game-auto-sign-toggle-all",
        "kuro-delete-select",
        "kuro-delete-confirm"
    ];

    public Task<CommandResponse> ExecuteAsync(
        string actionType,
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        // Resolve the invocation clock from the plugin container as part of the stable callback contract.
        _ = _timeProvider.GetTimestamp();

        return actionType switch
        {
            "kuro-bbs-sign-select" => ExecuteBbsSignSelectAsync(context, action, editMessageId, cancellationToken),
            "kuro-bbs-sign-all" => ExecuteBbsSignAllAsync(context, editMessageId, cancellationToken),
            "kuro-game-sign-select" => ExecuteGameSignSelectAsync(context, action, editMessageId, cancellationToken),
            "kuro-game-sign-panel" => ExecuteGameSignPanelAsync(context, action, editMessageId, cancellationToken),
            "kuro-game-sign-run" => ExecuteGameSignRunAsync(context, action, editMessageId, cancellationToken),
            "kuro-game-sign-back" => ExecuteGameSignBackAsync(context, editMessageId, cancellationToken),
            "kuro-game-sign-all" => ExecuteGameSignAllAsync(context, editMessageId, cancellationToken),
            "kuro-autosign-root-menu" => ExecuteAutoSignRootMenuAsync(context, action, editMessageId, cancellationToken),
            "kuro-autosign-account-menu" => ExecuteAutoSignAccountMenuAsync(context, action, editMessageId, cancellationToken),
            "kuro-autosign-bbs-menu" => ExecuteAutoSignBbsMenuAsync(context, action, editMessageId, cancellationToken),
            "kuro-autosign-game-menu" => ExecuteAutoSignGameMenuAsync(context, action, editMessageId, cancellationToken),
            "kuro-auto-sign-toggle" => ExecuteAutoSignToggleAsync(context, action, editMessageId, cancellationToken),
            "kuro-bbs-task-toggle" => ExecuteBbsTaskToggleAsync(context, action, editMessageId, cancellationToken),
            "kuro-bbs-task-toggle-all" => ExecuteBbsTaskToggleAllAsync(context, action, editMessageId, cancellationToken),
            "kuro-game-auto-sign-toggle" => ExecuteGameAutoSignToggleAsync(context, action, editMessageId, cancellationToken),
            "kuro-game-auto-sign-toggle-all" => ExecuteGameAutoSignToggleAllAsync(context, action, editMessageId, cancellationToken),
            "kuro-delete-select" => ExecuteDeleteSelectAsync(context, action, editMessageId, cancellationToken),
            "kuro-delete-confirm" => ExecuteDeleteConfirmAsync(context, action, editMessageId, cancellationToken),
            _ => Task.FromResult(CallbackError(context.Identity, editMessageId, "未知库街区按钮操作。"))
        };
    }

    private async Task<CommandResponse> ExecuteBbsSignSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroBbsSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<KuroSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
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
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<KuroSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到库街区账号。");
        }

        var results = new List<(KuroAccount, IReadOnlyList<string>)>();
        foreach (var account in accounts)
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

        return builder.BuildCombinedResult(context, "[库街区-手动社区签到 - 全部账号]", results, editMessageId);
    }

    private async Task<CommandResponse> ExecuteGameSignSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroGameSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<KuroSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        var response = builder.BuildGameSignResult(context, await signService.ExecuteGameSignAsync(
            account,
            data.GameIds,
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
        var data = CallbackActionStore.ReadData<KuroGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        var selected = data.Toggle == 0
            ? KuroResponseBuilder.ResolveGameSignSelection(account)
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
        var data = CallbackActionStore.ReadData<KuroGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<KuroSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        var gameIds = KuroResponseBuilder.ResolveGameSignSelection(account);
        if (gameIds.Count == 0)
        {
            var panel = await builder.BuildGameSignPanelAsync(context, account, [], editMessageId, cancellationToken);
            panel.CallbackAnswerText = "请至少勾选一个游戏";
            panel.CallbackAnswerAlert = true;
            return panel;
        }

        var response = builder.BuildGameSignResult(context, await signService.ExecuteGameSignAsync(
            account,
            gameIds,
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
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
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
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<KuroSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到库街区账号。");
        }

        var results = new List<(KuroAccount, IReadOnlyList<string>)>();
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

        return builder.BuildCombinedResult(context, "[库街区-手动游戏签到 - 全部账号]", results, editMessageId);
    }

    private async Task<CommandResponse> ExecuteAutoSignRootMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignPanelAsync(context, accounts, editMessageId, cancellationToken, data.Page);
    }

    private async Task<CommandResponse> ExecuteAutoSignAccountMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignAccountPanelAsync(context, accounts, data.AccountId, editMessageId, cancellationToken, data.Page);
    }

    private async Task<CommandResponse> ExecuteAutoSignBbsMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(context.Identity.CoreUserId, noTracking: true, cancellationToken);
        return await builder.BuildAutoSignBbsPanelAsync(context, accounts, data.AccountId, editMessageId, cancellationToken, data.Page);
    }

    private async Task<CommandResponse> ExecuteAutoSignGameMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
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
        var data = CallbackActionStore.ReadData<KuroAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();

        // 主菜单的「开启/关闭全部」：翻转后回到主菜单本身，而不是某个账号详情。
        if (data.ToggleAll)
        {
            var all = await accountService.ToggleAllAutoSignAsync(context.Identity.CoreUserId, cancellationToken);
            return all.Count == 0
                ? CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。")
                : await builder.BuildAutoSignPanelAsync(context, all, editMessageId, cancellationToken, data.Page);
        }

        var accounts = await accountService.ToggleAutoSignAsync(context.Identity.CoreUserId, data.AccountId, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        return await builder.BuildAutoSignAccountPanelAsync(
            context, accounts, data.AccountId, editMessageId, cancellationToken, data.Page);
    }

    private async Task<CommandResponse> ExecuteBbsTaskToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroBbsTaskCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ToggleBbsTaskAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            data.TaskFlag,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        return await builder.BuildAutoSignBbsPanelAsync(context, accounts, data.AccountId, editMessageId, cancellationToken);
    }

    private async Task<CommandResponse> ExecuteBbsTaskToggleAllAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroBbsTaskToggleAllCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ToggleAllBbsTasksAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        return await builder.BuildAutoSignBbsPanelAsync(context, accounts, data.AccountId, editMessageId, cancellationToken);
    }

    private async Task<CommandResponse> ExecuteGameAutoSignToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<KuroGameAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ToggleGameAutoSignAsync(context.Identity.CoreUserId, data.RoleId, cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区角色。");
        }

        var accountId = data.AccountId == 0
            ? accounts.FirstOrDefault(account => account.Roles.Any(role => role.Id == data.RoleId))?.Id ?? 0
            : data.AccountId;
        if (accountId == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
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
        var data = CallbackActionStore.ReadData<KuroGameAutoSignToggleAllCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<KuroResponseBuilder>();
        var accounts = await accountService.ToggleAllGameAutoSignAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
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
        var data = CallbackActionStore.ReadData<KuroAccountCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var callbackStore = scope.ServiceProvider.GetRequiredService<CallbackActionStore>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        var response = CommandResponses.Text($"确认删除库街区账号绑定？\n账号：`{account.DisplayName}`", context);
        response.AsTelegramEdit(editMessageId);
        response.AddButtonRow(new ResponseButtonRow
        {
            Buttons =
            {
                new ResponseButton
                {
                    Text = "确认删除",
                    Payload = await callbackStore.PutAsync(
                        "kuro-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new KuroDeleteConfirmCallbackData(account.Id, Confirm: true),
                        cancellationToken: cancellationToken)
                },
                new ResponseButton
                {
                    Text = "取消",
                    Payload = await callbackStore.PutAsync(
                        "kuro-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new KuroDeleteConfirmCallbackData(account.Id, Confirm: false),
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
        var data = CallbackActionStore.ReadData<KuroDeleteConfirmCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        if (!data.Confirm)
        {
            var canceled = CommandResponses.Text("删除操作已取消", context);
            canceled.AsTelegramEdit(editMessageId);
            return canceled;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<KuroAccountService>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定库街区账号。");
        }

        var deleted = await accountService.DeleteAsync(context.Identity.CoreUserId, data.AccountId, cancellationToken);
        var response = CommandResponses.Text(
            deleted ? $"已删除库街区账号绑定：`{account.DisplayName}`" : "未找到指定库街区账号",
            context);
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    internal static CommandResponse CallbackError(ResolvedIdentity identity, string editMessageId, string message)
    {
        return new CommandResponse
        {
            Code = 1,
            ErrorCode = "CallbackRejected",
            CallbackAnswerText = message,
            CallbackAnswerAlert = false,
            Context = new CommandResponseContext
            {
                CallerCoreUserId = identity.CoreUserId,
                CallerPrivilege = identity.Privilege,
                Platform = identity.Platform
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
