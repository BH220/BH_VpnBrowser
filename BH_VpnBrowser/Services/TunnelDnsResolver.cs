using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// VPN 이 내려준 DNS 서버로 직접 질의합니다.
    /// 시스템 리졸버(<see cref="Dns"/>)를 쓰면 로컬 DNS 로 질의가 새어 나가므로 사용하지 않습니다.
    /// </summary>
    public sealed class TunnelDnsResolver(VpnAdapter adapter)
    {
        private const int QueryTypeA = 1;
        private const int ClassInternet = 1;
        private const int MaxUdpResponse = 512;

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan UdpTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>VPN 이 DNS 를 내려주지 않아 시스템 리졸버로 넘어간 적이 있으면 true.</summary>
        public bool HasFallenBackToSystemDns { get; private set; }

        /// <summary>UDP 가 막혀 TCP 로 질의한 적이 있으면 true. 상태 표시에 씁니다.</summary>
        public bool UsesTcpDns { get; private set; }

        public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                return [literal];
            }

            if (_cache.TryGetValue(host, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Addresses;
            }

            var addresses = await QueryAllAsync(host, token);
            if (addresses.Length > 0)
            {
                _cache[host] = new CacheEntry(addresses, DateTime.UtcNow + CacheLifetime);
                return addresses;
            }

            token.ThrowIfCancellationRequested();

            if (adapter.DnsServers.Count > 0)
            {
                return [];
            }

            // VPN 이 DNS 를 내려주지 않은 구성. 이 경우에만 시스템 리졸버로 넘어갑니다.
            HasFallenBackToSystemDns = true;
            return await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, token);
        }

        /// <summary>
        /// DNS 서버가 여러 개면 동시에 물어보고 먼저 답한 쪽을 씁니다.
        /// 순서대로 물으면 앞 서버가 죽어 있을 때 그 타임아웃만큼 그대로 지연됩니다.
        /// </summary>
        private async Task<IPAddress[]> QueryAllAsync(string host, CancellationToken token)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);

            var pending = adapter.DnsServers
                .Select(server => QuerySafeAsync(server, host, attempt.Token))
                .ToList();

            try
            {
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending);
                    pending.Remove(finished);

                    var addresses = await finished;
                    if (addresses.Length > 0)
                    {
                        return addresses;
                    }
                }
            }
            finally
            {
                // 남은 질의를 정리하고, 그 작업들이 끝난 뒤에 CTS 를 폐기합니다.
                attempt.Cancel();
                await Task.WhenAll(pending).ConfigureAwait(false);
            }

            return [];
        }

        /// <summary>
        /// UDP 로 먼저 묻고, 답이 없으면 TCP 로 한 번 더 묻습니다.
        /// L2TP 터널 중에는 UDP 를 통과시키지 않는 구성이 있어서 TCP 경로가 반드시 필요합니다.
        /// </summary>
        private async Task<IPAddress[]> QuerySafeAsync(IPAddress server, string host, CancellationToken token)
        {
            var viaUdp = await AttemptAsync(() => QueryUdpAsync(server, host, token));
            if (viaUdp.Length > 0)
            {
                return viaUdp;
            }

            token.ThrowIfCancellationRequested();

            var viaTcp = await AttemptAsync(() => QueryTcpAsync(server, host, token));
            if (viaTcp.Length > 0)
            {
                UsesTcpDns = true;
            }

            return viaTcp;
        }

        /// <summary>응답하지 않는 서버는 실패가 아니라 "답이 없음"으로 다룹니다.</summary>
        private static async Task<IPAddress[]> AttemptAsync(Func<Task<IPAddress[]>> query)
        {
            try
            {
                return await query();
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
            {
                return [];
            }
        }

        private async Task<IPAddress[]> QueryUdpAsync(IPAddress server, string host, CancellationToken token)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            InterfaceBinder.BindTo(socket, adapter);

            var transactionId = NextTransactionId();
            var query = BuildQuery(transactionId, host);

            await socket.SendToAsync(query, new IPEndPoint(server, 53), token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(UdpTimeout);

            var buffer = new byte[MaxUdpResponse];
            var received = await socket.ReceiveFromAsync(
                buffer, new IPEndPoint(IPAddress.Any, 0), timeout.Token);

            return ParseAnswers(buffer.AsSpan(0, received.ReceivedBytes), transactionId);
        }

        /// <summary>RFC 1035 의 DNS over TCP. 메시지 앞에 2바이트 길이 프리픽스가 붙습니다.</summary>
        private async Task<IPAddress[]> QueryTcpAsync(IPAddress server, string host, CancellationToken token)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            InterfaceBinder.BindTo(socket, adapter);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TcpTimeout);

            await socket.ConnectAsync(server, 53, timeout.Token);

            var transactionId = NextTransactionId();
            var body = BuildQuery(transactionId, host);

            var framed = new byte[2 + body.Length];
            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)body.Length);
            body.CopyTo(framed, 2);
            await socket.SendAsync(framed, SocketFlags.None, timeout.Token);

            var lengthPrefix = new byte[2];
            if (!await ReceiveExactAsync(socket, lengthPrefix, timeout.Token))
            {
                return [];
            }

            var responseLength = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
            if (responseLength == 0)
            {
                return [];
            }

            var response = new byte[responseLength];
            return await ReceiveExactAsync(socket, response, timeout.Token)
                ? ParseAnswers(response, transactionId)
                : [];
        }

        private static async Task<bool> ReceiveExactAsync(Socket socket, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, token);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private static ushort NextTransactionId() => (ushort)Random.Shared.Next(1, ushort.MaxValue);

        private static byte[] BuildQuery(ushort transactionId, string host)
        {
            var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var size = 12 + labels.Sum(l => l.Length + 1) + 1 + 4;
            var packet = new byte[size];

            BinaryPrimitives.WriteUInt16BigEndian(packet, transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0100); // 표준 질의, 재귀 요청
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);      // 질문 1개

            var offset = 12;
            foreach (var label in labels)
            {
                packet[offset++] = (byte)label.Length;
                offset += Encoding.ASCII.GetBytes(label, packet.AsSpan(offset));
            }

            packet[offset++] = 0; // 루트 라벨
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), QueryTypeA);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), ClassInternet);
            return packet;
        }

        private static IPAddress[] ParseAnswers(ReadOnlySpan<byte> response, ushort expectedId)
        {
            if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId)
            {
                return [];
            }

            var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
            var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
            if (answerCount == 0)
            {
                return [];
            }

            var offset = 12;
            for (var i = 0; i < questionCount; i++)
            {
                if (!TrySkipName(response, ref offset) || offset + 4 > response.Length)
                {
                    return [];
                }

                offset += 4; // QTYPE + QCLASS
            }

            var results = new List<IPAddress>(answerCount);
            for (var i = 0; i < answerCount; i++)
            {
                if (!TrySkipName(response, ref offset) || offset + 10 > response.Length)
                {
                    break;
                }

                var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
                var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
                offset += 10;

                if (offset + dataLength > response.Length)
                {
                    break;
                }

                // CNAME 등은 건너뛰고 A 레코드만 모읍니다.
                if (type == QueryTypeA && dataLength == 4)
                {
                    results.Add(new IPAddress(response.Slice(offset, 4).ToArray()));
                }

                offset += dataLength;
            }

            return [.. results];
        }

        /// <summary>DNS 이름을 건너뜁니다. 0xC0 압축 포인터를 만나면 2바이트로 끝납니다.</summary>
        private static bool TrySkipName(ReadOnlySpan<byte> packet, ref int offset)
        {
            while (offset < packet.Length)
            {
                var length = packet[offset];

                if ((length & 0xC0) == 0xC0)
                {
                    offset += 2;
                    return offset <= packet.Length;
                }

                offset++;
                if (length == 0)
                {
                    return true;
                }

                offset += length;
            }

            return false;
        }

        private readonly record struct CacheEntry(IPAddress[] Addresses, DateTime ExpiresAt);
    }
}
