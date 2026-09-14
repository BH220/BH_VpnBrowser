using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>설정 창 상태 문구의 성격. 뷰가 색으로 표현합니다.</summary>
    public enum StatusKind
    {
        Neutral,
        Success,
        Failure,
    }

    /// <summary>L2TP/IPsec 터널 설정 대화상자.</summary>
    public sealed partial class SettingsViewModel : ObservableObject
    {
        private const string DefaultHomePage = "https://www.google.com";

        private readonly VpnSettings _original;
        private readonly IVpnConnectionManager _connections;

        [ObservableProperty]
        private string _serverAddress;

        [ObservableProperty]
        private string _userName;

        [ObservableProperty]
        private string _password;

        [ObservableProperty]
        private string _preSharedKey;

        [ObservableProperty]
        private bool _autoConnect;

        [ObservableProperty]
        private bool _blockWebRtcLeak;

        [ObservableProperty]
        private bool _bypassLoopback;

        [ObservableProperty]
        private string _homePage;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        private StatusKind _statusKind = StatusKind.Neutral;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
        private bool _isBusy;

        public SettingsViewModel(VpnSettings settings, IVpnConnectionManager connections)
        {
            _original = settings;
            _connections = connections;

            _serverAddress = settings.ServerAddress;
            _userName = settings.UserName;
            _password = settings.Password;
            _preSharedKey = settings.PreSharedKey;
            _autoConnect = settings.AutoConnect;
            _blockWebRtcLeak = settings.BlockWebRtcLeak;
            _bypassLoopback = settings.BypassLoopback;
            _homePage = settings.HomePage;

            // 연결을 눌러보기 전에 미리 알려줍니다. 이건 앱 밖(관리자 권한)에서만 고칠 수 있습니다.
            if (!RasErrorGuide.IsNatTraversalEnabled())
            {
                SetStatus(
                    "이 PC 가 공유기(NAT) 뒤에 있으면 Windows 가 L2TP 를 거부합니다.\n" +
                    "관리자 PowerShell 에서 아래를 실행하고 재부팅해 주세요:\n" +
                    RasErrorGuide.NatTraversalCommand,
                    StatusKind.Failure);
            }
        }

        /// <summary>창을 닫아 달라는 요청. 인자가 true 면 저장, false 면 취소.</summary>
        public event EventHandler<bool>? CloseRequested;

        /// <summary>저장을 눌렀을 때의 최종 설정. 취소했으면 null.</summary>
        public VpnSettings? Result { get; private set; }

        /// <summary>
        /// Windows VPN 항목을 만들고 자격 증명을 저장한 뒤 실제로 연결해 봅니다.
        /// 어댑터의 인터페이스 인덱스까지 잡혀야 터널을 쓸 수 있으므로 거기까지 확인합니다.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanTestConnection))]
        private async Task TestConnectionAsync()
        {
            if (!TryBuild(out var candidate, out var error))
            {
                SetStatus(error, StatusKind.Failure);
                return;
            }

            IsBusy = true;
            try
            {
                // 서버가 받아주는 PPP 조합을 찾을 때까지 순서대로 시도합니다.
                var connected = await _connections.ApplyAndConnectAsync(
                    candidate, progress: message => SetStatus(message, StatusKind.Neutral));

                if (!connected.Succeeded)
                {
                    SetStatus(connected.Message, StatusKind.Failure);
                    return;
                }

                // 성공한 조합을 결과에 남겨 다음 실행부터 한 번에 붙게 합니다.
                _original.EncryptionLevel = candidate.EncryptionLevel;
                _original.AuthMethods = candidate.AuthMethods;

                VpnAdapter? adapter = null;
                for (var attempt = 0; attempt < 12 && adapter is null; attempt++)
                {
                    await Task.Delay(300);
                    adapter = VpnAdapterLocator.Find(VpnSettings.ConnectionName);
                }

                if (adapter is null)
                {
                    SetStatus("연결은 됐지만 VPN 어댑터를 찾지 못했습니다.", StatusKind.Failure);
                    return;
                }

                var dns = adapter.DnsServers.Count == 0
                    ? "DNS 미수신 (시스템 리졸버로 대체됩니다)"
                    : $"DNS {string.Join(", ", adapter.DnsServers)}";

                SetStatus($"{connected.Message}\n{adapter.Name} · {adapter.LocalAddress} · {dns}", StatusKind.Success);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanTestConnection => !IsBusy;

        [RelayCommand]
        private void Save()
        {
            if (!TryBuild(out var settings, out var error))
            {
                SetStatus(error, StatusKind.Failure);
                return;
            }

            Result = settings;
            CloseRequested?.Invoke(this, true);
        }

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke(this, false);

        private bool TryBuild(out VpnSettings settings, out string error)
        {
            settings = _original.Clone();
            error = string.Empty;

            var server = ServerAddress.Trim();
            if (server.Length == 0)
            {
                error = "서버 주소를 입력하세요.";
                return false;
            }

            var user = UserName.Trim();
            if (user.Length == 0)
            {
                error = "사용자 이름을 입력하세요.";
                return false;
            }

            settings.ServerAddress = server;
            settings.UserName = user;
            settings.Password = Password;
            settings.PreSharedKey = PreSharedKey;
            settings.AutoConnect = AutoConnect;
            settings.BlockWebRtcLeak = BlockWebRtcLeak;
            settings.BypassLoopback = BypassLoopback;
            settings.HomePage = string.IsNullOrWhiteSpace(HomePage) ? DefaultHomePage : HomePage.Trim();
            return true;
        }

        private void SetStatus(string message, StatusKind kind)
        {
            StatusText = message;
            StatusKind = kind;
        }
    }
}
