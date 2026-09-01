using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// 127.0.0.1 에서만 받는 로컬 SOCKS5 서버.
    /// 들어온 연결을 VPN 어댑터에 바인딩한 소켓으로 중계하므로,
    /// 이 프록시를 쓰는 프로세스만 터널을 타고 PC 의 나머지 트래픽은 영향을 받지 않습니다.
    /// </summary>
    public sealed class Socks5ProxyServer : IDisposable
    {
        private const byte Version = 0x05;
        private const byte MethodNoAuth = 0x00;
        private const byte MethodNone = 0xFF;
        private const byte CommandConnect = 0x01;

        private const byte AddressIpv4 = 0x01;
        private const byte AddressDomain = 0x03;
        private const byte AddressIpv6 = 0x04;

        private const byte ReplySucceeded = 0x00;
        private const byte ReplyGeneralFailure = 0x01;
        private const byte ReplyHostUnreachable = 0x04;
        private const byte ReplyCommandNotSupported = 0x07;
        private const byte ReplyAddressNotSupported = 0x08;

        private readonly VpnAdapter _adapter;
        private readonly TunnelDnsResolver _resolver;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();

        public Socks5ProxyServer(VpnAdapter adapter)
        {
            _adapter = adapter;
            _resolver = new TunnelDnsResolver(adapter);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = AcceptLoopAsync(_shutdown.Token);
        }

        /// <summary>OS 가 할당한 로컬 포트. WebView2 의 --proxy-server 에 넣습니다.</summary>
        public int Port { get; }

        public string Endpoint => $"127.0.0.1:{Port}";

        /// <summary>VPN 이 DNS 를 주지 않아 시스템 리졸버로 넘어갔는지 여부.</summary>
        public bool HasDnsFallback => _resolver.HasFallenBackToSystemDns;

        /// <summary>마지막으로 중계에 실패한 이유. 상태 표시에 사용합니다.</summary>
        public string? LastError { get; private set; }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                _ = HandleClientAsync(client, token);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();

                    if (!await NegotiateAsync(stream, token))
                    {
                        return;
                    }

                    var request = await ReadRequestAsync(stream, token);
                    if (request is null)
                    {
                        return;
                    }

                    using var remote = await ConnectThroughTunnelAsync(stream, request.Value, token);
                    if (remote is null)
                    {
                        return;
                    }

                    using var remoteStream = new NetworkStream(remote, ownsSocket: false);
                    await RelayAsync(stream, remoteStream, token);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
                {
                    // 클라이언트나 원격이 먼저 끊은 정상적인 상황.
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
        }

        /// <summary>인증 협상. 루프백에서만 받으므로 no-auth 만 허용합니다.</summary>
        private static async Task<bool> NegotiateAsync(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[2];
            if (!await ReadExactAsync(stream, header, token) || header[0] != Version)
            {
                return false;
            }

            var methods = new byte[header[1]];
            if (!await ReadExactAsync(stream, methods, token))
            {
                return false;
            }

            var accepted = Array.IndexOf(methods, MethodNoAuth) >= 0;
            await stream.WriteAsync(new byte[] { Version, accepted ? MethodNoAuth : MethodNone }, token);
            return accepted;
        }

        private static async Task<Destination?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var header = new byte[4];
            if (!await ReadExactAsync(stream, header, token) || header[0] != Version)
            {
                return null;
            }

            if (header[1] != CommandConnect)
            {
                await SendReplyAsync(stream, ReplyCommandNotSupported, token);
                return null;
            }

            string host;
            switch (header[3])
            {
                case AddressIpv4:
                {
                    var raw = new byte[4];
                    if (!await ReadExactAsync(stream, raw, token))
                    {
                        return null;
                    }

                    host = new IPAddress(raw).ToString();
                    break;
                }

                case AddressDomain:
                {
                    var length = new byte[1];
                    if (!await ReadExactAsync(stream, length, token))
                    {
                        return null;
                    }

                    var raw = new byte[length[0]];
                    if (!await ReadExactAsync(stream, raw, token))
                    {
                        return null;
                    }

                    host = Encoding.ASCII.GetString(raw);
                    break;
                }

                case AddressIpv6:
                {
                    // 터널은 IPv4 로만 구성하므로 IPv6 목적지는 받지 않습니다.
                    var raw = new byte[16];
                    _ = await ReadExactAsync(stream, raw, token);
                    await SendReplyAsync(stream, ReplyAddressNotSupported, token);
                    return null;
                }

                default:
                    await SendReplyAsync(stream, ReplyAddressNotSupported, token);
                    return null;
            }

            var portBytes = new byte[2];
            if (!await ReadExactAsync(stream, portBytes, token))
            {
                return null;
            }

            return new Destination(host, BinaryPrimitives.ReadUInt16BigEndian(portBytes));
        }

        private async Task<Socket?> ConnectThroughTunnelAsync(
            NetworkStream stream, Destination destination, CancellationToken token)
        {
            IPAddress[] addresses;
            try
            {
                addresses = await _resolver.ResolveAsync(destination.Host, token);
            }
            catch (Exception ex)
            {
                LastError = $"DNS 실패 ({destination.Host}): {ex.Message}";
                await SendReplyAsync(stream, ReplyHostUnreachable, token);
                return null;
            }

            if (addresses.Length == 0)
            {
                await SendReplyAsync(stream, ReplyHostUnreachable, token);
                return null;
            }

            foreach (var address in addresses)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
                    InterfaceBinder.BindTo(socket, _adapter);
                    await socket.ConnectAsync(address, destination.Port, token);
                    await SendReplyAsync(stream, ReplySucceeded, token);
                    return socket;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    LastError = $"연결 실패 ({destination.Host}:{destination.Port})";
                    socket.Dispose();
                }
            }

            await SendReplyAsync(stream, ReplyGeneralFailure, token);
            return null;
        }

        private static Task SendReplyAsync(NetworkStream stream, byte code, CancellationToken token)
        {
            // BND.ADDR / BND.PORT 는 클라이언트가 쓰지 않으므로 0 으로 채웁니다.
            var reply = new byte[] { Version, code, 0x00, AddressIpv4, 0, 0, 0, 0, 0, 0 };
            return stream.WriteAsync(reply, token).AsTask();
        }

        private static async Task RelayAsync(NetworkStream client, NetworkStream remote, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);

            var upstream = PumpAsync(client, remote, linked);
            var downstream = PumpAsync(remote, client, linked);

            await Task.WhenAll(upstream, downstream);
        }

        /// <summary>한쪽이 끝나면 반대 방향도 함께 정리합니다.</summary>
        private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationTokenSource linked)
        {
            var buffer = new byte[32 * 1024];
            try
            {
                int read;
                while ((read = await from.ReadAsync(buffer, linked.Token)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException)
            {
                // 정상 종료 경로.
            }
            finally
            {
                await linked.CancelAsync();
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            _listener.Stop();
            _shutdown.Dispose();
        }

        private readonly record struct Destination(string Host, int Port);
    }
}
