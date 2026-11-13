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
        /// <summary>
        /// 로컬 SOCKS5 주소(127.0.0.1:포트). 한 번도 준비된 적이 없으면 null 이고 브라우저는 닫힌 포트를 보게 됩니다.
        /// 한 번 정해진 주소는 터널이 끊겼다 다시 붙어도 바뀌지 않습니다.
        /// </summary>
        string? Endpoint { get; }

        /// <summary>지금 터널로 중계할 수 있는지. 실행 중에 VPN 이 끊기면 false 로 바뀌고 그동안 통신은 차단됩니다.</summary>
        bool IsReady { get; }

        /// <summary>배지·툴팁에 보여 줄 현재 상태 한 줄.</summary>
        string Status { get; }

        /// <summary>
        /// 실행 중에 터널 상태가 바뀌었을 때(끊김, 다시 붙음). <see cref="PrepareAsync"/> 를 처음 부른 스레드(UI)에서 알립니다.
        /// </summary>
        event EventHandler? StateChanged;

        /// <summary>
        /// 터널을 준비합니다. 이미 떠 있는 로컬 SOCKS5 가 있으면 포트는 그대로 두고 어댑터만 다시 맞춥니다.
        /// 진행 상황은 <paramref name="progress"/> 로 알립니다(상태 표시줄용).
        /// </summary>
        Task<TunnelPrepareResult> PrepareAsync(VpnSettings settings, Action<string> progress);

        /// <summary>
        /// 돌려준 객체를 폐기할 때까지 실행 중 감시(자동 재연결 포함)를 멈춥니다.
        /// 설정 창의 연결 테스트처럼 다른 곳에서 VPN 을 끊고 다시 거는 동안 씁니다.
        /// </summary>
        IDisposable PauseMonitoring();
    }
}
