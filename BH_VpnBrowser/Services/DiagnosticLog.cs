using System.Diagnostics;
using System.IO;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// 진단용 추적 로그. 평소에는 꺼져 있고, 환경 변수 <c>BH_VPNBROWSER_TRACE</c> 에 파일 경로를 주면
    /// 그 파일에 한 줄씩 덧붙입니다. 디버거가 붙어 있으면 출력 창에도 같은 내용이 나갑니다.
    /// 터널·SOCKS5·다운로드처럼 UI 에 드러나지 않는 실패 원인을 찍는 데 씁니다. 비밀값은 절대 기록하지 않습니다.
    /// </summary>
    public static class DiagnosticLog
    {
        private const string PathVariable = "BH_VPNBROWSER_TRACE";

        private static readonly string? FilePath = Environment.GetEnvironmentVariable(PathVariable);
        private static readonly object Gate = new();

        public static bool IsEnabled => FilePath is not null || Debugger.IsAttached;

        public static void Write(string category, string message)
        {
            if (!IsEnabled)
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
            Debug.WriteLine(line);

            if (FilePath is null)
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (IOException)
            {
                // 로그를 못 써도 앱 동작에는 영향을 주지 않습니다.
            }
        }
    }
}
