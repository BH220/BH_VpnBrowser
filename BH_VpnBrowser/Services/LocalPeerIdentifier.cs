using System.Net;
using System.Runtime.InteropServices;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// 루프백 TCP 연결의 상대편이 어느 프로세스인지 찾고, 그 프로세스가 이 앱의 자손인지 판정합니다.
    /// <para>
    /// 로컬 SOCKS5 는 인증이 없으므로, 같은 PC 의 다른 프로그램이 포트만 알면 이 앱의 터널을 빌려 쓸 수 있습니다.
    /// 접속한 프로세스가 이 앱 자신이거나 그 자식(WebView2 프로세스들)일 때만 받아 줍니다.
    /// </para>
    /// </summary>
    public static class LocalPeerIdentifier
    {
        private const int AddressFamilyInternet = 2;
        private const int TcpTableOwnerPidAll = 5;
        private const int ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessBasicInformationClass = 0;

        /// <summary>부모를 따라 올라가는 최대 단계. WebView2 는 앱 → 브라우저 → 유틸리티(네트워크)로 두 단계입니다.</summary>
        private const int MaxAncestorDepth = 16;

        /// <summary>
        /// 우리 리스너(<paramref name="listenPort"/>)에 <paramref name="peer"/> 에서 접속한 소켓을 가진 프로세스 ID.
        /// 찾지 못하면 null.
        /// </summary>
        public static int? FindOwningProcess(IPEndPoint peer, int listenPort)
        {
            var size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AddressFamilyInternet, TcpTableOwnerPidAll, 0);

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AddressFamilyInternet, TcpTableOwnerPidAll, 0) != 0)
                {
                    return null;
                }

                var peerAddress = BitConverter.ToUInt32(peer.Address.GetAddressBytes(), 0);
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();

                // 테이블은 항목 수(4바이트) 뒤에 행이 이어집니다.
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + 4 + i * rowSize);

                    if (row.LocalAddress == peerAddress
                        && PortOf(row.LocalPort) == peer.Port
                        && PortOf(row.RemotePort) == listenPort)
                    {
                        return (int)row.OwningPid;
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>이 프로세스 자신이거나, 부모를 따라 올라가면 이 프로세스가 나오는지.</summary>
        public static bool IsCurrentProcessOrDescendant(int pid)
        {
            var current = Environment.ProcessId;
            if (pid == current)
            {
                return true;
            }

            for (var depth = 0; depth < MaxAncestorDepth && pid > 4; depth++)
            {
                var parent = GetParentProcessId(pid);
                if (parent is null)
                {
                    return false;
                }

                if (parent == current)
                {
                    return true;
                }

                pid = parent.Value;
            }

            return false;
        }

        private static int? GetParentProcessId(int pid)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var information = new ProcessBasicInformation();
                var status = NtQueryInformationProcess(
                    handle, ProcessBasicInformationClass, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _);

                return status == 0 ? (int)information.InheritedFromUniqueProcessId : null;
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }

        /// <summary>포트는 하위 16비트에 네트워크 바이트 오더로 들어 있습니다.</summary>
        private static int PortOf(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

        /// <summary>MIB_TCPROW_OWNER_PID</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddress;
            public uint LocalPort;
            public uint RemoteAddress;
            public uint RemotePort;
            public uint OwningPid;
        }

        /// <summary>PROCESS_BASIC_INFORMATION</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2A;
            public IntPtr Reserved2B;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
    }
}
