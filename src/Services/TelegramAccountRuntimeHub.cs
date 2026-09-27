using System.Collections.Concurrent;

namespace TelegramMonitor;

public class TelegramAccountRuntimeHub : ITelegramAccountRuntimeHub
{
    private readonly ConcurrentDictionary<int, TelegramAccountRuntime> _runtimes = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly ITelegramAccountRepository _accountRepository;
    private readonly ITelegramMessageArchiveService _messageArchive;
    private readonly IKeywordRepository _keywordRepository;
    private readonly IBotNotifyTargetRepository _botNotifyTargetRepository;
    private readonly BotNotifyChannel _notifyChannel;
    private readonly ILogger<TelegramAccountRuntimeHub> _logger;
    private bool _disposed;

    // 同一发送者在该时间窗口内只做一次实体补齐，避免对确实没有用户名的用户重复请求
    private static readonly TimeSpan SenderEnrichCooldown = TimeSpan.FromHours(6);

    public TelegramAccountRuntimeHub(
        ITelegramAccountRepository accountRepository,
        ITelegramMessageArchiveService messageArchive,
        IKeywordRepository keywordRepository,
        IBotNotifyTargetRepository botNotifyTargetRepository,
        BotNotifyChannel notifyChannel,
        ILogger<TelegramAccountRuntimeHub> logger)
    {
        _accountRepository = accountRepository;
        _messageArchive = messageArchive;
        _keywordRepository = keywordRepository;
        _botNotifyTargetRepository = botNotifyTargetRepository;
        _notifyChannel = notifyChannel;
        _logger = logger;
        _logger.LogInformation(
            "本次启动时刻: {StartedAt:yyyy-MM-dd HH:mm:ss}，早于该时刻或缺少时间的消息一律丢弃",
            ChinaTime.ToChinaTime(RealtimeMessagePolicy.StartedAtUtc));
    }

    public IReadOnlyList<TelegramRuntimeSnapshot> GetRuntimeSnapshots() =>
        _runtimes.Values
            .Select(runtime =>
            {
                var connected = runtime.IsAuthorized && !runtime.Client.Disconnected;
                var monitoring = runtime.IsMonitoring && connected;
                return new TelegramRuntimeSnapshot(
                    runtime.Account.Id,
                    connected,
                    monitoring,
                    runtime.LastMessageAt);
            })
            .OrderBy(x => x.AccountId)
            .ToList();

