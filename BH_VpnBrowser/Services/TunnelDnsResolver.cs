using System.Buffers.Binary;
using System.Collections.Concurrent;
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
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>VPN 이 DNS 를 내려주지 않아 시스템 리졸버로 넘어간 적이 있으면 true.</summary>
        public bool HasFallenBackToSystemDns { get; private set; }

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
        /// <para>
        /// 순서대로 물으면 앞 서버가 죽어 있을 때 그 타임아웃만큼 그대로 지연되고,
        /// 다음 서버까지 한 번씩 실패하면 이름을 못 찾은 것으로 처리돼 브라우저에 DNS 오류가 뜹니다.
        /// VPN 이 내려주는 첫 번째 서버가 응답하지 않는 경우가 드물지 않아 동시 질의로 갑니다.
        /// </para>
        /// </summary>
        private async Task<IPAddress[]> QueryAllAsync(string host, CancellationToken token)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);

            var pending = adapter.DnsServers
                .Select(server => QuerySafeAsync(server, host, attempt.Token))
                .ToList();

            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);

                var addresses = await finished;
                if (addresses.Length > 0)
                {
                    // 남은 질의는 버립니다.
                    attempt.Cancel();
                    return addresses;
                }
            }

            return [];
        }

        /// <summary>응답하지 않는 서버는 실패가 아니라 "답이 없음"으로 다룹니다.</summary>
        private async Task<IPAddress[]> QuerySafeAsync(IPAddress server, string host, CancellationToken token)
        {
            try
            {
                return await QueryAsync(server, host, token);
            }
            catch (OperationCanceledException)
            {
                return [];
            }
            catch (SocketException)
            {
                return [];
            }
        }

        private async Task<IPAddress[]> QueryAsync(IPAddress server, string host, CancellationToken token)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            InterfaceBinder.BindTo(socket, adapter);

            var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = BuildQuery(transactionId, host);

            await socket.SendToAsync(query, new IPEndPoint(server, 53), token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(QueryTimeout);

            var buffer = new byte[512];
            var received = await socket.ReceiveFromAsync(
                buffer, new IPEndPoint(IPAddress.Any, 0), timeout.Token);

            return ParseAnswers(buffer.AsSpan(0, received.ReceivedBytes), transactionId);
        }

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
