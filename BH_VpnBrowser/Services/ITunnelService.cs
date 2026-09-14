using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <param name="IsReady">로컬 SOCKS5 가 떠서 브라우저가 터널을 탈 수 있는지.</param>
    /// <param name="SettingsChanged">연결 과정에서 설정(PPP 조합)이 바뀌어 저장이 필요한지.</param>
    /// <param name="Status">배지·툴팁에 보여 줄 상태 한 줄.</param>
    public sealed record TunnelPrepareResult(bool IsReady, bool SettingsChanged, string Status);

    /// <summary>
    /// 앱 전용 터널: Windows VPN 을 확인·연결하고, 그 어댑터에 바인딩한 로컬 SOCKS5 를 띄웁니다.
    /// 브라우저는 이 SOCKS5 로만 나가므로 이 앱의 트래픽만 터널을 탑니다.
    /// </summary>
    public interface ITunnelService : IDisposable
    {
        /// <summary>로컬 SOCKS5 주소(127.0.0.1:포트). 준비되지 않았으면 null 이고 브라우저는 fail-closed 가 됩니다.</summary>
        string? Endpoint { get; }

        bool IsReady { get; }

        /// <summary>진행 상황은 <paramref name="progress"/> 로 알립니다(상태 표시줄용).</summary>
        Task<TunnelPrepareResult> PrepareAsync(VpnSettings settings, Action<string> progress);
    }
}
