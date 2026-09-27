namespace TelegramMonitor;

public enum BlacklistEntryType
{
    User = 0,
    Chat = 1
}

public record BlacklistEntryDto(
    int Id,
    int? AccountId,
    BlacklistEntryType Type,
    string TargetValue,
    string DisplayName,
    bool IsEnabled,
    string? Remark,
    DateTime CreatedAt,
    DateTime UpdatedAt);
