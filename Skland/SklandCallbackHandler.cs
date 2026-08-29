using OhMyBot.Contracts.Grpc;
using OhMyBot.Core.Commanding.Callbacks;
using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Commanding.Presentation;
using OhMyBot.Core.Infrastructure.Data.Entities;
using OhMyBot.Core.Infrastructure.Identity;
using OhMyBot.Core.Integrations.Skland;

namespace OhMyBot.Plugins.Skland;

/// <summary>
/// Handles the legacy skland-* callback action names so buttons emitted before pluginization
/// remain valid until their normal callback TTL expires.
/// </summary>
public sealed class SklandCallbackHandler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IPluginCallbackHandler
{
    public IReadOnlyCollection<string> ActionTypes { get; } =
    [
        "skland-game-sign-panel",
        "skland-game-sign-run",
        "skland-game-sign-back",
        "skland-game-sign-all",
        "skland-autosign-root-menu",
        "skland-autosign-account-menu",
        "skland-auto-sign-toggle",
        "skland-game-auto-sign-toggle",
        "skland-game-auto-sign-toggle-all",
        "skland-delete-select",
        "skland-delete-confirm"
    ];

    public Task<CommandResponse> ExecuteAsync(
        string actionType,
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken = default)
    {
        var pluginContext = context with
        {
            StartedAt = timeProvider.GetTimestamp(),
            CancellationToken = cancellationToken
        };

        return actionType switch
        {
            "skland-game-sign-panel" => ExecuteGameSignPanelAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-game-sign-run" => ExecuteGameSignRunAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-game-sign-back" => ExecuteGameSignBackAsync(pluginContext, editMessageId, cancellationToken),
            "skland-game-sign-all" => ExecuteGameSignAllAsync(pluginContext, editMessageId, cancellationToken),
            "skland-autosign-root-menu" => ExecuteAutoSignRootMenuAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-autosign-account-menu" => ExecuteAutoSignAccountMenuAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-auto-sign-toggle" => ExecuteAutoSignToggleAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-game-auto-sign-toggle" => ExecuteGameAutoSignToggleAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-game-auto-sign-toggle-all" => ExecuteGameAutoSignToggleAllAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-delete-select" => ExecuteDeleteSelectAsync(pluginContext, action, editMessageId, cancellationToken),
            "skland-delete-confirm" => ExecuteDeleteConfirmAsync(pluginContext, action, editMessageId, cancellationToken),
            _ => Task.FromResult(CallbackError(context.Identity, editMessageId, "未知森空岛按钮操作。"))
        };
    }

    private async Task<CommandResponse> ExecuteGameSignPanelAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<SklandGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        var selected = string.IsNullOrEmpty(data.Toggle)
            ? SklandResponseBuilder.ResolveGameSignSelection(account)
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
        var data = CallbackActionStore.ReadData<SklandGameSignPanelCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<SklandSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        var selectedKeys = SklandResponseBuilder.ResolveGameSignSelection(account);
        if (selectedKeys.Count == 0)
        {
            var panel = await builder.BuildGameSignPanelAsync(
                context,
                account,
                selectedKeys,
                editMessageId,
                cancellationToken);
            panel.CallbackAnswerText = "请至少勾选一个游戏";
            panel.CallbackAnswerAlert = true;
            return panel;
        }

        var selectedGameIds = selectedKeys.Select(SklandGameNames.FromAppCode).ToHashSet();
        var roleIds = account.Roles
            .Where(role => selectedGameIds.Contains(role.GameId))
            .Select(role => role.Id)
            .ToArray();
        var response = builder.BuildGameSignResult(
            context,
            await signService.ExecuteGameSignAsync(
                account,
                roleIds,
                includeMissingConfigMessage: true,
                cancellationToken: cancellationToken,
                progress: context.Progress));
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private async Task<CommandResponse> ExecuteGameSignBackAsync(
        CommandContext context,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
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
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var signService = scope.ServiceProvider.GetRequiredService<SklandSignService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到森空岛账号。");
        }

        var results = new List<(SklandAccount, IReadOnlyList<string>)>();
        foreach (var account in accounts)
        {
            try
            {
                var result = await signService.ExecuteGameSignAsync(
                    account,
                    includeMissingConfigMessage: true,
                    cancellationToken: cancellationToken,
                    progress: context.Progress);
                results.Add((account, result.Lines));
            }
            catch (Exception exception)
            {
                results.Add((account, ["签到失败：" + exception.GetBaseException().Message]));
            }
        }

