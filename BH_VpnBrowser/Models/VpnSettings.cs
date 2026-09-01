using System.Text.Json.Serialization;

namespace BH_VpnBrowser.Models
{
    /// <summary>
    /// L2TP/IPsec 터널 설정. 이 앱의 브라우저 프로세스만 터널을 타게 하는 데 필요한 값들입니다.
    /// <para>
    /// 저장 위치는 %APPDATA%\BH_VpnBrowser\settings.json 이며 저장소(git)에는 들어가지 않습니다.
    /// 비밀번호와 사전 공유 키는 DPAPI(현재 사용자 키)로 암호화해 보관합니다.
    /// </para>
    /// </summary>
    public sealed class VpnSettings
    {
        /// <summary>앱이 관리하는 Windows VPN 연결 항목 이름.</summary>
        public const string ConnectionName = "BH VPN Browser";

        public string ServerAddress { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        /// <summary>DPAPI 로 보호된 비밀번호.</summary>
        public string ProtectedPassword { get; set; } = string.Empty;

        /// <summary>DPAPI 로 보호된 IPsec 사전 공유 키.</summary>
        public string ProtectedPreSharedKey { get; set; } = string.Empty;

        /// <summary>앱 시작 시 VPN 이 끊겨 있으면 자동으로 연결합니다.</summary>
        public bool AutoConnect { get; set; } = true;

        /// <summary>
        /// PPP 암호화 수준. Required 로 두면 MPPE 를 강제하는데,
        /// L2TP/IPsec 은 이미 IPsec 이 암호화하므로 MPPE 를 협상하지 않는 서버가 많습니다.
        /// 그런 서버는 연결을 그냥 끊어버립니다(오류 628).
        /// </summary>
        public string EncryptionLevel { get; set; } = "Optional";

        /// <summary>PPP 인증 방식. 서버가 받는 것에 맞춰 연결 테스트가 자동으로 정합니다.</summary>
        public string[] AuthMethods { get; set; } = ["MSChapv2"];

        /// <summary>프록시를 우회하는 UDP 경로(WebRTC)로 실제 IP 가 새는 것을 막습니다.</summary>
        public bool BlockWebRtcLeak { get; set; } = true;

        /// <summary>사설/로컬 주소는 터널을 거치지 않고 직결합니다.</summary>
        public bool BypassLoopback { get; set; } = true;

        public string HomePage { get; set; } = "https://www.google.com";

        /// <summary>런타임에만 존재하는 평문 값(파일에 그대로 저장되지 않음).</summary>
        [JsonIgnore]
        public string Password { get; set; } = string.Empty;

        [JsonIgnore]
        public string PreSharedKey { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ServerAddress) && !string.IsNullOrWhiteSpace(UserName);

        /// <summary>
        /// WebView2(Chromium) 브라우저 프로세스에만 적용되는 커맨드라인 인자.
        /// PC 전체 트래픽은 영향을 받지 않습니다.
        /// </summary>
        /// <param name="localTunnelEndpoint">
        /// 앱이 띄운 로컬 SOCKS5 의 <c>host:port</c>.
        /// 터널이 준비되지 않았으면 null 을 넘겨 fail-closed 로 만듭니다.
        /// </param>
        public string BuildBrowserArguments(string? localTunnelEndpoint)
        {
            // 터널이 없으면 닫힌 포트로 보내 통신 자체를 막습니다(실제 IP 노출 방지).
            var proxy = $"socks5://{localTunnelEndpoint ?? "127.0.0.1:1"}";

            var args = new List<string>
            {
                $"--proxy-server=\"{proxy}\"",

                // 지정하지 않으면 Chromium 이 사설/로컬 주소를 프록시 없이 직결로 보냅니다.
                BypassLoopback
                    ? "--proxy-bypass-list=\"<local>\""
                    : "--proxy-bypass-list=\"<-loopback>\"",
            };

            if (BlockWebRtcLeak)
            {
                // 프록시를 타지 않는 UDP(STUN/TURN) 후보를 막아 실제 공인 IP 노출을 차단합니다.
                args.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
            }

            return string.Join(' ', args);
        }

        /// <summary>
        /// 이 값이 바뀌면 WebView2 환경을 다시 만들어야 합니다.
        /// 로컬 SOCKS5 포트는 실행마다 달라지므로 비교에서 제외합니다.
        /// </summary>
        public string BuildEnvironmentSignature() =>
            string.Join('|', BypassLoopback, BlockWebRtcLeak);

        public VpnSettings Clone() => (VpnSettings)MemberwiseClone();

        public bool AffectsBrowserEnvironment(VpnSettings other) =>
            BuildEnvironmentSignature() != other.BuildEnvironmentSignature();
    }
}
