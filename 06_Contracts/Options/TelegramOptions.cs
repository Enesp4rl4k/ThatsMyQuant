namespace ThatsMyQuant.Contracts.Options;

public sealed class TelegramOptions
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId   { get; set; } = string.Empty;

    /// <summary>
    /// Enables the read-only Telegram command bot (/status, /pnl, /positions, /regime, /help).
    /// Only replies to chats listed in <see cref="AllowedChatIds"/>; if empty, replies only to <see cref="ChatId"/>.
    /// </summary>
    public bool EnableReadBot { get; set; } = false;

    /// <summary>
    /// Comma-separated list of additional chat IDs allowed to invoke read-only commands.
    /// </summary>
    public string AllowedChatIds { get; set; } = string.Empty;
}
