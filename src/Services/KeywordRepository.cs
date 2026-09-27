namespace TelegramMonitor;

public class KeywordRepository : IKeywordRepository
{
    private readonly ISqlSugarClient _db;

    public KeywordRepository(ISqlSugarClient db)
    {
        _db = db;
    }

    public Task<List<KeywordConfig>> ListAsync(int? accountId = null)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        var query = _db.Queryable<KeywordConfig>();

        query = normalizedAccountId.HasValue
            ? query.Where(x => x.AccountId == normalizedAccountId.Value)
            : query.Where(x => x.AccountId == null);

        return query
            .OrderBy(x => x.Priority)
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public Task<List<KeywordConfig>> ListBlacklistAsync(
        int? accountId = null,
        BlacklistEntryType? type = null)
    {
        var query = _db.Queryable<KeywordConfig>()
            .Where(x => x.KeywordAction == KeywordAction.Exclude)
            .Where(x =>
                x.ChatId != null ||
                (x.IsMatchUser && x.ChatId == null && x.ExactContent == null));

        if (accountId.HasValue)
        {
            query = accountId.Value > 0
                ? query.Where(x => x.AccountId == accountId.Value)
                : query.Where(x => x.AccountId == null);
        }

        if (type == BlacklistEntryType.User)
            query = query.Where(x =>
                x.IsMatchUser && x.ChatId == null && x.ExactContent == null);
        else if (type == BlacklistEntryType.Chat)
            query = query.Where(x => x.ChatId != null);

        return query
            .OrderByDescending(x => x.Id)
            .ToListAsync();
    }

    public async Task AddAsync(KeywordConfig keyword)
    {
        Validate(keyword);

        keyword.AccountId = NormalizeAccountId(keyword.AccountId);
        keyword.CreatedAt = ChinaTime.Now;
        keyword.UpdatedAt = keyword.CreatedAt;

        await EnsureUniqueAsync(keyword);
        await _db.Insertable(keyword).ExecuteCommandAsync();
    }

    public async Task BatchAddAsync(List<KeywordConfig> keywords)
    {
        if (keywords == null || keywords.Count == 0)
            throw Oops.Oh("关键词规则不能为空");

        try
        {
            await _db.Ado.BeginTranAsync();

            foreach (var keyword in keywords)
                await AddAsync(keyword);

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    public async Task UpdateAsync(KeywordConfig keyword)
    {
        if (keyword == null)
            throw Oops.Oh("关键词规则不能为空");
        if (keyword.Id <= 0)
            throw Oops.Oh("关键词规则 ID 无效");

        var existing = await _db.Queryable<KeywordConfig>().FirstAsync(x => x.Id == keyword.Id);
        if (existing == null)
            throw Oops.Oh($"关键词规则不存在: {keyword.Id}");

        Validate(keyword);

        existing.AccountId = NormalizeAccountId(keyword.AccountId);
        existing.RuleName = keyword.RuleName?.Trim();
        existing.KeywordPattern = keyword.KeywordPattern.Trim();
        existing.MatchMode = keyword.MatchMode;
        existing.IsMatchUser = keyword.IsMatchUser;
        existing.UserPattern = keyword.IsMatchUser ? keyword.UserPattern?.Trim() : null;
        // ChatId 和 ExactContent 是由 Bot 快捷屏蔽按钮维护的内部限定条件。
        // 常规关键词编辑接口不暴露这两个字段，更新时必须保留原值。
        existing.KeywordAction = keyword.KeywordAction;
        existing.IsCaseSensitive = keyword.IsCaseSensitive;
        existing.IsEnabled = keyword.IsEnabled;
        existing.Priority = keyword.Priority;
        existing.Remark = keyword.Remark?.Trim();
        existing.UpdatedAt = ChinaTime.Now;

        await EnsureUniqueAsync(existing, existing.Id);
        await _db.Updateable(existing).ExecuteCommandAsync();
    }

    public async Task DeleteAsync(int id)
    {
        if (id <= 0)
            return;

        await _db.Deleteable<KeywordConfig>().In(id).ExecuteCommandAsync();
    }

    public async Task BatchDeleteAsync(IEnumerable<int> ids)
    {
        var deleteIds = ids?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<int>();

        if (deleteIds.Count == 0)
            return;

        await _db.Deleteable<KeywordConfig>().In(deleteIds).ExecuteCommandAsync();
    }

    public async Task SetBlacklistEnabledAsync(int id, bool enabled)
    {
        var existing = await RequireBlacklistAsync(id);
        existing.IsEnabled = enabled;
        existing.UpdatedAt = ChinaTime.Now;
        await _db.Updateable(existing).UpdateColumns(x => new { x.IsEnabled, x.UpdatedAt }).ExecuteCommandAsync();
    }

    public async Task DeleteBlacklistAsync(int id)
    {
        await RequireBlacklistAsync(id);
        await _db.Deleteable<KeywordConfig>().In(id).ExecuteCommandAsync();
    }

    private async Task<KeywordConfig> RequireBlacklistAsync(int id)
    {
        var existing = id > 0
            ? await _db.Queryable<KeywordConfig>().FirstAsync(x => x.Id == id)
            : null;

        if (existing == null || !IsUserOrChatBlacklist(existing))
            throw Oops.Oh($"黑名单记录不存在: {id}");

        return existing;
    }

    private static bool IsUserOrChatBlacklist(KeywordConfig keyword) =>
        keyword.KeywordAction == KeywordAction.Exclude &&
        (keyword.ChatId.HasValue ||
         (keyword.IsMatchUser &&
          keyword.ChatId == null && keyword.ExactContent == null));

    private async Task EnsureUniqueAsync(KeywordConfig keyword, int? ignoreId = null)
    {
        var query = _db.Queryable<KeywordConfig>()
            .Where(x =>
                x.AccountId == keyword.AccountId &&
                x.KeywordPattern == keyword.KeywordPattern &&
                x.IsCaseSensitive == keyword.IsCaseSensitive &&
                x.IsMatchUser == keyword.IsMatchUser &&
                SqlFunc.IsNull(x.UserPattern, string.Empty) == (keyword.UserPattern ?? string.Empty) &&
                x.ChatId == keyword.ChatId &&
                SqlFunc.IsNull(x.ExactContent, string.Empty) == (keyword.ExactContent ?? string.Empty));

        if (ignoreId.HasValue)
            query = query.Where(x => x.Id != ignoreId.Value);

        if (await query.AnyAsync())
            throw Oops.Oh("该账号下已存在相同关键词规则");
    }

    private static void Validate(KeywordConfig keyword)
    {
        if (keyword == null)
            throw Oops.Oh("关键词规则不能为空");
        if (string.IsNullOrWhiteSpace(keyword.KeywordPattern))
            throw Oops.Oh("关键词正则不能为空");

        keyword.KeywordPattern = keyword.KeywordPattern.Trim();
        KeywordPatternBuilder.EnsureValidRegex(keyword.KeywordPattern, "关键词正则");

        if (keyword.IsMatchUser)
        {
            if (string.IsNullOrWhiteSpace(keyword.UserPattern))
                throw Oops.Oh("启用匹配用户时，用户匹配规则不能为空");

            keyword.UserPattern = keyword.UserPattern.Trim();
            KeywordPatternBuilder.EnsureValidRegex(keyword.UserPattern, "用户匹配正则");
        }

        if (!keyword.IsMatchUser)
            keyword.UserPattern = null;
    }

    private static int? NormalizeAccountId(int? accountId) =>
        accountId.HasValue && accountId.Value > 0 ? accountId.Value : null;
}
