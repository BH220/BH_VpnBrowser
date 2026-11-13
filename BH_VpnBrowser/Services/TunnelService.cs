using System.Net.NetworkInformation;
using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// L2TP 연결을 확인·복구·연결한 뒤 VPN 어댑터에 바인딩한 로컬 SOCKS5 를 띄웁니다.
    /// 실패하면 <see cref="Endpoint"/> 를 null 로 두어 브라우저가 닫힌 포트를 보게(fail-closed) 합니다.
    /// <para>
    /// 한 번 띄운 뒤에는 네트워크 변화를 지켜봅니다. VPN 이 끊기면 중계를 막고 상태를 알리며,
    /// 다시 붙으면(자동 재연결 또는 사용자가 Windows 에서 연결) 같은 포트에 새 어댑터를 묶습니다.
    /// 브라우저는 포트만 알고 있으므로 다시 시작할 필요가 없습니다.
    /// </para>
    /// </summary>
    public sealed class TunnelService(IVpnConnectionManager connections) : ITunnelService
    {
        /// <summary>RAS 어댑터가 오르내리거나 경로가 얹히기까지 기다리는 횟수와 간격(최대 3초).</summary>
        private const int WaitAttempts = 10;

        private static readonly TimeSpan WaitInterval = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// 실행 중 자동 재연결 사이의 최소 간격. 서버가 죽었거나 계정이 막힌 상태에서
        /// 네트워크 알림이 올 때마다 다이얼을 반복하지 않게 합니다.
        /// </summary>
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(30);

        /// <summary>준비·감시가 같은 연결 항목을 동시에 다루지 않게 합니다.</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Socks5ProxyServer? _proxy;
        private VpnSettings? _settings;
        private SynchronizationContext? _context;
        private DateTime _lastReconnect = DateTime.MinValue;
        private int _pauses;
        private int _checkPending;
        private bool _watching;
        private volatile bool _disposed;

        public string? Endpoint => _proxy?.Endpoint;

        public bool IsReady => _proxy?.Adapter is not null;

        public string Status { get; private set; } = string.Empty;

        public event EventHandler? StateChanged;

        public async Task<TunnelPrepareResult> PrepareAsync(VpnSettings settings, Action<string> progress)
        {
            _context ??= SynchronizationContext.Current;

            await _gate.WaitAsync();
            try
            {
                _settings = settings;

                var result = await PrepareCoreAsync(settings, progress);
                Status = result.Status;
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<TunnelPrepareResult> PrepareCoreAsync(VpnSettings settings, Action<string> progress)
        {
            if (!settings.IsConfigured)
            {
                progress("VPN 설정이 필요합니다. 메뉴 > VPN 설정에서 서버 주소와 계정을 입력하세요.");
                return Fail("VPN 설정이 비어 있습니다.", settingsChanged: false);
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
                    return Fail(connect.Message, settingsChanged);
                }

                adapter = await WaitForAdapterAsync(name, present: true);
            }

            if (adapter is null)
            {
                var status = $"'{name}' VPN 어댑터를 찾지 못했습니다.";
                progress(status);
                return Fail(status, settingsChanged);
            }

            // 경로가 없으면 프록시를 띄워도 모든 중계가 실패하므로 준비된 것으로 보지 않습니다(fail-closed).
            if (!await WaitForDefaultRouteAsync(adapter))
            {
                var status = "터널에 기본 경로가 없습니다. VPN 을 끊고 다시 연결하세요.";
                progress(status);
                return Fail(status, settingsChanged);
            }

            Bind(adapter);

            progress($"터널 준비 완료 - {adapter.Name} ({adapter.LocalAddress})");
            return new TunnelPrepareResult(true, settingsChanged, Describe(adapter));
        }

        /// <summary>준비에 실패했습니다. 이미 떠 있는 프록시가 있으면 예전 어댑터로 계속 내보내지 않게 막습니다.</summary>
        private TunnelPrepareResult Fail(string status, bool settingsChanged)
        {
            _proxy?.UseAdapter(null);
            return new TunnelPrepareResult(false, settingsChanged, status);
        }

        /// <summary>
        /// 어댑터를 로컬 SOCKS5 에 묶습니다. 프록시가 이미 있으면 포트를 유지한 채 어댑터만 바꿉니다
        /// (브라우저의 --proxy-server 는 시작할 때 고정되므로 포트가 바뀌면 안 됩니다).
        /// </summary>
        private void Bind(VpnAdapter adapter)
        {
            if (_proxy is null)
            {
                _proxy = new Socks5ProxyServer(adapter);
                StartWatching();
            }
            else
            {
                _proxy.UseAdapter(adapter);
            }
        }

        private static string Describe(VpnAdapter adapter) =>
            adapter.DnsServers.Count > 0
                ? $"{adapter.Name} · {adapter.LocalAddress}"
                : $"{adapter.Name} · {adapter.LocalAddress} · 공용 DNS(터널 경유)";

        // ================= 실행 중 감시 =================

        public IDisposable PauseMonitoring()
        {
            Interlocked.Increment(ref _pauses);
            return new Pause(this);
        }

        private void StartWatching()
        {
            if (_watching)
            {
                return;
            }

            _watching = true;
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        private void OnNetworkAddressChanged(object? sender, EventArgs e) => QueueCheck();

        private void QueueCheck()
        {
            // 확인이 이미 예약돼 있으면 그 한 번으로 충분합니다.
            if (_disposed || Interlocked.Exchange(ref _checkPending, 1) == 1)
            {
                return;
            }

            _ = Task.Run(CheckAsync);
        }

        private async Task CheckAsync()
        {
            try
            {
                // 주소 변경 알림은 어댑터가 오르내리는 동안 여러 번 연달아 옵니다. 잠깐 모아서 한 번만 확인합니다.
                await Task.Delay(WaitInterval);
                Interlocked.Exchange(ref _checkPending, 0);

                if (_disposed || Volatile.Read(ref _pauses) > 0)
                {
                    return;
                }

                await _gate.WaitAsync();
                try
                {
                    await SyncAsync();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _checkPending, 0);
                DiagnosticLog.Write("tunnel", $"감시 오류 {ex}");
            }
        }

        /// <summary>실제 어댑터 상태를 프록시에 맞춥니다. 달라진 것이 있으면 <see cref="StateChanged"/> 로 알립니다.</summary>
        private async Task SyncAsync()
        {
            var proxy = _proxy;
            var settings = _settings;
            if (proxy is null || settings is null || _disposed || Volatile.Read(ref _pauses) > 0)
            {
                return;
            }

            var name = VpnSettings.ConnectionName;
            var found = VpnAdapterLocator.Find(name);
            var current = proxy.Adapter;

            if (found is not null && current is not null && IsSame(found, current))
            {
                return;
            }

            if (found is null)
            {
                if (current is not null)
                {
                    // 끊긴 것은 재연결을 기다리지 않고 바로 알립니다(다이얼은 길게는 1분 걸립니다).
                    DiagnosticLog.Write("tunnel", "VPN 어댑터가 사라짐 → 통신 차단");
                    proxy.UseAdapter(null);
                    Report("VPN 연결이 끊겼습니다.");
                }

                if (!settings.AutoConnect)
                {
                    return;
                }

                var sinceLast = DateTime.UtcNow - _lastReconnect;
                if (sinceLast < ReconnectInterval)
                {
                    // 이번 알림을 그냥 버리면 다음 알림이 없을 때 끊긴 채로 남으므로, 간격이 지난 뒤 한 번 더 봅니다.
                    _ = Task.Delay(ReconnectInterval - sinceLast).ContinueWith(_ => QueueCheck(), TaskScheduler.Default);
                    return;
                }

                _lastReconnect = DateTime.UtcNow;
                Report("VPN 연결이 끊겨 다시 연결하는 중입니다...");

                // 이미 한 번 붙었던 항목이므로 그대로 다시 겁니다. PPP 조합을 훑는 것은 시작/설정 저장 때만 합니다.
                var connect = await connections.ConnectAsync(settings);
                if (!connect.Succeeded)
                {
                    DiagnosticLog.Write("tunnel", $"재연결 실패 {connect.Message.Split('\n')[0]}");
                    Report("VPN 연결이 끊겼습니다. 다시 연결하지 못했습니다: " + connect.Message.Split('\n')[0]);
                    return;
                }

                found = await WaitForAdapterAsync(name, present: true);
                if (found is null)
                {
                    Report($"'{name}' VPN 어댑터를 찾지 못했습니다.");
                    return;
                }
            }

            if (!await WaitForDefaultRouteAsync(found))
            {
                proxy.UseAdapter(null);
                Report("터널에 기본 경로가 없습니다. VPN 을 끊고 다시 연결하세요.");
                return;
            }

            DiagnosticLog.Write("tunnel", $"어댑터 다시 묶음 {found.Name} if={found.InterfaceIndex} {found.LocalAddress}");
            proxy.UseAdapter(found);
            Report(Describe(found));
        }

        /// <summary>다시 연결된 VPN 은 인터페이스 인덱스나 주소가 달라질 수 있습니다. 둘 다 같아야 같은 터널입니다.</summary>
        private static bool IsSame(VpnAdapter a, VpnAdapter b) =>
            a.InterfaceIndex == b.InterfaceIndex && a.LocalAddress.Equals(b.LocalAddress);

        private void Report(string status)
        {
            Status = status;

            var handler = StateChanged;
            if (handler is null || _disposed)
            {
                return;
            }

            if (_context is { } context)
            {
                context.Post(_ => handler(this, EventArgs.Empty), null);
            }
            else
            {
                handler(this, EventArgs.Empty);
            }
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
            _disposed = true;

            if (_watching)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                _watching = false;
            }

            _proxy?.Dispose();
            _proxy = null;
        }

        private sealed class Pause(TunnelService owner) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._pauses);
                }
            }
        }
    }
}
