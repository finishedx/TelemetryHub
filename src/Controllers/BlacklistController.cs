namespace TelegramMonitor;

[ApiController]
[Route("api/blacklist")]
[ApiDescriptionSettings(Tag = "blacklist", Description = "用户与群组黑名单接口")]
[Authorize]
public class BlacklistController : ControllerBase
{
    private readonly IKeywordRepository _keywordRepository;

    public BlacklistController(IKeywordRepository keywordRepository)
    {
        _keywordRepository = keywordRepository;
    }

    [HttpGet]
    public async Task<IReadOnlyList<BlacklistEntryDto>> ListAsync(
        [FromQuery] int? accountId = null,
        [FromQuery] BlacklistEntryType? type = null)
    {
        var entries = await _keywordRepository.ListBlacklistAsync(accountId, type);
        return entries.Select(ToDto).ToList();
    }

    [HttpPut("{id}/toggle")]
    public Task ToggleAsync(int id, [FromQuery] bool enabled) =>
        _keywordRepository.SetBlacklistEnabledAsync(id, enabled);

    [HttpDelete("{id}")]
    public Task DeleteAsync(int id) =>
        _keywordRepository.DeleteBlacklistAsync(id);

    private static BlacklistEntryDto ToDto(KeywordConfig entry)
    {
        var type = entry.ChatId.HasValue ? BlacklistEntryType.Chat : BlacklistEntryType.User;
        var targetValue = type == BlacklistEntryType.Chat
            ? entry.ChatId!.Value.ToString()
            : FormatUserTarget(entry);

        return new BlacklistEntryDto(
            entry.Id,
            entry.AccountId,
            type,
            targetValue,
            string.IsNullOrWhiteSpace(entry.RuleName) ? targetValue : entry.RuleName,
            entry.IsEnabled,
            entry.Remark,
            entry.CreatedAt,
            entry.UpdatedAt);
    }

    private static string FormatUserPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "-";

        if (pattern.Length >= 2 && pattern[0] == '^' && pattern[^1] == '$')
        {
            try
            {
                return Regex.Unescape(pattern[1..^1]);
            }
            catch (ArgumentException)
            {
            }
        }

        return pattern;
    }

    private static string FormatUserTarget(KeywordConfig entry) =>
        entry.IsMatchUser
            ? $"用户: {FormatUserPattern(entry.UserPattern)}"
            : "-";
}
