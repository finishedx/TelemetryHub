using Furion.ConfigurableOptions;

namespace TelegramMonitor;

[OptionsSettings("Telegram")]
public class TelegramOptions : IConfigurableOptions
{
    public int DefaultApiId { get; set; }
    public string DefaultApiHash { get; set; } = string.Empty;
    public string SessionsPath { get; set; } = "session";
    public TelegramProxyOptions Proxy { get; set; } = new();
}

public class TelegramProxyOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7897;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
