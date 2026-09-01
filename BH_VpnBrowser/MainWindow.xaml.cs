using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;
using BH_VpnBrowser.ViewModels;
using BH_VpnBrowser.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BH_VpnBrowser
{
    /// <summary>
    /// 탭 브라우저 창.
    /// 모든 탭이 하나의 <see cref="CoreWebView2Environment"/> 를 공유하므로,
    /// 프록시(터널) 설정은 창 전체에 일괄 적용됩니다.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string IpCheckUrl = "https://ifconfig.me/";

        private readonly ObservableCollection<BrowserTab> _tabs = [];
        private readonly ObservableCollection<DownloadItem> _downloads = [];

        private VpnSettings _settings = new();
        private CoreWebView2Environment? _environment;
        private BrowserTab? _activeTab;
        private Socks5ProxyServer? _tunnelProxy;

        /// <summary>
        /// 링크로 열리는 새 창에 즉시 넘겨줄 예비 탭.
        /// NewWindowRequested 안에서 WebView2 를 새로 만들어 기다리면 그동안 요청한 페이지가
        /// deferral 에 묶여 멈춰 있으므로, 미리 하나 만들어 둡니다.
        /// </summary>
        private BrowserTab? _spareTab;
        private string _tunnelStatus = string.Empty;

        public MainWindow()
        {
            InitializeComponent();

            TabStrip.ItemsSource = _tabs;
            DownloadsList.ItemsSource = _downloads;

            Loaded += MainWindow_Loaded;
        }

        // ================= 초기화 =================

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsStore.Load();
            UpdateProxyBadge();
            UpdateDownloadsUi();

            await PrepareTunnelAsync();
            UpdateProxyBadge();

            if (await InitializeEnvironmentAsync())
            {
                await CreateTabAsync(_settings.HomePage);
                _ = PrepareSpareTabAsync();
            }

            // 아직 설정이 없으면 터널이 없어 아무것도 못 여니, 바로 설정 창을 띄웁니다.
            if (!_settings.IsConfigured)
            {
                OpenSettings();
            }
        }

        /// <summary>
        /// L2TP 모드에서 VPN 어댑터를 찾아 로컬 SOCKS5 를 띄웁니다.
        /// 실패하면 프록시를 닫힌 포트로 두어(fail-closed) 실제 IP 로 새지 않게 합니다.
        /// </summary>
        private async Task PrepareTunnelAsync()
        {
            if (!_settings.IsConfigured)
            {
                _tunnelStatus = "VPN 설정이 비어 있습니다.";
                SetStatus("VPN 설정이 필요합니다. 메뉴 > VPN 설정에서 서버 주소와 계정을 입력하세요.");
                return;
            }

            var name = VpnSettings.ConnectionName;
            var adapter = VpnAdapterLocator.Find(name);

            if (adapter is null && _settings.AutoConnect)
            {
                SetStatus($"VPN '{name}' 연결 중...");

                var connections = await VpnConnectionManager.ListAsync();
                var entryExists = connections.Any(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

                // 항목이 이미 있으면 저장된 조합으로 바로 붙여 봅니다.
                var connect = entryExists
                    ? await VpnConnectionManager.ConnectAsync(_settings)
                    : new CommandResult(false, "연결 항목이 없습니다.");

                // 실패하면 서버가 받아주는 PPP 조합을 찾을 때까지 훑고, 찾으면 저장합니다.
                if (!connect.Succeeded)
                {
                    connect = await VpnConnectionManager.ApplyAndConnectAsync(
                        _settings, progress: SetStatus);

                    if (connect.Succeeded)
                    {
                        SettingsStore.Save(_settings);
                    }
                }

                if (!connect.Succeeded)
                {
                    _tunnelStatus = connect.Message;
                    SetStatus("VPN 연결 실패: " + connect.Message.Split('\n')[0]);
                    return;
                }

                // RAS 어댑터가 올라오기까지 잠깐 걸립니다.
                for (var attempt = 0; attempt < 10 && adapter is null; attempt++)
                {
                    await Task.Delay(300);
                    adapter = VpnAdapterLocator.Find(name);
                }
            }

            if (adapter is null)
            {
                _tunnelStatus = string.IsNullOrWhiteSpace(name)
                    ? "연결된 VPN 어댑터를 찾지 못했습니다."
                    : $"'{name}' VPN 어댑터를 찾지 못했습니다.";
                SetStatus(_tunnelStatus);
                return;
            }

            _tunnelProxy = new Socks5ProxyServer(adapter);
            _tunnelStatus = $"{adapter.Name} · {adapter.LocalAddress}";
            SetStatus($"터널 준비 완료 - {adapter.Name} ({adapter.LocalAddress})");
        }

        private async Task<bool> InitializeEnvironmentAsync()
        {
            try
            {
                SetStatus("브라우저 엔진 초기화 중...");

                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = _settings.BuildBrowserArguments(_tunnelProxy?.Endpoint),
                };

                Directory.CreateDirectory(SettingsStore.WebViewProfileDirectory);

                _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: SettingsStore.WebViewProfileDirectory,
                    options: options);

                SetStatus("준비 완료");
                return true;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                SetStatus("WebView2 런타임이 설치되어 있지 않습니다.");
                MessageBox.Show(
                    this,
                    "Microsoft Edge WebView2 런타임이 필요합니다.\n\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/ 에서 Evergreen Runtime 을 설치한 뒤 다시 실행해 주세요.",
                    "WebView2 런타임 없음",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            catch (Exception ex)
            {
                SetStatus("초기화 실패: " + ex.Message);
                MessageBox.Show(this, ex.ToString(), "브라우저 초기화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // ================= 탭 =================

        private async Task<BrowserTab?> CreateTabAsync(string? url, bool activate = true, bool spare = false)
        {
            if (_environment is null)
            {
                return null;
            }

            var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 22, 25) };
            var tab = new BrowserTab(view);

            // 예비 탭은 탭 목록에 넣지 않습니다. Collapsed 로 두면 WPF 가 창을 만들지 않아
            // 초기화가 끝나지 않으므로 Hidden 으로 둡니다.
            if (spare)
            {
                view.Visibility = Visibility.Hidden;
            }

            BrowserHost.Children.Add(view);

            if (!spare)
            {
                _tabs.Add(tab);
            }

            if (activate)
            {
                SelectTab(tab);
            }

            try
            {
                await view.EnsureCoreWebView2Async(_environment);
            }
            catch (Exception ex)
            {
                SetStatus("탭 생성 실패: " + ex.Message);

                if (spare)
                {
                    BrowserHost.Children.Remove(view);
                    view.Dispose();
                }
                else
                {
                    CloseTab(tab);
                }

                return null;
            }

            AttachTabEvents(tab);

            if (!string.IsNullOrWhiteSpace(url))
            {
                Navigate(tab, url);
            }

            return tab;
        }

        private void AttachTabEvents(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;

            core.Settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += (_, e) =>
            {
                tab.IsLoading = true;
                tab.Address = e.Uri;
                if (tab.IsSelected)
                {
                    SetStatus($"이동 중: {e.Uri}");
                    SyncAddressBar();
                    UpdateReloadButton();
                }
            };

            core.SourceChanged += (_, _) =>
            {
                tab.Address = core.Source;
                if (tab.IsSelected)
                {
                    SyncAddressBar();
                }
            };

            core.HistoryChanged += (_, _) =>
            {
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;
                if (tab.IsSelected)
                {
                    SyncNavigationButtons();
                }
            };

            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = core.DocumentTitle;
                if (tab.IsSelected)
                {
                    UpdateWindowTitle();
                }
            };

            core.NavigationCompleted += (_, e) =>
            {
                tab.IsLoading = false;
                tab.CanGoBack = core.CanGoBack;
                tab.CanGoForward = core.CanGoForward;

                if (!tab.IsSelected)
                {
                    return;
                }

                SyncNavigationButtons();
                SetStatus(e.IsSuccess ? "완료" : DescribeFailure(e.WebErrorStatus));
            };

            // 프록시가 인증을 요구할 때(407 등) 저장된 자격 증명으로 응답합니다.
            core.DownloadStarting += (_, e) =>
            {
                AddDownload(e.DownloadOperation);
                e.Handled = true; // 기본 다운로드 UI 억제
            };

            // Ctrl+휠처럼 WebView2 가 스스로 처리한 확대/축소도 표시에 반영합니다.
            tab.View.ZoomFactorChanged += (_, _) =>
            {
                if (tab.IsSelected)
                {
                    UpdateZoomIndicator();
                }
            };

            core.NewWindowRequested += OnNewWindowRequested;

            core.WindowCloseRequested += (_, _) => CloseTab(tab);

            tab.Address = core.Source;
        }

        /// <summary>
        /// target=_blank 등으로 열리는 창을 새 탭으로 받습니다.
        /// <para>
        /// 여기서 WebView2 를 새로 만들어 기다리면(deferral) 그동안 링크를 누른 페이지가
        /// 멈춰 있어, 새 탭이 주소만 띄운 채 가만히 있는 것처럼 보입니다.
        /// 그래서 미리 만들어 둔 예비 탭을 즉시 넘겨줍니다.
        /// </para>
        /// </summary>
        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var spare = _spareTab;

            if (spare?.View.CoreWebView2 is { } core)
            {
                // 넘겨준 창은 WebView2 가 알아서 목적지로 이동시킵니다.
                // window.opener 도 그대로 유지됩니다.
                AdoptSpareTab(spare, e.Uri);
                e.NewWindow = core;
                e.Handled = true;
            }
            else
            {
                // 예비 탭이 아직 준비되지 않았으면 우리가 직접 엽니다.
                // 기다리지 않는 것이 핵심이라 여기서도 await 하지 않습니다.
                e.Handled = true;
                _ = CreateTabAsync(e.Uri);
            }

            _ = PrepareSpareTabAsync();
        }

        /// <summary>예비 탭을 실제 탭으로 승격시켜 화면에 띄웁니다.</summary>
        private void AdoptSpareTab(BrowserTab tab, string target)
        {
            _spareTab = null;

            _tabs.Add(tab);
            SelectTab(tab);

            // 이동이 시작되기 전에도 목적지가 보이게 합니다.
            tab.Address = target;
            SyncAddressBar();
        }

        /// <summary>다음 새 창을 위해 탭 하나를 미리 초기화해 둡니다.</summary>
        private async Task PrepareSpareTabAsync()
        {
            if (_spareTab is not null || _environment is null)
            {
                return;
            }

            _spareTab = await CreateTabAsync(url: null, activate: false, spare: true);
        }

        private void SelectTab(BrowserTab tab)
        {
            foreach (var other in _tabs)
            {
                other.IsSelected = ReferenceEquals(other, tab);
            }

            _activeTab = tab;
            SyncAddressBar();
            SyncNavigationButtons();
            UpdateWindowTitle();
            UpdateZoomIndicator();
        }

        private void CloseTab(BrowserTab tab)
        {
            var index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            _tabs.RemoveAt(index);
            BrowserHost.Children.Remove(tab.View);
            tab.View.Dispose();

            if (_tabs.Count == 0)
            {
                Close();
                return;
            }

            if (ReferenceEquals(_activeTab, tab))
            {
                SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
        }

        private void Navigate(BrowserTab tab, string? input)
        {
            var uri = UrlHelper.Normalize(input);
            if (uri is null || tab.View.CoreWebView2 is null)
            {
                return;
            }

            tab.View.CoreWebView2.Navigate(uri.ToString());
        }

        // ================= 다운로드 =================

        private void AddDownload(CoreWebView2DownloadOperation operation)
        {
            var item = new DownloadItem(operation);
            item.PropertyChanged += Download_PropertyChanged;
            _downloads.Insert(0, item);

            UpdateDownloadsUi();
            DownloadsPopup.IsOpen = true;
        }

        private void Download_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DownloadItem.CanCancel) or nameof(DownloadItem.IsCompleted))
            {
                UpdateDownloadsUi();
            }
        }

        private void UpdateDownloadsUi()
        {
            var active = _downloads.Count(d => d.CanCancel);

            DownloadsBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadsBadgeText.Text = active > 9 ? "9+" : active.ToString();
            NoDownloadsText.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= 상태 표시 =================

        private void SyncAddressBar()
        {
            if (!AddressBox.IsKeyboardFocusWithin)
            {
                AddressBox.Text = _activeTab?.Address ?? string.Empty;
            }
        }

        private void SyncNavigationButtons()
        {
            BackButton.IsEnabled = _activeTab?.CanGoBack ?? false;
            ForwardButton.IsEnabled = _activeTab?.CanGoForward ?? false;
            ReloadButton.IsEnabled = _activeTab is not null;
            UpdateReloadButton();
        }

        /// <summary>일반 브라우저처럼 로딩 중에는 중단(X), 완료되면 새로고침으로 바꿉니다.</summary>
        private void UpdateReloadButton()
        {
            var loading = _activeTab?.IsLoading ?? false;

            ReloadGlyph.Data = Geometry.Parse(loading
                ? "M 6,6 L 18,18 M 18,6 L 6,18"
                : "M 20.5,15 A 9,9 0 1 1 18.4,5.6 L 23,10 M 23,4 L 23,10 L 17,10");

            ReloadButton.ToolTip = loading ? "중단 (Esc)" : "새로고침 (F5)";
        }

        /// <summary>작업 표시줄에 현재 활성 탭의 제목을 그대로 노출합니다.</summary>
        private void UpdateWindowTitle() => Title = _activeTab?.Title ?? "BH VPN Browser";

        private void SetStatus(string message) => StatusText.Text = message;

        private void UpdateProxyBadge()
        {
            // 어댑터를 찾아 로컬 SOCKS5 가 떠야만 실제로 터널을 탑니다.
            if (_tunnelProxy is null)
            {
                ProxyBadge.Background = (Brush)FindResource("DangerBrush");
                ProxyBadgeText.Text = "터널 끊김 - 통신 차단됨";
                ProxyBadge.ToolTip = string.IsNullOrEmpty(_tunnelStatus)
                    ? "VPN 어댑터를 찾지 못했습니다. 실제 IP 노출을 막기 위해 통신을 차단합니다."
                    : _tunnelStatus + "\n실제 IP 노출을 막기 위해 통신을 차단합니다.";
                return;
            }

            ProxyBadge.Background = (Brush)FindResource("SuccessBrush");
            ProxyBadgeText.Text = "L2TP 터널 · " + _tunnelStatus;
            ProxyBadge.ToolTip = "이 창의 소켓만 VPN 어댑터에 바인딩됩니다. PC의 다른 앱은 영향을 받지 않습니다.";
        }

        private string DescribeFailure(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.ConnectionAborted or
            CoreWebView2WebErrorStatus.ConnectionReset or
            CoreWebView2WebErrorStatus.CannotConnect when _tunnelProxy is null =>
                "VPN 터널이 없어 통신을 차단했습니다. 메뉴 > VPN 설정에서 연결한 뒤 다시 시작하세요.",
            CoreWebView2WebErrorStatus.OperationCanceled => "이동이 취소되었습니다.",
            _ => $"이동 실패: {status}",
        };

        // ================= 확대/축소 =================

        /// <summary>크롬과 같은 배율 단계입니다.</summary>
        private static readonly double[] ZoomSteps =
            [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

        /// <summary>배율이 같은지 비교할 때 쓰는 여유값(부동소수 오차 흡수).</summary>
        private const double ZoomEpsilon = 0.001;

        private void StepZoom(int direction)
        {
            if (_activeTab is null)
            {
                return;
            }

            var current = _activeTab.View.ZoomFactor;

            var index = direction > 0
                ? Array.FindIndex(ZoomSteps, step => step > current + ZoomEpsilon)
                : Array.FindLastIndex(ZoomSteps, step => step < current - ZoomEpsilon);

            // 이미 양 끝이면 그대로 둡니다.
            if (index < 0)
            {
                return;
            }

            SetZoom(ZoomSteps[index]);
        }

        private void SetZoom(double factor)
        {
            if (_activeTab is null)
            {
                return;
            }

            _activeTab.View.ZoomFactor = factor;
            UpdateZoomIndicator();
        }

        /// <summary>현재 탭의 배율을 주소창 오른쪽 표시에 반영합니다.</summary>
        private void UpdateZoomIndicator()
        {
            var factor = _activeTab?.View.ZoomFactor ?? 1.0;
            var text = $"{Math.Round(factor * 100)}%";

            ZoomText.Text = text;
            ZoomPopupText.Text = text;

            // 100% 면 눈에 띄지 않게, 아니면 강조색으로 둡니다(크롬과 같은 방식).
            var isDefault = Math.Abs(factor - 1.0) < ZoomEpsilon;
            ZoomText.Foreground = (Brush)FindResource(isDefault ? "TextMutedBrush" : "AccentBrush");
        }

        private void ZoomButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateZoomIndicator();
            ZoomPopup.IsOpen = !ZoomPopup.IsOpen;
        }

        private void ZoomInButton_Click(object sender, RoutedEventArgs e) => StepZoom(1);

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

        private void ZoomResetButton_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

        // ================= 단축키 =================

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Esc: 로딩 중이면 중단 (일반 브라우저와 동일)
            if (e.Key == Key.Escape && _activeTab is { IsLoading: true } tab)
            {
                tab.View.CoreWebView2?.Stop();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            if (HandleShortcut((uint)KeyInterop.VirtualKeyFromKey(e.Key)))
            {
                e.Handled = true;
            }
        }

        /// <summary>Ctrl 조합 단축키. 반환값이 true 면 처리됨.</summary>
        private bool HandleShortcut(uint virtualKey)
        {
            switch (virtualKey)
            {
                case 0x54: // T
                    _ = CreateTabAsync(_settings.HomePage);
                    return true;

                case 0x57: // W
                    if (_activeTab is not null)
                    {
                        CloseTab(_activeTab);
                    }
                    return true;

                case 0x4A: // J
                    DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
                    return true;

                case 0x4C: // L
                    AddressBox.Focus();
                    AddressBox.SelectAll();
                    return true;

                case 0x09: // Tab
                    SelectNextTab();
                    return true;

                case 0x30: // 0
                case 0x60: // 숫자패드 0
                    SetZoom(1.0);
                    return true;

                case 0xBB: // = / +
                case 0x6B: // 숫자패드 +
                    StepZoom(1);
                    return true;

                case 0xBD: // - / _
                case 0x6D: // 숫자패드 -
                    StepZoom(-1);
                    return true;

                default:
                    return false;
            }
        }

        private void SelectNextTab()
        {
            if (_activeTab is null || _tabs.Count < 2)
            {
                return;
            }

            var next = (_tabs.IndexOf(_activeTab) + 1) % _tabs.Count;
            SelectTab(_tabs[next]);
        }

        // ================= 창 캡션 =================

        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
        private const int DwmCornerPreference = 33;

        /// <summary>DWMWCP_ROUND - 크롬과 동일한 Windows 11 기본 라운딩</summary>
        private const int DwmCornerRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Windows 11 이상에서만 반영되고, 그 이전 버전에서는 조용히 무시됩니다.
            var handle = new WindowInteropHelper(this).Handle;
            var preference = DwmCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmCornerPreference, ref preference, sizeof(int));
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _tunnelProxy?.Dispose();
            base.OnClosed(e);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var maximized = WindowState == WindowState.Maximized;

            // WindowStyle=None 으로 최대화하면 리사이즈 테두리만큼 화면 밖으로 나가 잘립니다.
            var border = SystemParameters.WindowResizeBorderThickness;
            RootPanel.Margin = maximized
                ? new Thickness(border.Left, border.Top, border.Right, border.Bottom)
                : new Thickness(0);

            // 최대화 상태에서는 겹친 사각형(복원) 모양으로 바꿉니다.
            MaximizeGlyph.Data = Geometry.Parse(maximized
                ? "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z"
                : "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");

            MaximizeButton.ToolTip = maximized ? "이전 크기로 복원" : "최대화";
        }

        // ================= UI 이벤트 =================

        private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: BrowserTab tab })
            {
                return;
            }

            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab);
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                SelectTab(tab);
            }
        }

        private void TabCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: BrowserTab tab })
            {
                CloseTab(tab);
            }

            e.Handled = true;
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e) => _ = CreateTabAsync(_settings.HomePage);

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _activeTab is null)
            {
                return;
            }

            Navigate(_activeTab, AddressBox.Text);
            _activeTab.View.Focus();
            e.Handled = true;
        }

        private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab?.View.CoreWebView2?.CanGoBack == true)
            {
                _activeTab.View.CoreWebView2.GoBack();
            }
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab?.View.CoreWebView2?.CanGoForward == true)
            {
                _activeTab.View.CoreWebView2.GoForward();
            }
        }

        /// <summary>로딩 중이면 중단, 아니면 새로고침.</summary>
        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            var core = _activeTab?.View.CoreWebView2;
            if (core is null)
            {
                return;
            }

            if (_activeTab!.IsLoading)
            {
                core.Stop();
            }
            else
            {
                core.Reload();
            }
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab is not null)
            {
                Navigate(_activeTab, _settings.HomePage);
            }
        }

        private void IpCheckButton_Click(object sender, RoutedEventArgs e)
        {
            MenuPopup.IsOpen = false;

            // 보고 있던 페이지를 잃지 않도록 새 탭에서 엽니다.
            _ = CreateTabAsync(IpCheckUrl);
        }

        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            MenuPopup.IsOpen = false;
            _activeTab?.View.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }

        private void DownloadsButton_Click(object sender, RoutedEventArgs e) =>
            DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;

        private void MenuButton_Click(object sender, RoutedEventArgs e) =>
            MenuPopup.IsOpen = !MenuPopup.IsOpen;

        private void ClearDownloads_Click(object sender, RoutedEventArgs e)
        {
            foreach (var finished in _downloads.Where(d => !d.CanCancel).ToList())
            {
                finished.PropertyChanged -= Download_PropertyChanged;
                _downloads.Remove(finished);
            }

            UpdateDownloadsUi();
        }

        private void DownloadCancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DownloadItem item })
            {
                item.Cancel();
            }
        }

        private void DownloadShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DownloadItem item })
            {
                item.ShowInFolder();
            }
        }

        private void DownloadOpen_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: DownloadItem { IsCompleted: true } item })
            {
                item.OpenFile();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MenuPopup.IsOpen = false;
            OpenSettings();
        }

        private async void OpenSettings()
        {
            var wasConfigured = _settings.IsConfigured;
            var hadTunnel = _tunnelProxy is not null;

            var dialog = new SettingsWindow(_settings.Clone()) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var updated = dialog.Result;

            // 터널은 WebView2 환경 생성 시점에 고정되므로, 새로 연결됐으면 재시작해야 반영됩니다.
            var needsRestart = updated.AffectsBrowserEnvironment(_settings)
                               || !hadTunnel
                               || !wasConfigured;

            SettingsStore.Save(updated);
            _settings = updated;

            // 저장만 하고 Windows VPN 항목에 반영하지 않으면 사전 공유 키 같은 값이
            // 전화번호부에 빠진 채로 남아 연결이 실패합니다. 저장 = 적용이어야 합니다.
            SetStatus("VPN 연결 항목에 적용하는 중...");
            var applied = await VpnConnectionManager.ApplyAsync(updated);
            UpdateProxyBadge();

            if (!applied.Succeeded)
            {
                SetStatus("VPN 항목 적용 실패: " + applied.Message.Split('\n')[0]);
                MessageBox.Show(this, applied.Message, "VPN 설정 적용 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!needsRestart)
            {
                SetStatus("설정을 적용했습니다.");
                return;
            }

            var answer = MessageBox.Show(
                this,
                "터널 설정은 브라우저 엔진이 시작할 때 적용됩니다.\n지금 프로그램을 다시 시작할까요?",
                "다시 시작 필요",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
            else
            {
                SetStatus("설정이 저장되었습니다. 다음 실행부터 적용됩니다.");
            }
        }

        private static void RestartApplication()
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            Application.Current.Shutdown();
        }
    }
}
