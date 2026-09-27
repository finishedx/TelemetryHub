using Microsoft.Data.Sqlite;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramMonitor;

public class BotService : IBotService, IAsyncDisposable
{
    private readonly IBotNotifyTargetRepository _targetRepository;
    private readonly IKeywordRepository _keywordRepository;
    private readonly ITelegramMessageArchiveService _messageArchive;
    private readonly ILogger<BotService> _logger;
    private readonly BotOptions _options;

    private readonly List<WTelegram.Bot> _bots = [];
    private readonly List<SqliteConnection> _dbConnections = [];
    private int _roundRobinIndex = -1;

    public BotService(
        IBotNotifyTargetRepository targetRepository,
        IKeywordRepository keywordRepository,
        ITelegramMessageArchiveService messageArchive,
        ILogger<BotService> logger)
    {
        _targetRepository = targetRepository;
        _keywordRepository = keywordRepository;
        _messageArchive = messageArchive;
        _logger = logger;
        _options = App.GetConfig<BotOptions>("Bot") ?? new BotOptions();
    }

    public bool IsEnabled => _options.Enabled && _options.Tokens.Count > 0;

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            _logger.LogInformation("Bot 未启用，跳过初始化");
            return;
        }

        var telegramOptions = App.GetConfig<TelegramOptions>("Telegram") ?? new TelegramOptions();

        for (var i = 0; i < _options.Tokens.Count; i++)
        {
            var token = _options.Tokens[i];
            if (string.IsNullOrWhiteSpace(token))
                continue;

            try
            {
                var dbConn = new SqliteConnection($"Data Source=wtelegrambot_{i}.db");
                var bot = new WTelegram.Bot(
                    token,
                    telegramOptions.DefaultApiId,
                    telegramOptions.DefaultApiHash,
                    dbConn);

                TelegramProxyFactory.Apply(bot.Client, telegramOptions.Proxy);

                bot.OnUpdate += update => OnUpdateReceived(bot, update);
                bot.OnError += (ex, source) => OnBotError(bot, ex, source);

                var me = await bot.GetMe();
                _bots.Add(bot);
                _dbConnections.Add(dbConn);

                _logger.LogInformation("Bot[{Index}] @{Username} 已连接", i, me.Username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bot[{Index}] 初始化失败", i);
            }
        }

        _logger.LogInformation("共初始化 {Count}/{Total} 个 Bot", _bots.Count, _options.Tokens.Count);
    }

    public async Task<BotStatusDto> GetStatusAsync()
    {
        if (!IsEnabled)
            return new BotStatusDto(false, 0, 0, []);

        var usernames = new List<string>();
        var connected = 0;

        foreach (var bot in _bots)
        {
            try
            {
                var me = await bot.GetMe();
                usernames.Add(me.Username ?? "unknown");
                connected++;
            }
            catch
            {
                usernames.Add("(离线)");
            }
        }

        return new BotStatusDto(true, _bots.Count, connected, usernames);
    }

    public async Task<BotNotifyTarget> ValidateAndAddTargetAsync(string chatIdentifier, string? remark)
    {
        if (!IsEnabled || _bots.Count == 0)
            throw Oops.Oh("Bot 未启用或未初始化，请先在配置中设置 Bot Tokens 并启用");

        if (string.IsNullOrWhiteSpace(chatIdentifier))
            throw Oops.Oh("请输入 Chat ID 或 @用户名");

        chatIdentifier = chatIdentifier.Trim();
        var chatInput = long.TryParse(chatIdentifier, out var numericId)
            ? (Telegram.Bot.Types.ChatId)numericId
            : (Telegram.Bot.Types.ChatId)(chatIdentifier.StartsWith('@') ? chatIdentifier : $"@{chatIdentifier}");

        Telegram.Bot.Types.ChatFullInfo? chatInfo = null;
        foreach (var bot in _bots)
        {
            try
            {
                chatInfo = await bot.GetChat(chatInput);
                break;
            }
            catch
            {
            }
        }

        if (chatInfo == null)
            throw Oops.Oh("所有 Bot 均无法获取该会话信息，请确认 Chat ID 或 @用户名 正确且至少一个 Bot 已加入该会话");

        var chatId = chatInfo.Id;
        var existing = await _targetRepository.FindByChatIdAsync(chatId);
        if (existing != null)
            throw Oops.Oh($"该会话已存在: {existing.ChatTitle} (ID: {chatId})");

        var chatType = chatInfo.Type.ToString().ToLowerInvariant();
        var chatTitle = chatInfo.Title ?? chatInfo.FirstName ?? "";
        var chatUsername = chatInfo.Username;

        var failedBots = new List<string>();

        for (var i = 0; i < _bots.Count; i++)
        {
            try
            {
                var bot = _bots[i];
                var me = await bot.GetMe();

                await bot.GetChat(chatInput);
                if (chatInfo.Type is ChatType.Group or ChatType.Supergroup or ChatType.Channel)
                {
                    var member = await bot.GetChatMember(chatId, me.Id);
                    if (!HasSendPermission(chatInfo.Type, member))
                    {
                        failedBots.Add($"Bot[{i}] @{me.Username ?? "unknown"} (状态: {member.Status}，当前无发消息权限)");
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is not AppFriendlyException)
            {
                failedBots.Add($"Bot[{i}] (检查失败: {ex.Message})");
            }
        }

        if (failedBots.Count > 0)
            throw Oops.Oh($"以下 Bot 未全部通过目标校验，请处理后重试：\n{string.Join("\n", failedBots)}");

        var target = new BotNotifyTarget
        {
            ChatId = chatId,
            ChatTitle = chatTitle,
            ChatUsername = chatUsername,
            ChatType = chatType,
            IsEnabled = true,
            Remark = remark?.Trim()
        };

        return await _targetRepository.AddAsync(target);
    }

    public async Task<int> SendNotifyMessageAsync(long chatId, string htmlText, BotCallbackActions callbackActions)
    {
        if (_bots.Count == 0)
            throw new InvalidOperationException("Bot 未初始化");

        var startIndex = (int)((uint)Interlocked.Increment(ref _roundRobinIndex) % _bots.Count);

        var buttonRows = new List<InlineKeyboardButton[]>();

        if (BuildPrimaryButton(callbackActions.Primary) is { } primaryButton)
            buttonRows.Add(new[] { primaryButton });

        AddCallbackButton(buttonRows, "🚫 屏蔽此人", callbackActions.BlockUser);
        AddCallbackButton(buttonRows, "🚫 屏蔽此群组", callbackActions.BlockChat);
        AddCallbackButton(buttonRows, "🚫 屏蔽此内容", callbackActions.BlockContent);
        InlineKeyboardMarkup? replyMarkup = buttonRows.Count == 0
            ? null
            : new InlineKeyboardMarkup(buttonRows.ToArray());

        Exception? lastException = null;
        for (var offset = 0; offset < _bots.Count; offset++)
        {
            var index = (startIndex + offset) % _bots.Count;
            var bot = _bots[index];

            try
            {
                var sent = await SendMessageAsync(
                    bot,
                    chatId,
                    htmlText,
                    ParseMode.Html,
                    replyMarkup);

                return sent.MessageId;
            }
            catch (Exception htmlException)
            {
                _logger.LogWarning(htmlException, "Bot[{Index}] HTML 通知发送到 {ChatId} 失败，尝试纯文本降级", index, chatId);

                try
                {
                    var fallback = await SendMessageAsync(
                        bot,
                        chatId,
                        StripHtml(htmlText),
                        ParseMode.None,
                        replyMarkup);

                    return fallback.MessageId;
                }
                catch (Exception fallbackException)
                {
                    lastException = fallbackException;
                    _logger.LogWarning(fallbackException, "Bot[{Index}] 纯文本通知发送到 {ChatId} 也失败，准备尝试下一个 Bot", index, chatId);
                }
            }
        }

        if (lastException != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new InvalidOperationException("没有可用的 Bot 可用于发送通知");
    }

    private static Task<WTelegram.Types.Message> SendMessageAsync(
        WTelegram.Bot bot,
        long chatId,
        string text,
        ParseMode parseMode,
        InlineKeyboardMarkup? replyMarkup) =>
        bot.SendMessage(
            chatId,
            text,
            parseMode: parseMode,
            replyMarkup: replyMarkup,
            linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true });

    private static string StripHtml(string htmlText)
    {
        var withLineBreaks = Regex.Replace(htmlText, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        var withLinks = Regex.Replace(
            withLineBreaks,
            "<a\\s+href=[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
            "$2 ($1)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(withLinks, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }

    private static InlineKeyboardButton? BuildPrimaryButton(BotPrimaryAction? primary)
    {
        if (primary == null || string.IsNullOrWhiteSpace(primary.Value))
            return null;

        return primary.Kind switch
        {
            BotPrimaryActionKind.Url => InlineKeyboardButton.WithUrl(primary.Text, primary.Value),
            BotPrimaryActionKind.CopyText => InlineKeyboardButton.WithCopyText(
                primary.Text,
                new Telegram.Bot.Types.CopyTextButton { Text = primary.Value }),
            _ => null
        };
    }

    private static void AddCallbackButton(
        List<InlineKeyboardButton[]> buttonRows,
        string text,
        string? callbackData)
    {
        if (!string.IsNullOrWhiteSpace(callbackData))
            buttonRows.Add(new[] { InlineKeyboardButton.WithCallbackData(text, callbackData) });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bot in _bots)
            bot.Dispose();
        _bots.Clear();

        foreach (var conn in _dbConnections)
            await conn.DisposeAsync();
        _dbConnections.Clear();
    }

    private async Task OnUpdateReceived(WTelegram.Bot bot, Telegram.Bot.Types.Update update)
    {
        if (update.CallbackQuery is { Data: { } data } cq)
        {
            await HandleCallbackAsync(bot, cq, data);
        }
    }

    private Task OnBotError(WTelegram.Bot bot, Exception ex, Telegram.Bot.Polling.HandleErrorSource source)
    {
        var botIndex = _bots.IndexOf(bot);
        _logger.LogWarning(ex, "Bot[{Index}] 错误 (来源: {Source})", botIndex, source);
        return Task.CompletedTask;
    }

    private async Task HandleCallbackAsync(WTelegram.Bot answerBot, Telegram.Bot.Types.CallbackQuery cq, string data)
    {
        if (data.StartsWith("blk:", StringComparison.Ordinal))
        {
            await HandleLegacyBlockUserCallbackAsync(answerBot, cq, data);
            return;
        }

        var parts = data.Split(':');
        if (parts.Length != 3 || parts[0] is not ("blku" or "blkg" or "blkc"))
        {
            await AnswerCallbackAsync(answerBot, cq.Id, "未知操作");
            return;
        }

        try
        {
            if (!int.TryParse(parts[1], out var accountId) ||
                !int.TryParse(parts[2], out var recordId))
            {
                await AnswerCallbackAsync(answerBot, cq.Id, "按钮参数无效");
                return;
            }

            var record = await _messageArchive.FindByIdAsync(recordId);
            if (record == null || record.AccountId != accountId)
            {
                await AnswerCallbackAsync(answerBot, cq.Id, "原消息记录不存在");
                return;
            }

            var (keyword, successMessage) = parts[0] switch
            {
                "blku" => BuildBlockUserRule(accountId, record),
                "blkg" => BuildBlockChatRule(accountId, record),
                "blkc" => BuildBlockContentRule(accountId, record),
                _ => throw Oops.Oh("未知操作")
            };

            await _keywordRepository.AddAsync(keyword);
            await AnswerCallbackAsync(answerBot, cq.Id, successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理 Bot 回调失败: {Data}", data);
            await AnswerCallbackAsync(answerBot, cq.Id, $"操作失败: {ex.Message}");
        }
    }

    private async Task HandleLegacyBlockUserCallbackAsync(
        WTelegram.Bot answerBot,
        Telegram.Bot.Types.CallbackQuery cq,
        string data)
    {
        try
        {
            var parts = data.Split(':');
            if (parts.Length < 3 || !long.TryParse(parts[2], out var senderId))
            {
                await AnswerCallbackAsync(answerBot, cq.Id, "发送者 ID 无效");
                return;
            }

            int? accountId = parts[1] == "g"
                ? null
                : int.TryParse(parts[1], out var aid) ? aid : null;

            await _keywordRepository.AddAsync(BuildBaseBlockRule(
                accountId,
                $"屏蔽用户 {senderId}",
                true,
                $"^{senderId}$"));
            await AnswerCallbackAsync(answerBot, cq.Id, $"已屏蔽用户 {senderId}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理旧版 Bot 回调失败: {Data}", data);
            await AnswerCallbackAsync(answerBot, cq.Id, $"操作失败: {ex.Message}");
        }
    }

    private static (KeywordConfig Rule, string Message) BuildBlockUserRule(
        int accountId,
        TelegramMessageRecord record)
    {
        if (!record.SenderId.HasValue)
            throw Oops.Oh("该消息没有可屏蔽的发送者");

        var senderId = record.SenderId.Value;
        return (
            BuildBaseBlockRule(accountId, $"屏蔽用户 {senderId}", true, $"^{senderId}$"),
            $"已屏蔽用户 {senderId}");
    }

    private static (KeywordConfig Rule, string Message) BuildBlockChatRule(
        int accountId,
        TelegramMessageRecord record)
    {
        if (!record.ChatId.HasValue || string.Equals(record.ChatType, "User", StringComparison.OrdinalIgnoreCase))
            throw Oops.Oh("该消息不属于可屏蔽的群组或频道");

        var chatTitle = string.IsNullOrWhiteSpace(record.ChatTitle)
            ? record.ChatId.Value.ToString()
            : record.ChatTitle;
        var rule = BuildBaseBlockRule(accountId, $"屏蔽群组 {chatTitle}");
        rule.ChatId = record.ChatId.Value;
        return (rule, $"已屏蔽群组 {chatTitle}");
    }

    private static (KeywordConfig Rule, string Message) BuildBlockContentRule(
        int accountId,
        TelegramMessageRecord record)
    {
        if (string.IsNullOrEmpty(record.Text))
            throw Oops.Oh("该消息没有可屏蔽的文本内容");

        var preview = record.Text.Length <= 30 ? record.Text : record.Text[..30] + "...";
        var rule = BuildBaseBlockRule(accountId, $"屏蔽内容 {preview}");
        rule.ExactContent = record.Text;
        rule.IsCaseSensitive = true;
        return (rule, "已屏蔽完全相同的消息内容");
    }

    private static KeywordConfig BuildBaseBlockRule(
        int? accountId,
        string ruleName,
        bool isMatchUser = false,
        string? userPattern = null) =>
        new()
        {
            AccountId = accountId,
            RuleName = ruleName,
            KeywordPattern = KeywordPatternBuilder.MatchAllPattern,
            MatchMode = KeywordMatchMode.Regex,
            IsMatchUser = isMatchUser,
            UserPattern = userPattern,
            KeywordAction = KeywordAction.Exclude,
            IsCaseSensitive = false,
            IsEnabled = true,
            Priority = -1,
            Remark = "由 Bot 按钮自动创建"
        };

    private static bool HasSendPermission(ChatType chatType, Telegram.Bot.Types.ChatMember member) =>
        chatType switch
        {
            ChatType.Channel => member switch
            {
                Telegram.Bot.Types.ChatMemberOwner => true,
                Telegram.Bot.Types.ChatMemberAdministrator administrator => administrator.CanPostMessages == true,
                _ => false
            },
            ChatType.Group or ChatType.Supergroup => member switch
            {
                Telegram.Bot.Types.ChatMemberAdministrator => true,
                Telegram.Bot.Types.ChatMemberOwner => true,
                Telegram.Bot.Types.ChatMemberMember => true,
                Telegram.Bot.Types.ChatMemberRestricted restricted => restricted.CanSendMessages,
                _ => false
            },
            _ => true
        };

    private async Task AnswerCallbackAsync(WTelegram.Bot bot, string callbackQueryId, string text)
    {
        try
        {
            await bot.AnswerCallbackQuery(callbackQueryId, text, showAlert: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "回复 callback query 失败");
        }
    }
}
