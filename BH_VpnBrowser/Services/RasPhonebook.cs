using System.IO;
using System.Net;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// RAS 전화번호부(rasphone.pbk)의 항목 설정을 직접 손봅니다.
    /// <para>
    /// 이 앱은 터널에 기본 경로(0.0.0.0/0)가 있어야 합니다. 소켓을 VPN 인터페이스에
    /// 바인딩해도(IP_UNICAST_IF) 그 인터페이스에 넥스트홉이 없으면 나갈 수 없기 때문입니다.
    /// 그렇다고 PC 전체 트래픽까지 터널로 보내면 안 되므로, 메트릭이 아주 큰 기본 경로를
    /// 터널에만 얹어 시스템 기본 경로는 물리 NIC 가 계속 이기게 만듭니다.
    /// </para>
    /// <para>
    /// <c>Add-VpnConnectionRoute</c> 는 0.0.0.0/0 을 거부하고, 라우팅 테이블을 직접 고치려면
    /// 관리자 권한이 필요합니다. 그래서 cmdlet 이 쓰는 저장 위치(pbk 의 NumRoutes/Routes)에
    /// 같은 형식으로 직접 써 넣습니다. RAS 가 연결을 올릴 때 스스로 적용하므로 권한 상승이 없습니다.
    /// </para>
    /// <para>
    /// 참고: <c>IpPrioritizeRemote=1</c>(원격 게이트웨이 사용)은 메트릭으로 뒤집을 수 없습니다.
    /// 인터페이스 메트릭 9000, 라우트 메트릭 9000 을 줘도 Windows 는 계속 터널을 고릅니다.
    /// 즉 그 옵션은 "PC 전체 터널링"이라 이 앱에는 쓸 수 없습니다.
    /// </para>
    /// </summary>
    public static class RasPhonebook
    {
        /// <summary>
        /// pbk 는 UTF-8(BOM 없음) + CRLF 입니다. 다른 인코딩으로 저장하면 RAS 가
        /// 전화번호부 전체를 못 읽어 <b>항목이 하나도 없는 것처럼</b> 동작합니다.
        /// </summary>
        private static readonly UTF8Encoding PhonebookEncoding = new(encoderShouldEmitUTF8Identifier: false);

        private const string LineSeparator = "\r\n";

        public static string DefaultPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");

        /// <summary>
        /// 항목의 키 값을 바꿉니다. 키가 없으면 섹션 머리 바로 뒤에 넣습니다.
        /// 항목이나 파일이 없으면 아무것도 하지 않고 false 를 돌려줍니다.
        /// </summary>
        public static bool SetEntryValues(
            string entryName, IReadOnlyDictionary<string, string> values, string? phonebookPath = null)
        {
            var path = phonebookPath ?? DefaultPath;

            if (!File.Exists(path) || values.Count == 0)
            {
                return false;
            }

            try
            {
                var lines = File.ReadAllText(path, PhonebookEncoding).Split(LineSeparator).ToList();

                var start = lines.FindIndex(line =>
                    line.Trim().Equals($"[{entryName}]", StringComparison.OrdinalIgnoreCase));

                if (start < 0)
                {
                    return false;
                }

                // 섹션은 다음 머리([...]) 직전까지입니다.
                var end = lines.FindIndex(start + 1, line => line.StartsWith('['));
                if (end < 0)
                {
                    end = lines.Count;
                }

                var changed = false;

                foreach (var (key, value) in values)
                {
                    var prefix = key + "=";
                    var at = lines.FindIndex(
                        start + 1, end - start - 1,
                        line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                    var entry = prefix + value;

                    if (at >= 0)
                    {
                        if (lines[at] == entry)
                        {
                            continue;
                        }

                        lines[at] = entry;
                    }
                    else
                    {
                        // 섹션 끝에 붙입니다. 머리 바로 뒤에 끼워 넣으면 Encoding= 이 첫 줄에서
                        // 밀려나는데, 그러면 RAS 가 항목 자체를 못 읽습니다.
                        var insertAt = end;
                        while (insertAt > start + 1 && lines[insertAt - 1].Length == 0)
                        {
                            insertAt--;
                        }

                        lines.Insert(insertAt, entry);
                        end++;
                    }

                    changed = true;
                }

                if (changed)
                {
                    File.WriteAllText(path, string.Join(LineSeparator, lines), PhonebookEncoding);
                }

                return true;
            }
            catch (Exception)
            {
                // 전화번호부를 못 고쳐도 연결 자체는 시도해 봅니다.
                return false;
            }
        }

        /// <summary>지정한 키들의 현재 값을 읽습니다(진단용).</summary>
        public static IReadOnlyDictionary<string, string> ReadEntryValues(
            string entryName, IReadOnlyCollection<string> keys, string? phonebookPath = null)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = phonebookPath ?? DefaultPath;

            if (!File.Exists(path))
            {
                return found;
            }

            var inEntry = false;

            foreach (var line in File.ReadLines(path, PhonebookEncoding))
            {
                if (line.StartsWith('['))
                {
                    if (inEntry)
                    {
                        break;
                    }

                    inEntry = line.Trim().Equals($"[{entryName}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inEntry)
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line[..separator];
                if (keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    found[key] = line[(separator + 1)..];
                }
            }

            return found;
        }

        /// <summary>
        /// 프로필 라우트 한 개를 pbk 가 쓰는 이진 형식(hex)으로 만듭니다.
        /// 구조는 리틀엔디언으로 [메트릭 4바이트][주소 체계 4바이트][프리픽스 길이 4바이트][주소 24바이트]입니다.
        /// (Add-VpnConnectionRoute 가 실제로 쓰는 바이트를 그대로 흉내 낸 것입니다.)
        /// </summary>
        public static string BuildRouteBlob(IPAddress destination, int prefixLength, int metric)
        {
            const int AddressFieldLength = 24;
            const int AfInet = 2;
            const int AfInet6 = 23;

            var buffer = new List<byte>(36);
            buffer.AddRange(BitConverter.GetBytes(metric));
            buffer.AddRange(BitConverter.GetBytes(
                destination.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? AfInet6 : AfInet));
            buffer.AddRange(BitConverter.GetBytes(prefixLength));

            var address = destination.GetAddressBytes();
            buffer.AddRange(address);
            buffer.AddRange(new byte[AddressFieldLength - address.Length]);

            return Convert.ToHexString(buffer.ToArray());
        }
    }
}
