using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// L2TP 연결을 확인·복구·연결한 뒤 VPN 어댑터에 바인딩한 로컬 SOCKS5 를 띄웁니다.
    /// 실패하면 <see cref="Endpoint"/> 를 null 로 두어 브라우저가 닫힌 포트를 보게(fail-closed) 합니다.
    /// </summary>
    public sealed class TunnelService(IVpnConnectionManager connections) : ITunnelService
    {
        /// <summary>RAS 어댑터가 오르내리거나 경로가 얹히기까지 기다리는 횟수와 간격(최대 3초).</summary>
        private const int WaitAttempts = 10;

        private static readonly TimeSpan WaitInterval = TimeSpan.FromMilliseconds(300);

        private Socks5ProxyServer? _proxy;

        public string? Endpoint => _proxy?.Endpoint;

        public bool IsReady => _proxy is not null;

        public async Task<TunnelPrepareResult> PrepareAsync(VpnSettings settings, Action<string> progress)
        {
            if (!settings.IsConfigured)
            {
                progress("VPN 설정이 필요합니다. 메뉴 > VPN 설정에서 서버 주소와 계정을 입력하세요.");
                return new TunnelPrepareResult(false, false, "VPN 설정이 비어 있습니다.");
            }

            var name = VpnSettings.ConnectionName;
            var settingsChanged = false;
            var adapter = VpnAdapterLocator.Find(name);

            // 연결은 살아 있는데 터널에 기본 경로가 없으면 바인딩한 소켓이 전부 실패합니다
            // (다이얼 도중 앱이 종료돼 경로를 못 얹은 연결이 남은 경우). 끊고 다시 걸어 경로를 다시 얹습니다.
            if (adapter is not null && settings.AutoConnect && !InterfaceBinder.HasDefaultRoute(adapter))
            {
                progress("터널에 경로가 없어 VPN 을 다시 연결합니다...");
                await connections.DisconnectAsync(name);
                adapter = await WaitForAdapterAsync(name, present: false);
            }

            if (adapter is null && settings.AutoConnect)
            {
                progress($"VPN '{name}' 연결 중...");

                var entries = await connections.ListAsync();
                var entry = entries.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

                // 항목이 설정과 같은 조합으로 만들어져 있을 때만 바로 붙여 봅니다.
                // 예전 빌드가 남긴 항목(예: 암호화 없음)은 실패가 정해진 다이얼에 1분을 쓰므로 바로 다시 만듭니다.
                var connect = entry is not null && entry.Matches(settings)
                    ? await connections.ConnectAsync(settings)
                    : new CommandResult(false, entry is null ? "연결 항목이 없습니다." : "연결 항목이 설정과 다릅니다.");

                // 실패하면 서버가 받아주는 PPP 조합을 찾을 때까지 훑습니다. 찾으면 설정이 바뀝니다.
                if (!connect.Succeeded)
                {
                    connect = await connections.ApplyAndConnectAsync(settings, progress);
                    settingsChanged = connect.Succeeded;
                }

                if (!connect.Succeeded)
                {
                    progress("VPN 연결 실패: " + connect.Message.Split('\n')[0]);
                    return new TunnelPrepareResult(false, settingsChanged, connect.Message);
                }

                adapter = await WaitForAdapterAsync(name, present: true);
            }

            if (adapter is null)
            {
                var status = $"'{name}' VPN 어댑터를 찾지 못했습니다.";
                progress(status);
                return new TunnelPrepareResult(false, settingsChanged, status);
            }

            // 경로가 없으면 프록시를 띄워도 모든 중계가 실패하므로 준비된 것으로 보지 않습니다(fail-closed).
            if (!await WaitForDefaultRouteAsync(adapter))
            {
                var status = "터널에 기본 경로가 없습니다. VPN 을 끊고 다시 연결하세요.";
                progress(status);
                return new TunnelPrepareResult(false, settingsChanged, status);
            }

            _proxy?.Dispose();
            _proxy = new Socks5ProxyServer(adapter);

            progress($"터널 준비 완료 - {adapter.Name} ({adapter.LocalAddress})");
            return new TunnelPrepareResult(true, settingsChanged, $"{adapter.Name} · {adapter.LocalAddress}");
        }

        /// <summary>RAS 어댑터가 올라오거나(present) 내려가기까지 잠깐 걸립니다.</summary>
        private static async Task<VpnAdapter?> WaitForAdapterAsync(string name, bool present)
        {
            var adapter = VpnAdapterLocator.Find(name);
            for (var attempt = 0; attempt < WaitAttempts && (adapter is not null) != present; attempt++)
            {
                await Task.Delay(WaitInterval);
                adapter = VpnAdapterLocator.Find(name);
            }

            return adapter;
        }

        /// <summary>RAS 가 전화번호부의 경로를 얹는 데도 잠깐 걸릴 수 있습니다.</summary>
        private static async Task<bool> WaitForDefaultRouteAsync(VpnAdapter adapter)
        {
            for (var attempt = 0; attempt < WaitAttempts; attempt++)
            {
                if (InterfaceBinder.HasDefaultRoute(adapter))
                {
                    return true;
                }

                await Task.Delay(WaitInterval);
            }

            return false;
        }

        public void Dispose()
        {
            _proxy?.Dispose();
            _proxy = null;
        }
    }
}