    public async Task<Client> GetOrCreateClientAsync(TelegramAccount account)
    {
        if (_runtimes.TryGetValue(account.Id, out var existing))
        {
            existing.Account = account;
            return existing.Client;
        }

        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            if (_runtimes.TryGetValue(account.Id, out existing))
            {
                existing.Account = account;
                return existing.Client;
            }

            EnsureDirectories(account);

            string Config(string what) => what switch
            {
                "api_id" => account.ApiId.ToString(),
                "api_hash" => account.ApiHash,
                "session_pathname" => account.SessionPath,
                "session_key" => string.IsNullOrWhiteSpace(account.SessionKey) ? account.ApiHash : account.SessionKey,
                "phone_number" => string.IsNullOrWhiteSpace(account.Phone) ? null! : account.Phone,
                "user_id" => account.UserId?.ToString() ?? null!,
                // 以下 proxy_* 键是无效的：WTelegramClient 不识别它们，代理由 TcpHandler 承担
                //"proxy_server" => string.IsNullOrWhiteSpace(account.ProxyServer) ? null! : account.ProxyServer,
                //"proxy_port" => account.ProxyPort is > 0 ? account.ProxyPort.Value.ToString() : null!,
                //"proxy_username" => string.IsNullOrWhiteSpace(account.ProxyUsername) ? null! : account.ProxyUsername,
                //"proxy_password" => string.IsNullOrWhiteSpace(account.ProxyPassword) ? null! : account.ProxyPassword,
                //"proxy_secret" => string.IsNullOrWhiteSpace(account.ProxySecret) ? null! : account.ProxySecret,
                _ => null!
            };

            var client = new Client(Config);
            TelegramProxyFactory.Apply(client, App.GetConfig<TelegramOptions>("Telegram")?.Proxy);

            var runtime = new TelegramAccountRuntime(account, client);
            _runtimes[account.Id] = runtime;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TelegramLoginResult> StartLoginAsync(TelegramAccount account)
    {
        try
        {
            var client = await GetOrCreateClientAsync(account);
            var normalizedPhone = account.Phone.NormalizeTelegramPhone();
            var step = await client.Login(normalizedPhone);
            return await BuildLoginResultAsync(account.Id, step, client);
        }
        catch (Exception ex)
        {
            var userMessage = TelegramRpcErrorCatalog.GetExceptionMessage(ex);
            _logger.LogWarning(ex, "账号 {AccountId} 发起登录失败", account.Id);
            await _accountRepository.UpdateLastErrorAsync(account.Id, userMessage);
            return new TelegramLoginResult(account.Id, LoginState.Other, $"发起登录失败：{userMessage}");
        }
    }

    public async Task<TelegramLoginResult> SubmitCodeAsync(TelegramAccount account, string code)
    {
        try
        {
            var client = await GetOrCreateClientAsync(account);
            var step = await client.Login((code ?? string.Empty).Trim());
            return await BuildLoginResultAsync(account.Id, step, client);
        }
        catch (Exception ex)
        {
            var userMessage = TelegramRpcErrorCatalog.GetExceptionMessage(ex);
            _logger.LogWarning(ex, "账号 {AccountId} 提交验证码失败", account.Id);
            await _accountRepository.UpdateLastErrorAsync(account.Id, userMessage);
            return new TelegramLoginResult(account.Id, LoginState.Other, $"验证码校验失败：{userMessage}");
        }
    }

    public async Task<TelegramLoginResult> SubmitPasswordAsync(TelegramAccount account, string password)
    {
        try
        {
            var client = await GetOrCreateClientAsync(account);
            var step = await client.Login(password ?? string.Empty);
            return await BuildLoginResultAsync(account.Id, step, client);
        }
        catch (Exception ex)
        {
            var userMessage = TelegramRpcErrorCatalog.GetExceptionMessage(ex);
            _logger.LogWarning(ex, "账号 {AccountId} 提交二步验证码失败", account.Id);
            await _accountRepository.UpdateLastErrorAsync(account.Id, userMessage);
            return new TelegramLoginResult(account.Id, LoginState.Other, $"二步验证失败：{userMessage}");
        }
    }

    public async Task<(bool Success, User? User, string? Error)> EnsureAuthorizedAsync(TelegramAccount account)
    {
        try
        {
            var client = await GetOrCreateClientAsync(account);
            await client.ConnectAsync();

            var self = client.User;
            if (self == null)
            {
                var users = await client.Users_GetUsers(InputUser.Self);
                self = users.OfType<User>().FirstOrDefault();
            }

            if (self == null)
            {
                if (_runtimes.TryGetValue(account.Id, out var runtimeMissing))
                    runtimeMissing.IsAuthorized = false;

                return (false, null, "会话尚未登录");
            }

            await _accountRepository.MarkAuthorizedAsync(account.Id, self);

            if (_runtimes.TryGetValue(account.Id, out var runtime))
            {
                runtime.IsAuthorized = true;
                runtime.Account = await _accountRepository.FindByIdAsync(account.Id) ?? account;
            }

            return (true, self, null);
        }
        catch (Exception ex)
        {
            var userMessage = TelegramRpcErrorCatalog.GetExceptionMessage(ex);
            _logger.LogWarning(ex, "账号 {AccountId} 鉴权失败", account.Id);
            await _accountRepository.UpdateLastErrorAsync(account.Id, userMessage);

            if (_runtimes.TryGetValue(account.Id, out var runtime))
                runtime.IsAuthorized = false;

            return (false, null, userMessage);
        }
    }

    public async Task<TelegramMonitorOperationResult> StartMonitoringAsync(TelegramAccount account)
    {
        var client = await GetOrCreateClientAsync(account);
        var runtime = _runtimes[account.Id];

        await runtime.SyncLock.WaitAsync();
        try
        {
            runtime.Account = account;

            if (runtime.IsMonitoring && runtime.IsAuthorized && !client.Disconnected)
                return new TelegramMonitorOperationResult(account.Id, true, "监听已在运行");

            if (runtime.Manager == null)
            {
                runtime.Manager = string.IsNullOrWhiteSpace(account.StatePath)
                    ? client.WithUpdateManager(update => HandleUpdateAsync(runtime, update), reentrant: true)
                    : client.WithUpdateManager(update => HandleUpdateAsync(runtime, update), account.StatePath, reentrant: true);
            }

            runtime.IsMonitoring = true;

            var authorized = await EnsureAuthorizedAsync(account);
            if (!authorized.Success)
            {
                runtime.IsMonitoring = false;
                return new TelegramMonitorOperationResult(account.Id, false, authorized.Error ?? "账号尚未登录");
            }

            var dialogs = await client.Messages_GetAllDialogs();
            dialogs.CollectUsersChats(runtime.Manager.Users, runtime.Manager.Chats);
            await runtime.Manager.LoadDialogs(dialogs);

            SaveStateQuietly(runtime);

            await _accountRepository.MarkMonitoringStartedAsync(account.Id);
            return new TelegramMonitorOperationResult(account.Id, true, "监听已启动");
        }
        catch (Exception ex)
        {
            var userMessage = TelegramRpcErrorCatalog.GetExceptionMessage(ex);
            runtime.IsMonitoring = false;
            _logger.LogWarning(ex, "账号 {AccountId} 启动监听失败", account.Id);
            await _accountRepository.UpdateLastErrorAsync(account.Id, userMessage);
            return new TelegramMonitorOperationResult(account.Id, false, $"启动监听失败：{userMessage}");
        }
        finally
        {
            runtime.SyncLock.Release();
        }
    }

    public async Task<TelegramMonitorOperationResult> StopMonitoringAsync(int accountId)
    {
        await RemoveClientAsync(accountId);
        return new TelegramMonitorOperationResult(accountId, true, "监听已停止");
    }

    public async Task RemoveClientAsync(int accountId)
    {
        if (!_runtimes.TryRemove(accountId, out var runtime))
        {
            _locks.TryRemove(accountId, out _);
            return;
        }

        try
        {
            runtime.IsMonitoring = false;
            runtime.IsAuthorized = false;

            if (runtime.Manager != null && !string.IsNullOrWhiteSpace(runtime.Account.StatePath))
                runtime.Manager.SaveState(runtime.Account.StatePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "账号 {AccountId} 保存更新状态失败", accountId);
        }

        await runtime.DisposeAsync();
        _locks.TryRemove(accountId, out _);
    }

    public async Task RemoveAllAsync()
    {
        foreach (var accountId in _runtimes.Keys.ToArray())
            await RemoveClientAsync(accountId);
    }

    public void SaveAllState()
    {
        foreach (var runtime in _runtimes.Values)
            SaveStateQuietly(runtime);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await RemoveAllAsync();

        foreach (var gate in _locks.Values)
            gate.Dispose();

        _locks.Clear();
    }

    private async Task<TelegramLoginResult> BuildLoginResultAsync(int accountId, string? step, Client client)
    {
        if (client.User != null)
        {
            await _accountRepository.MarkAuthorizedAsync(accountId, client.User);

            var account = await _accountRepository.FindByIdAsync(accountId);
            if (account != null && !account.MonitorEnabled)
                account = await _accountRepository.SetMonitoringEnabledAsync(accountId, true);

            if (_runtimes.TryGetValue(accountId, out var runtime))
                runtime.IsAuthorized = true;

            TelegramMonitorOperationResult? monitorResult = null;
            if (account != null)
                monitorResult = await StartMonitoringAsync(account);

            var loginMessage = monitorResult switch
            {
                { Success: true } => "登录成功，已自动启动监听",
                { Success: false } => $"登录成功，但自动启动监听失败：{monitorResult.Message}",
                _ => "登录成功"
            };

            return new TelegramLoginResult(
                accountId,
                LoginState.LoggedIn,
                loginMessage,
                account == null ? null : TelegramAccountAssembler.ToDto(account, GetRuntimeSnapshots()));
        }

        var state = step switch
        {
            "verification_code" => LoginState.WaitingForVerificationCode,
            "password" => LoginState.WaitingForPassword,
            "name" => LoginState.WaitingForName,
            null => LoginState.NotLoggedIn,
            _ => LoginState.Other
        };

        var message = step switch
        {
            "verification_code" => "需要输入验证码",
            "password" => "需要输入二步验证码",
            "name" => "当前会话需要补充注册信息",
            null => "尚未登录",
            _ => $"出现未处理的登录步骤: {step}"
        };

        return new TelegramLoginResult(accountId, state, message);
    }

    private async Task HandleUpdateAsync(TelegramAccountRuntime runtime, Update update)
    {
        if (runtime.Manager == null)
            return;

        try
        {
            _logger.LogDebug("账号 {AccountId} 收到更新回调: {UpdateType}", runtime.Account.Id, update.GetType().Name);

            if (TryResolveMessageUpdate(update, out var messageBase, out var isEdited))
            {
                await PersistMessageAsync(runtime, update.GetType().Name, messageBase, isEdited);
            }
            else
            {
                var updateTypeName = update.GetType().Name;
                if (updateTypeName.Contains("UpdateNew", StringComparison.OrdinalIgnoreCase) ||
                    updateTypeName.Contains("UpdateEdit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("账号 {AccountId} 收到 {UpdateType}，但未能解析 message 消息体", runtime.Account.Id, updateTypeName);
                }
                else
                {
                    _logger.LogDebug("账号 {AccountId} 收到未归档更新类型 {UpdateType}", runtime.Account.Id, updateTypeName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "处理账号 {AccountId} 的 Telegram 更新失败", runtime.Account.Id);
        }
    }

    private static bool TryResolveMessageUpdate(Update update, out MessageBase messageBase, out bool isEdited)
    {
        switch (update)
        {
            case UpdateNewMessage updateNew when updateNew.message != null:
                messageBase = updateNew.message;
                isEdited = false;
                return true;

            case UpdateEditMessage updateEdit when updateEdit.message != null:
                messageBase = updateEdit.message;
                isEdited = true;
                return true;
        }

        var updateType = update.GetType();
        var messageMember = updateType.GetMember("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault();
        var messageValue = messageMember switch
        {
            FieldInfo field => field.GetValue(update),
            PropertyInfo property => property.GetValue(update),
            _ => null
        };

        if (messageValue is MessageBase genericMessage)
        {
            messageBase = genericMessage;
            isEdited = updateType.Name.Contains("Edit", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        messageBase = null!;
        isEdited = false;
        return false;
    }

    private async Task PersistMessageAsync(
        TelegramAccountRuntime runtime,
        string updateType,
        MessageBase messageBase,
        bool isEdited)
    {
        if (runtime.Manager == null)
            return;

        if (!RealtimeMessagePolicy.ShouldProcess(messageBase.Date, RealtimeMessagePolicy.StartedAtUtc))
        {
            _logger.LogDebug(
                "账号 {AccountId} 跳过历史或缺少时间的消息: MessageId={MessageId}, MessageDate={MessageDate}",
                runtime.Account.Id,
                messageBase.ID,
                messageBase.Date);
            return;
        }

        var record = await _messageArchive.ArchiveAsync(runtime.Account.Id, runtime.Manager, updateType, messageBase, isEdited);
        runtime.LastMessageAt = ChinaTime.Now;

        await EnrichSenderAsync(runtime, record, messageBase);
        await _messageArchive.UpdateSenderInfoAsync(record);

        _logger.LogInformation(
            "账号 {AccountId} 已归档消息: UpdateType={UpdateType}, MessageId={MessageId}, IsEdited={IsEdited}",
            runtime.Account.Id,
            updateType,
            messageBase.ID,
            isEdited);

        await TryNotifyKeywordHitAsync(runtime, record);
    }

    private async Task TryNotifyKeywordHitAsync(TelegramAccountRuntime runtime, TelegramMessageRecord record)
    {
        try
        {
            var accountId = runtime.Account.Id;
            if (record.ChatId.HasValue)
            {
                var targets = await _botNotifyTargetRepository.ListEnabledAsync();
                if (targets.Any(t => IsSameTelegramChat(record.ChatId.Value, record.ChatType, t.ChatId)))
                    return;
            }

            var accountKeywords = await _keywordRepository.ListAsync(accountId);
            var globalKeywords = await _keywordRepository.ListAsync(null);
            var allKeywords = globalKeywords.Concat(accountKeywords).ToList();

            if (allKeywords.Count == 0)
                return;

            var senderUserNames = GetSenderUsernames(runtime.Manager, record.SenderId, record.SenderUsername);

            var matched = KeywordMatchExtensions.Match(
                record.Text,
                record.ChatId,
                record.SenderId,
                senderUserNames,
                allKeywords);

            var monitorHits = matched.Where(r => r.KeywordAction == KeywordAction.Monitor).ToList();
            var hasExclude = matched.Any(r => r.KeywordAction == KeywordAction.Exclude);

            if (monitorHits.Count > 0 && !hasExclude)
            {
                await EnqueueNotificationsAsync(accountId, record, monitorHits);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "账号 {AccountId} 关键词匹配或入队失败", runtime.Account.Id);
        }
    }

    private async Task EnrichSenderAsync(
        TelegramAccountRuntime runtime,
        TelegramMessageRecord record,
        MessageBase messageBase)
    {
        if (!record.SenderId.HasValue || runtime.Manager == null)
            return;

        var senderPeer = messageBase switch
        {
            Message message => message.From ?? message.Peer,
            MessageService service => service.from_id ?? service.peer_id,
            _ => null
        };

        // 用户和频道的数字 ID 可能相同，不能把频道发送者当作用户查询。
        if (senderPeer is not PeerUser)
            return;

        var senderId = record.SenderId.Value;

        runtime.Manager.Users.TryGetValue(senderId, out var sender);

        // 缓存未命中时向服务端补齐实体，等价于 Telethon event.get_sender() 的按需补齐行为
        if (sender == null)
            sender = await TryResolveSenderFromMessageAsync(runtime, record);

        if (sender == null)
            return;

        if (string.IsNullOrWhiteSpace(record.SenderTitle))
            record.SenderTitle = sender.DisplayName();

        if (string.IsNullOrWhiteSpace(record.SenderUsername))
            record.SenderUsername = GetPreferredUsername(sender);

        if (!string.IsNullOrWhiteSpace(record.SenderUsername))
            return;

        var refreshedSender = await TryRefreshSenderAsync(runtime, sender, record);
        if (refreshedSender == null)
            return;

        record.SenderTitle = refreshedSender.DisplayName();
        record.SenderUsername = GetPreferredUsername(refreshedSender);
    }

    // 通过原消息补齐实体；普通群和私聊使用 messages.getMessages，频道使用 channels.getMessages。
    private async Task<User?> TryResolveSenderFromMessageAsync(
        TelegramAccountRuntime runtime,
        TelegramMessageRecord record)
    {
        if (runtime.Manager == null || !record.SenderId.HasValue)
            return null;

        if (record.TelegramMessageId <= 0 || record.ChatId == null)
            return null;

        var isChannel = record.ChatType is "Group" or "Channel" or "Supergroup" or "PeerChannel";
        var channel = isChannel ? ResolveChannel(runtime.Manager, record.ChatId.Value) : null;
        if (isChannel && channel == null)
            return null;

        if (!isChannel && record.ChatType is not ("Chat" or "User" or "PeerChat" or "PeerUser"))
            return null;

        if (!TryBeginEnrichAttempt(runtime.SenderLookupAttempts, record.SenderId.Value))
            return null;

        try
        {
            var messageIds = new InputMessage[] { new InputMessageID { id = record.TelegramMessageId } };
            var messages = isChannel
                ? await runtime.Client.Channels_GetMessages(channel, messageIds)
                : await runtime.Client.Messages_GetMessages(messageIds);

            messages.CollectUsersChats(runtime.Manager.Users, runtime.Manager.Chats);
            return messages.UserOrChat(new PeerUser { user_id = record.SenderId.Value }) as User;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "账号 {AccountId} 重拉消息 {MessageId} 解析发送者 {SenderId} 失败",
                runtime.Account.Id,
                record.TelegramMessageId,
                record.SenderId);
            return null;
        }
    }

    // 已有实体但用户名仍为空时，向服务端刷新一次，用于获取用户后来新增的用户名
    private async Task<User?> TryRefreshSenderAsync(
        TelegramAccountRuntime runtime,
        User sender,
        TelegramMessageRecord record)
    {
        if (runtime.Manager == null)
            return null;

        try
        {
            var inputUser = BuildSenderInputUser(runtime.Manager, sender, record);
            if (inputUser == null || !TryBeginEnrichAttempt(runtime.SenderRefreshAttempts, sender.ID))
                return null;

            var users = await runtime.Client.Users_GetUsers(inputUser);
            var refreshedSender = users.OfType<User>().FirstOrDefault(user => user.ID == sender.ID);

            if (refreshedSender != null)
                runtime.Manager.Users[refreshedSender.ID] = refreshedSender;

            return refreshedSender;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "账号 {AccountId} 刷新发送者 {SenderId} 用户名失败", runtime.Account.Id, sender.ID);
            return null;
        }
    }

    private static InputUserBase? BuildSenderInputUser(
        UpdateManager manager,
        User sender,
        TelegramMessageRecord record)
    {
        if (sender.flags.HasFlag(User.Flags.self))
            return InputUser.Self;

        if (sender.access_hash != 0 && !sender.flags.HasFlag(User.Flags.min))
            return new InputUser(sender.ID, sender.access_hash);

        if (record.TelegramMessageId <= 0 || !record.ChatId.HasValue)
            return null;

        // min 实体的 access_hash 不能直接查询用户，必须携带该用户出现过的消息上下文。
        InputPeer? inputPeer = record.ChatType switch
        {
            "Group" or "Channel" or "Supergroup" or "PeerChannel" =>
                ResolveChannel(manager, record.ChatId.Value)?.ToInputPeer(),
            "Chat" or "PeerChat" => new InputPeerChat(record.ChatId.Value),
            _ => null
        };

        return inputPeer == null ? null : new InputUserFromMessage
        {
            peer = inputPeer,
            msg_id = record.TelegramMessageId,
            user_id = sender.ID
        };
    }

    private static Channel? ResolveChannel(UpdateManager manager, long chatId)
    {
        var channelId = chatId < 0 ? -chatId - 1000000000000L : chatId;
        if (channelId <= 0)
            return null;

        return manager.Chats.TryGetValue(channelId, out var chat) ? chat as Channel : null;
    }

    // 同一个发送者短期内只补齐一次，避免对无用户名的用户反复请求触发限流
    private static bool TryBeginEnrichAttempt(
        ConcurrentDictionary<long, DateTime> attempts,
        long senderId)
    {
        var now = DateTime.UtcNow;
        if (attempts.Count > 10000)
            attempts.Clear();

        while (true)
        {
            if (attempts.TryGetValue(senderId, out var lastAttempt))
            {
                if (now - lastAttempt < SenderEnrichCooldown)
                    return false;

                if (attempts.TryUpdate(senderId, now, lastAttempt))
                    return true;
            }
            else if (attempts.TryAdd(senderId, now))
            {
                return true;
            }
        }
    }

    private static string? GetPreferredUsername(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.MainUsername))
            return user.MainUsername;

        return user.ActiveUsernames?
            .FirstOrDefault(username => !string.IsNullOrWhiteSpace(username));
    }

    private static IReadOnlyCollection<string> GetSenderUsernames(
        UpdateManager? manager,
        long? senderId,
        string? mainUsername)
    {
        var usernames = new List<string>();
        if (!string.IsNullOrWhiteSpace(mainUsername))
            usernames.Add(mainUsername);

        if (manager != null && senderId.HasValue && manager.Users.TryGetValue(senderId.Value, out var sender))
        {
            usernames.AddRange(sender.ActiveUsernames ?? Array.Empty<string>());
        }

        return usernames
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task EnqueueNotificationsAsync(int accountId, TelegramMessageRecord record, List<KeywordConfig> matchedRules)
    {
        var targets = await _botNotifyTargetRepository.ListEnabledAsync();
        if (targets.Count == 0)
            return;

        var ruleNames = string.Join(", ", matchedRules
            .Select(r => string.IsNullOrWhiteSpace(r.RuleName) ? r.KeywordPattern : r.RuleName));

        var messageText = BotMessageFormatter.FormatNotifyMessage(accountId, record, matchedRules);
        var callbackActions = BotMessageFormatter.BuildCallbackActions(accountId, record);

        foreach (var target in targets)
        {
            _notifyChannel.Writer.TryWrite(new BotNotifyItem(
                target.ChatId,
                target.ChatTitle,
                messageText,
                callbackActions));
        }

        _logger.LogInformation(
            "账号 {AccountId} 消息 {MessageId} 命中关键词 [{Rules}]，已入队 {Count} 条通知",
            accountId, record.Id, ruleNames, targets.Count);
    }

    private static bool IsSameTelegramChat(long clientApiChatId, string? chatType, long botApiChatId)
    {
        if (clientApiChatId == botApiChatId)
            return true;

        return chatType?.Trim() switch
        {
            "Group" => botApiChatId == -1000000000000 - clientApiChatId,
            "Supergroup" => botApiChatId == -1000000000000 - clientApiChatId,
            "Channel" => botApiChatId == -1000000000000 - clientApiChatId,
            "Chat" => botApiChatId == -clientApiChatId,
            _ => false
        };
    }

    private void SaveStateQuietly(TelegramAccountRuntime runtime)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(runtime.Account.StatePath) && runtime.Manager != null)
                runtime.Manager.SaveState(runtime.Account.StatePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "持久化账号 {AccountId} 更新状态失败", runtime.Account.Id);
        }
    }

    private static void EnsureDirectories(TelegramAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.SessionPath))
        {
            var sessionDirectory = Path.GetDirectoryName(Path.GetFullPath(account.SessionPath));
            if (!string.IsNullOrWhiteSpace(sessionDirectory))
                Directory.CreateDirectory(sessionDirectory);
        }

        if (!string.IsNullOrWhiteSpace(account.StatePath))
        {
            var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(account.StatePath));
            if (!string.IsNullOrWhiteSpace(stateDirectory))
                Directory.CreateDirectory(stateDirectory);
        }
    }

    private sealed class TelegramAccountRuntime : IAsyncDisposable
    {
        public TelegramAccountRuntime(TelegramAccount account, Client client)
        {
            Account = account;
            Client = client;
        }

        public TelegramAccount Account { get; set; }
        public Client Client { get; }
        public UpdateManager? Manager { get; set; }
        public SemaphoreSlim SyncLock { get; } = new(1, 1);
        public bool IsAuthorized { get; set; }
        public bool IsMonitoring { get; set; }
        public DateTime? LastMessageAt { get; set; }

        // 用户名补齐的节流记录，按账号隔离，避免同一发送者被反复请求
        public ConcurrentDictionary<long, DateTime> SenderLookupAttempts { get; } = new();
        public ConcurrentDictionary<long, DateTime> SenderRefreshAttempts { get; } = new();

        public async ValueTask DisposeAsync()
        {
            SyncLock.Dispose();
            await Client.DisposeAsync();
        }
    }
}
