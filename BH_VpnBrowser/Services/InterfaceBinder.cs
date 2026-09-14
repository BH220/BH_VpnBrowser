using System.Net;
using System.Net.Sockets;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// 소켓을 특정 네트워크 인터페이스로 강제로 내보냅니다.
    /// <para>
    /// 시스템 라우팅 테이블을 전혀 건드리지 않고 <b>이 소켓만</b> VPN 어댑터를 타게 하는 것이
    /// 앱 단위 터널링의 핵심입니다. 관리자 권한이 필요 없습니다.
    /// </para>
    /// </summary>
    public static class InterfaceBinder
    {
        /// <summary>IP_UNICAST_IF (ws2ipdef.h)</summary>
        private const SocketOptionName IpUnicastIf = (SocketOptionName)31;

        public static void BindTo(Socket socket, VpnAdapter adapter)
        {
            // IPv4 의 IP_UNICAST_IF 는 인터페이스 인덱스를 네트워크 바이트 오더로 받습니다.
            // 호스트 바이트 오더로 넣으면 조용히 무시되고 기본 경로로 나가므로 반드시 변환해야 합니다.
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                IpUnicastIf,
                IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));

            // 출발지 주소까지 터널 주소로 고정합니다.
            socket.Bind(new IPEndPoint(adapter.LocalAddress, 0));
        }

        /// <summary>
        /// 이 어댑터로 인터넷에 나갈 경로가 있는지 확인합니다.
        /// <para>
        /// UDP 소켓의 connect 는 패킷을 보내지 않고 경로만 찾습니다. IP_UNICAST_IF 로 묶은 뒤
        /// 공인 주소에 connect 해 보면 터널에 기본 경로가 있는지 알 수 있고, 없으면 WSAENETUNREACH 가
        /// 납니다. 연결은 됐는데 경로가 안 얹힌 상태(그러면 모든 중계가 실패)를 걸러내는 데 씁니다.
        /// </para>
        /// </summary>
        public static bool HasDefaultRoute(VpnAdapter adapter)
        {
            // TEST-NET-1(RFC 5737). 어디로도 라우팅되지 않는 문서용 주소라 실제 트래픽은 없습니다.
            var probe = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9);

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                BindTo(socket, adapter);
                socket.Connect(probe);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
