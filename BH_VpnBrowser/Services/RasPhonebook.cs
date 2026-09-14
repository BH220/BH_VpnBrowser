using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// RAS 전화번호부(rasphone.pbk)의 항목 값을 직접 손봅니다.
    /// 사용자 범위 파일이라 관리자 권한은 필요 없습니다.
    /// </summary>
    public static class RasPhonebook
    {
        /// <summary>
        /// Windows 가 쓰는 형식은 BOM 없는 UTF-8(사실상 ASCII)입니다.
        /// UTF-16 으로 읽으면 전부 깨진 문자가 되어 항목을 찾지 못하고,
        /// UTF-16 으로 쓰면 Windows 가 항목을 인식하지 못합니다.
        /// </summary>
        private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>Routes 항목 하나의 크기. 남는 자리는 0 으로 채웁니다.</summary>
        private const int RouteBlobSize = 36;

        private const int AddressFamilyInternet = 2;

        public static string Path { get; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");

        /// <summary>
        /// 전화번호부의 <c>Routes</c> 값에 들어가는 정적 경로 한 건을 만듭니다.
        /// <para>
        /// Add-VpnConnectionRoute cmdlet 은 기본 경로(0.0.0.0/0)를 거부하므로
        /// 이 블롭을 직접 써 넣습니다. 형식은 cmdlet 이 만든 값에서 역산했습니다:
        /// 메트릭(4) + 주소 패밀리(4) + 프리픽스 길이(4) + 주소(4) + 패딩(20), 리틀엔디안.
        /// </para>
        /// </summary>
        public static string BuildRouteBlob(IPAddress destination, int prefixLength, int metric)
        {
            var blob = new byte[RouteBlobSize];

            BinaryPrimitives.WriteInt32LittleEndian(blob, metric);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), AddressFamilyInternet);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(8), prefixLength);
            destination.GetAddressBytes().CopyTo(blob, 12);

            return Convert.ToHexString(blob);
        }

        /// <summary>
        /// 항목을 통째로 지웁니다. 남는 항목이 없으면 파일까지 지웁니다(Windows 도 그렇게 합니다).
        /// </summary>
        /// <returns>지웠거나 애초에 없었으면 true.</returns>
        public static bool RemoveEntry(string entryName)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return true;
                }

                var kept = new List<string>();
                var inTarget = false;

                foreach (var line in File.ReadAllLines(Path, FileEncoding))
                {
                    if (line.TrimStart().StartsWith('['))
                    {
                        inTarget = line.Trim().Equals($"[{entryName}]", StringComparison.OrdinalIgnoreCase);
                    }

                    if (!inTarget)
                    {
                        kept.Add(line);
                    }
                }

                if (kept.Any(l => l.TrimStart().StartsWith('[')))
                {
                    File.WriteAllLines(Path, kept, FileEncoding);
                }
                else
                {
                    File.Delete(Path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 항목 안의 키들을 설정합니다. 이미 있으면 값을 바꾸고, 없으면 항목 <b>본문</b> 끝에 넣습니다.
        /// <para>
        /// 항목은 본문(key=value 나열) 뒤에 <c>NETCOMPONENTS=</c> / <c>MEDIA=</c> / <c>DEVICE=</c> 로
        /// 시작하는 하위 블록이 이어지는 구조입니다. RAS 는 본문 키를 본문 안에서만 찾기 때문에
        /// 하위 블록 뒤에 붙인 키는 오류도 없이 그냥 무시됩니다. <c>Routes</c> 가 그 자리에 들어가면
        /// 터널에 기본 경로가 안 잡혀 바인딩한 소켓이 전부 실패합니다(Add-VpnConnectionRoute 는
        /// <c>NumRoutes</c> 바로 뒤, 본문 안에 씁니다). 본문 첫 줄(<c>Encoding=</c>)도 밀리면 안 됩니다.
        /// </para>
        /// </summary>
        /// <returns>파일을 실제로 고쳤으면 true.</returns>
        public static bool SetEntryValues(string entryName, IReadOnlyDictionary<string, string> values)
        {
            try
            {
                if (!File.Exists(Path) || values.Count == 0)
                {
                    return false;
                }

                var lines = File.ReadAllLines(Path, FileEncoding).ToList();

                var start = lines.FindIndex(l =>
                    l.Trim().Equals($"[{entryName}]", StringComparison.OrdinalIgnoreCase));
                if (start < 0)
                {
                    return false;
                }

                var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
                if (end < 0)
                {
                    end = lines.Count;
                }

                var bodyEnd = FindBodyEnd(lines, start, end);

                foreach (var (key, value) in values)
                {
                    var prefix = key + "=";

                    // 하위 블록 뒤에 남아 있는 같은 키는 RAS 가 읽지 못하는 자리이므로 걷어냅니다.
                    for (var i = end - 1; i >= bodyEnd; i--)
                    {
                        if (lines[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            lines.RemoveAt(i);
                            end--;
                        }
                    }

                    var existing = lines.FindIndex(
                        start + 1,
                        bodyEnd - start - 1,
                        l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                    if (existing >= 0)
                    {
                        lines[existing] = prefix + value;
                    }
                    else
                    {
                        lines.Insert(bodyEnd, prefix + value);
                        bodyEnd++;
                        end++;
                    }
                }

                File.WriteAllLines(Path, lines, FileEncoding);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>하위 블록 머리로 쓰이는 대문자 키. 이 뒤로는 본문이 아닙니다.</summary>
        private static readonly string[] GroupHeaders = ["NETCOMPONENTS=", "MEDIA=", "DEVICE="];

        /// <summary>
        /// 본문이 끝나는 줄(첫 빈 줄 또는 첫 하위 블록 머리)의 인덱스를 돌려줍니다.
        /// 없으면 섹션 끝입니다.
        /// </summary>
        private static int FindBodyEnd(List<string> lines, int start, int end)
        {
            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || GroupHeaders.Any(h => line.StartsWith(h, StringComparison.Ordinal)))
                {
                    return i;
                }
            }

            return end;
        }
    }
}
