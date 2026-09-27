using System.Net.Sockets;

namespace TelegramMonitor;

/// <summary>
/// 给 WTelegramClient 接入 SOCKS5 代理。
/// WTelegramClient 不识别 proxy_server 这类配置键，它把"如何建立到 Telegram 的 TCP 连接"
/// 开放为 TcpHandler 委托；官方文档（EXAMPLES「Use a proxy or MTProxy to connect to Telegram」）
/// 推荐的做法就是挂 TcpHandler + 代理库，这里用项目已依赖的 xNetStandard。
/// </summary>
public static class TelegramProxyFactory
{
    public static void Apply(Client client, TelegramProxyOptions? options)
    {
        if (client == null || options == null || !options.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0)
            return;

        var proxyClient = string.IsNullOrWhiteSpace(options.Username)
            ? new Socks5ProxyClient(options.Host, options.Port)
            : new Socks5ProxyClient(options.Host, options.Port, options.Username, options.Password);

        client.TcpHandler = (host, port) =>
        {
            try
            {
                return Task.FromResult(proxyClient.CreateConnection(host, port, null));
            }
            catch (Exception ex)
            {
                return Task.FromException<TcpClient>(ex);
            }
        };
    }
}
