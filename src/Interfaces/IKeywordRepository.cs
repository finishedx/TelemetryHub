namespace TelegramMonitor;

public interface IKeywordRepository
{
    Task<List<KeywordConfig>> ListAsync(int? accountId = null);
    Task<List<KeywordConfig>> ListBlacklistAsync(int? accountId = null, BlacklistEntryType? type = null);
    Task AddAsync(KeywordConfig keyword);
    Task BatchAddAsync(List<KeywordConfig> keywords);
    Task UpdateAsync(KeywordConfig keyword);
    Task DeleteAsync(int id);
    Task BatchDeleteAsync(IEnumerable<int> ids);
    Task SetBlacklistEnabledAsync(int id, bool enabled);
    Task DeleteBlacklistAsync(int id);
}
