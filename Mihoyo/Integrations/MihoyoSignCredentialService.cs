using OhMyBot.Core.Commanding.Commands;
using OhMyBot.Core.Infrastructure.Data.Entities;

namespace OhMyBot.Core.Integrations.Mihoyo;

public sealed class MihoyoSignCredentialService(MihoyoHttpClient client, MihoyoAccountService accountService)
{
    public async Task<string> RefreshCnCookieAsync(MihoyoAccount account, MihoyoCredential credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(credential.Stoken))
        {
            throw new CommandUserException("MihoyoCookieExpired", "Cookie 已失效且缺少 stoken，请重新绑定米游社账号");
        }

        var response = await client.RefreshCookieTokenAsync(credential.StokenCookie, cancellationToken);
        if (response.Retcode == MihoyoHttpClient.CookieExpiredCode)
        {
            await accountService.ClearStokenAsync(account.Id, cancellationToken);
            account.StokenCiphertext = string.Empty;
            throw new CommandUserException("MihoyoStokenExpired", "stoken 已失效，请重新绑定米游社账号");
        }

        if (!response.Ok || string.IsNullOrEmpty(response.Data?.CookieToken))
        {
            throw new CommandUserException("MihoyoCookieRefreshFailed", "Cookie 续期失败，请稍后重试或重新绑定米游社账号");
        }

        var cookie = MihoyoAccountService.SetCookieToken(credential.Cookie, response.Data.CookieToken);
        await accountService.UpdateCookieAsync(account.Id, cookie, cancellationToken);
        var updated = await accountService.FindByIdAsync(account.Id, noTracking: true, cancellationToken);
        if (updated is not null)
        {
            account.CookieCiphertext = updated.CookieCiphertext;
        }

        return cookie;
    }
}
