using System.Windows;
using System.Windows.Media;
using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;

namespace BH_VpnBrowser.Views
{
    /// <summary>L2TP/IPsec 터널 설정 대화상자.</summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(VpnSettings settings)
        {
            InitializeComponent();
            Result = settings;
            LoadFrom(settings);

            // 연결을 눌러보기 전에 미리 알려줍니다. 이건 앱 밖(관리자 권한)에서만 고칠 수 있습니다.
            if (!RasErrorGuide.IsNatTraversalEnabled())
            {
                ShowStatus(
                    "이 PC 가 공유기(NAT) 뒤에 있으면 Windows 가 L2TP 를 거부합니다.\n" +
                    "관리자 PowerShell 에서 아래를 실행하고 재부팅해 주세요:\n" +
                    RasErrorGuide.NatTraversalCommand,
                    isSuccess: false);
            }
        }

        /// <summary>저장 버튼을 눌렀을 때의 최종 설정.</summary>
        public VpnSettings Result { get; private set; }

        private void LoadFrom(VpnSettings settings)
        {
            ServerBox.Text = settings.ServerAddress;
            UserBox.Text = settings.UserName;
            PasswordBox.Password = settings.Password;
            PskBox.Password = settings.PreSharedKey;

            AutoConnectCheck.IsChecked = settings.AutoConnect;
            WebRtcCheck.IsChecked = settings.BlockWebRtcLeak;
            BypassLoopbackCheck.IsChecked = settings.BypassLoopback;
            HomeBox.Text = settings.HomePage;
        }

        private bool TryBuild(out VpnSettings settings, out string error)
        {
            settings = Result.Clone();
            error = string.Empty;

            var server = ServerBox.Text.Trim();
            if (server.Length == 0)
            {
                error = "서버 주소를 입력하세요.";
                return false;
            }

            var user = UserBox.Text.Trim();
            if (user.Length == 0)
            {
                error = "사용자 이름을 입력하세요.";
                return false;
            }

            settings.ServerAddress = server;
            settings.UserName = user;
            settings.Password = PasswordBox.Password;
            settings.PreSharedKey = PskBox.Password;

            settings.AutoConnect = AutoConnectCheck.IsChecked == true;
            settings.BlockWebRtcLeak = WebRtcCheck.IsChecked == true;
            settings.BypassLoopback = BypassLoopbackCheck.IsChecked == true;
            settings.HomePage = string.IsNullOrWhiteSpace(HomeBox.Text)
                ? "https://www.google.com"
                : HomeBox.Text.Trim();

            return true;
        }

        /// <summary>
        /// Windows VPN 항목을 만들고 자격 증명을 저장한 뒤 실제로 연결해 봅니다.
        /// 어댑터의 인터페이스 인덱스까지 잡혀야 터널을 쓸 수 있으므로 거기까지 확인합니다.
        /// </summary>
        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuild(out var candidate, out var error))
            {
                ShowStatus(error, isSuccess: false);
                return;
            }

            ApplyButton.IsEnabled = false;
            try
            {
                // 서버가 받아주는 PPP 조합을 찾을 때까지 순서대로 시도합니다.
                var connected = await VpnConnectionManager.ApplyAndConnectAsync(
                    candidate,
                    progress: message => ShowStatus(message, isSuccess: null));

                if (!connected.Succeeded)
                {
                    ShowStatus(connected.Message, isSuccess: false);
                    return;
                }

                // 성공한 조합을 결과에 남겨 다음 실행부터 한 번에 붙게 합니다.
                Result.EncryptionLevel = candidate.EncryptionLevel;
                Result.AuthMethods = candidate.AuthMethods;

                VpnAdapter? adapter = null;
                for (var attempt = 0; attempt < 12 && adapter is null; attempt++)
                {
                    await Task.Delay(300);
                    adapter = VpnAdapterLocator.Find(VpnSettings.ConnectionName);
                }

                if (adapter is null)
                {
                    ShowStatus("연결은 됐지만 VPN 어댑터를 찾지 못했습니다.", isSuccess: false);
                    return;
                }

                var dns = adapter.DnsServers.Count == 0
                    ? "DNS 미수신 (시스템 리졸버로 대체됩니다)"
                    : $"DNS {string.Join(", ", adapter.DnsServers)}";

                ShowStatus(
                    $"{connected.Message}\n{adapter.Name} · {adapter.LocalAddress} · {dns}",
                    isSuccess: true);
            }
            finally
            {
                ApplyButton.IsEnabled = true;
            }
        }

        private void ShowStatus(string message, bool? isSuccess)
        {
            StatusText.Text = message;
            StatusText.Foreground = isSuccess switch
            {
                true => (Brush)FindResource("SuccessBrush"),
                false => (Brush)FindResource("DangerBrush"),
                _ => (Brush)FindResource("TextMutedBrush"),
            };
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryBuild(out var settings, out var error))
            {
                MessageBox.Show(this, error, "입력 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = settings;
            DialogResult = true;
        }
    }
}
