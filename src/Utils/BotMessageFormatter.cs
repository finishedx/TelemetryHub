namespace TelegramMonitor;

public static class BotMessageFormatter
{
    public static string FormatNotifyMessage(int accountId, TelegramMessageRecord record, List<KeywordConfig> matchedRules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<b>🎯 关键词命中通知</b>");
        sb.AppendLine();
        sb.AppendLine($"<b>账号:</b> {accountId}号");

        var chatDisplay = HtmlEncode(record.ChatTitle ?? "-");
        var chatUsername = NormalizeUsername(record.ChatUsername);
        if (chatUsername != null)
            chatDisplay += $" @{HtmlEncode(chatUsername)}";
        sb.AppendLine($"<b>消息来源:</b> {chatDisplay}");

        var senderDisplay = HtmlEncode(record.SenderTitle ?? "-");
        sb.AppendLine($"<b>发送者:</b> {senderDisplay}");
        sb.AppendLine($"<b>用户名:</b> {(NormalizeUsername(record.SenderUsername) is { } senderUsername ? $"@{HtmlEncode(senderUsername)}" : "无用户名")}");
        var senderIdDisplay = record.SenderId.HasValue
            ? $"<code>{record.SenderId.Value}</code>"
            : "-";
        sb.AppendLine($"<b>用户 ID:</b> {senderIdDisplay}");

        var keywordNames = matchedRules
            .Select(r => string.IsNullOrWhiteSpace(r.RuleName) ? r.KeywordPattern : r.RuleName)
            .Select(HtmlEncode);
        sb.AppendLine($"<b>命中关键词:</b> {string.Join(", ", keywordNames)}");

        var timeStr = record.MessageDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        sb.AppendLine($"<b>时间:</b> {timeStr}");

        var link = BuildMessageLink(record);
        if (link != null)
            sb.AppendLine($"<b>原消息:</b> <a href=\"{HtmlEncode(link)}\">查看原消息</a>");

        sb.AppendLine();
        sb.AppendLine("<b>💬 消息内容:</b>");
        sb.AppendLine($"<blockquote>{HtmlEncode(Truncate(record.Text, 500) ?? "-")}</blockquote>");
        return sb.ToString();
    }

    public static BotCallbackActions BuildCallbackActions(int accountId, TelegramMessageRecord record)
    {
        var messageLink = BuildMessageLink(record);

        return new BotCallbackActions(
            BuildPrimaryAction(record, messageLink),
            record.SenderId.HasValue ? BuildCallbackData("blku", accountId, record.Id) : null,
            record.ChatId.HasValue && !string.Equals(record.ChatType, "User", StringComparison.OrdinalIgnoreCase)
                ? BuildCallbackData("blkg", accountId, record.Id)
                : null,
            !string.IsNullOrEmpty(record.Text) ? BuildCallbackData("blkc", accountId, record.Id) : null);
    }

    // 首位按钮按可用信息依次降级：联系用户 → 立即前往 → 复制 ID
    private static BotPrimaryAction? BuildPrimaryAction(TelegramMessageRecord record, string? messageLink)
    {
        if (NormalizeUsername(record.SenderUsername) is { } username)
            return new BotPrimaryAction(BotPrimaryActionKind.Url, "👤 联系用户", $"https://t.me/{username}");

        if (messageLink != null && !IsPrivateMessageLink(messageLink))
            return new BotPrimaryAction(BotPrimaryActionKind.Url, "🚀 立即前往", messageLink);

        if (!record.SenderId.HasValue)
            return null;

        // 私密群组链接对非成员无效，此时改为复制用户 ID
        return new BotPrimaryAction(BotPrimaryActionKind.CopyText, "📋 复制ID", record.SenderId.Value.ToString());
    }

    private static bool IsPrivateMessageLink(string link) =>
        link.StartsWith("https://t.me/c/", StringComparison.OrdinalIgnoreCase);

    private static string? BuildCallbackData(string action, int accountId, int recordId)
    {
        var data = $"{action}:{accountId}:{recordId}";
        return data.Length <= 64 ? data : null;
    }

    private static string? BuildMessageLink(TelegramMessageRecord record)
    {
        if (record.TelegramMessageId <= 0 || record.ChatId == null)
            return null;

        // 普通群和私聊不支持此类消息深链，即使私聊对象有用户名也不能拼接消息 ID。
        if (record.ChatType is not ("Group" or "Channel" or "Supergroup" or "PeerChannel"))
            return null;

        if (NormalizeUsername(record.ChatUsername) is { } username)
            return $"https://t.me/{username}/{record.TelegramMessageId}";

        var chatId = record.ChatId.Value;
        if (chatId < -1000000000000L)
            chatId = -(chatId + 1000000000000L);

        if (chatId <= 0)
            return null;

        return $"https://t.me/c/{chatId}/{record.TelegramMessageId}";
    }

    private static string? NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var normalized = username.Trim().TrimStart('@');
        return Regex.IsMatch(normalized, "^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)
            ? normalized
            : null;
    }

    private static string HtmlEncode(string? value) =>
        string.IsNullOrEmpty(value) ? "-" : value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}

public enum BotPrimaryActionKind
{
    Url,
    CopyText
}

public record BotPrimaryAction(BotPrimaryActionKind Kind, string Text, string Value);

public record BotCallbackActions(
    BotPrimaryAction? Primary,
    string? BlockUser,
    string? BlockChat,
    string? BlockContent);
