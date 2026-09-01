using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BH_VpnBrowser.Services
{
    /// <summary>연결된 VPN 어댑터의 인터페이스 인덱스 / 로컬 IP / DNS 서버.</summary>
    public sealed record VpnAdapter(
        string Name,
        int InterfaceIndex,
        IPAddress LocalAddress,
        IReadOnlyList<IPAddress> DnsServers);

    /// <summary>
    /// Windows 가 L2TP 연결로 만든 RAS 어댑터를 찾습니다.
    /// 이 어댑터의 인덱스를 소켓에 지정하면 라우팅 테이블과 무관하게 해당 소켓만 터널로 나갑니다.
    /// </summary>
    public static class VpnAdapterLocator
    {
        public static VpnAdapter? Find(string connectionName)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                // L2TP/PPTP RAS 연결은 PPP, 일부 구성은 Tunnel 로 잡힙니다.
                if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel))
                {
                    continue;
                }

                if (!Matches(nic, connectionName))
                {
                    continue;
                }

                var properties = nic.GetIPProperties();

                var local = properties.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (local is null)
                {
                    continue;
                }

                int index;
                try
                {
                    index = properties.GetIPv4Properties().Index;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                var dns = properties.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();

                return new VpnAdapter(nic.Name, index, local.Address, dns);
            }

            return null;
        }

        /// <summary>연결 이름을 지정하지 않으면 아무 RAS 어댑터나 사용합니다.</summary>
        private static bool Matches(NetworkInterface nic, string connectionName) =>
            string.IsNullOrWhiteSpace(connectionName)
            || nic.Name.Contains(connectionName, StringComparison.OrdinalIgnoreCase)
            || nic.Description.Contains(connectionName, StringComparison.OrdinalIgnoreCase);
    }
}