        return builder.BuildCombinedGameSignResult(context, results, editMessageId);
    }

    private async Task<CommandResponse> ExecuteAutoSignRootMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        // 旧按钮的 payload 里没有 Page，读不出来时回落到第 0 页而不是报错，
        // 免得重启后 TTL 内的存量按钮集体失效。
        var page = CallbackActionStore.ReadData<SklandAutoSignMenuCallbackData>(action)?.Page ?? 0;
        return await builder.BuildAutoSignPanelAsync(context, accounts, editMessageId, cancellationToken, page);
    }

    private async Task<CommandResponse> ExecuteAutoSignAccountMenuAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<SklandAutoSignMenuCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ListByOwnerAsync(
            context.Identity.CoreUserId,
            noTracking: true,
            cancellationToken);
        return await builder.BuildAutoSignAccountPanelAsync(
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
        var data = CallbackActionStore.ReadData<SklandAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();

        // 主菜单的「开启/关闭全部」：翻转后回到主菜单本身，而不是某个账号详情。
        if (data.ToggleAll)
        {
            var all = await accountService.ToggleAllAutoSignAsync(context.Identity.CoreUserId, cancellationToken);
            return all.Count == 0
                ? CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。")
                : await builder.BuildAutoSignPanelAsync(context, all, editMessageId, cancellationToken, data.Page);
        }

        var accounts = await accountService.ToggleAutoSignAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        return await builder.BuildAutoSignAccountPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken,
            data.Page);
    }

    private async Task<CommandResponse> ExecuteGameAutoSignToggleAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<SklandGameAutoSignCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ToggleGameAutoSignAsync(
            context.Identity.CoreUserId,
            data.RoleId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛角色。");
        }

        var accountId = data.AccountId == 0
            ? accounts.FirstOrDefault(account => account.Roles.Any(role => role.Id == data.RoleId))?.Id ?? 0
            : data.AccountId;
        if (accountId == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        return await builder.BuildAutoSignAccountPanelAsync(
            context,
            accounts,
            accountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteGameAutoSignToggleAllAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<SklandGameAutoSignToggleAllCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var builder = scope.ServiceProvider.GetRequiredService<SklandResponseBuilder>();
        var accounts = await accountService.ToggleAllGameAutoSignAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        if (accounts.Count == 0)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        return await builder.BuildAutoSignAccountPanelAsync(
            context,
            accounts,
            data.AccountId,
            editMessageId,
            cancellationToken);
    }

    private async Task<CommandResponse> ExecuteDeleteSelectAsync(
        CommandContext context,
        CallbackAction action,
        string editMessageId,
        CancellationToken cancellationToken)
    {
        var data = CallbackActionStore.ReadData<SklandAccountCallbackData>(action);
        if (data is null)
        {
            return CallbackError(context.Identity, editMessageId, "按钮数据无效。");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var callbackStore = scope.ServiceProvider.GetRequiredService<CallbackActionStore>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        var response = CommandResponses.Text($"确认删除森空岛账号绑定？\n账号：`{account.DisplayName}`", context);
        response.AsTelegramEdit(editMessageId);
        response.AddButtonRow(new ResponseButtonRow
        {
            Buttons =
            {
                new ResponseButton
                {
                    Text = "确认删除",
                    Payload = await callbackStore.PutAsync(
                        "skland-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new SklandDeleteConfirmCallbackData(account.Id, Confirm: true),
                        cancellationToken: cancellationToken)
                },
                new ResponseButton
                {
                    Text = "取消",
                    Payload = await callbackStore.PutAsync(
                        "skland-delete-confirm",
                        context.Identity.CoreUserId,
                        context.Request.ChatId,
                        context.Request.UserId,
                        new SklandDeleteConfirmCallbackData(account.Id, Confirm: false),
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
        var data = CallbackActionStore.ReadData<SklandDeleteConfirmCallbackData>(action);
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
        var accountService = scope.ServiceProvider.GetRequiredService<SklandAccountService>();
        var account = await accountService.FindByIdAsync(data.AccountId, noTracking: true, cancellationToken);
        if (account is null || account.CoreUserId != context.Identity.CoreUserId)
        {
            return CallbackError(context.Identity, editMessageId, "未找到指定森空岛账号。");
        }

        var deleted = await accountService.DeleteAsync(
            context.Identity.CoreUserId,
            data.AccountId,
            cancellationToken);
        var response = CommandResponses.Text(
            deleted ? $"已删除森空岛账号绑定：`{account.DisplayName}`" : "未找到指定森空岛账号",
            context);
        response.AsTelegramEdit(editMessageId);
        return response;
    }

    private static CommandResponse CallbackError(
        ResolvedIdentity identity,
        string editMessageId,
        string message)
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
