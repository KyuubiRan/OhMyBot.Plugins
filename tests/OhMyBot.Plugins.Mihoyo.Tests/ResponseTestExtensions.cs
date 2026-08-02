using OhMyBot.Contracts.Grpc;

namespace OhMyBot.Plugins.Mihoyo.Tests;

internal static class ResponseTestExtensions
{
    public static IReadOnlyList<TelegramMessage> TgMessages(this CommandResponse response) => response.Telegram.Messages;

    public static TelegramMessage TgSingle(this CommandResponse response) => response.Telegram.Messages.Single();

    public static string TgText(this CommandResponse response) => response.Telegram.Messages.Single().Text;

    public static IReadOnlyList<ResponseButtonRow> TgButtonRows(this CommandResponse response) => response.Telegram.Messages[0].ButtonRows;

    public static IReadOnlyList<string> TgButtonTexts(this CommandResponse response) =>
        response.Telegram.Messages.SelectMany(message => message.ButtonRows).SelectMany(row => row.Buttons).Select(button => button.Text).ToList();

    public static IReadOnlyList<string> QqLines(this CommandResponse response) =>
        response.Qq.Messages.Select(message => message.Text).ToList();

    public static string QqText(this CommandResponse response) => response.Qq.Messages.Single().Text;
}
