using System.Diagnostics;

namespace TelegramMonitor;

internal static class RealtimeMessagePolicy
{
    public static DateTime StartedAtUtc { get; } = GetProcessStartTimeUtc();

    public static bool ShouldProcess(DateTime messageDate, DateTime startedAtUtc)
    {
        if (messageDate == default)
            return false;

        var messageDateUtc = messageDate.Kind == DateTimeKind.Local
            ? messageDate.ToUniversalTime()
            : DateTime.SpecifyKind(messageDate, DateTimeKind.Utc);

        return messageDateUtc >= startedAtUtc;
    }

    private static DateTime GetProcessStartTimeUtc()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
