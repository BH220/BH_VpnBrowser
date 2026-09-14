using System.Collections.ObjectModel;
using System.ComponentModel;
using BH_VpnBrowser.Browser;
using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>
    /// 탭 브라우저 창의 상태와 동작. 터널 준비 → 브라우저 엔진 초기화 → 첫 탭 순서로 시작합니다.
    /// 모든 탭이 하나의 엔진 환경을 공유하므로 프록시(터널) 설정은 창 전체에 일괄 적용됩니다.
    /// </summary>
    public sealed partial class MainViewModel : ObservableObject, IBrowserTabOwner
    {
        private const string IpCheckUrl = "https://ifconfig.me/";
        private const string DefaultWindowTitle = "BH VPN Browser";

        private readonly ISettingsStore _settingsStore;
        private readonly ITunnelService _tunnel;
        private readonly IVpnConnectionManager _connections;
        private readonly IBrowserViewFactory _browserFactory;
        private readonly IDialogService _dialogs;
        private readonly IAppLifetime _lifetime;

        private VpnSettings _settings = new();

        /// <summary>
        /// 링크로 열리는 새 창에 즉시 넘겨줄 예비 탭.
        /// 새 창 요청 안에서 뷰를 새로 만들어 기다리면 그동안 요청한 페이지가 멈춰 있으므로 미리 하나 만들어 둡니다.
        /// </summary>
        private BrowserTabViewModel? _spareTab;

        [ObservableProperty]
        private BrowserTabViewModel? _activeTab;

        [ObservableProperty]
        private string _addressText = string.Empty;

        /// <summary>주소창을 편집하는 동안에는 페이지 이동으로 주소를 덮어쓰지 않습니다. 뷰가 포커스에 따라 설정합니다.</summary>
        [ObservableProperty]
        private bool _isAddressEditing;

        [ObservableProperty]
        private string _windowTitle = DefaultWindowTitle;

        [ObservableProperty]
        private string _statusText = "준비";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TunnelBadgeText), nameof(TunnelBadgeToolTip))]
        private string _tunnelStatus = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TunnelBadgeText), nameof(TunnelBadgeToolTip))]
        private bool _isTunnelReady;

        [ObservableProperty]
        private string _zoomText = "100%";

        [ObservableProperty]
        private bool _isZoomDefault = true;

        [ObservableProperty]
        private bool _isDownloadsOpen;

        [ObservableProperty]
        private bool _isMenuOpen;

        [ObservableProperty]
        private bool _isZoomPopupOpen;

        [ObservableProperty]
        private bool _hasDownloads;

        [ObservableProperty]
        private bool _hasActiveDownloads;

        [ObservableProperty]
        private string _activeDownloadBadge = "0";

        public MainViewModel(
            ISettingsStore settingsStore,
            ITunnelService tunnel,
            IVpnConnectionManager connections,
            IBrowserViewFactory browserFactory,
            IDialogService dialogs,
            IAppLifetime lifetime)
        {
            _settingsStore = settingsStore;
            _tunnel = tunnel;
            _connections = connections;
            _browserFactory = browserFactory;
            _dialogs = dialogs;
            _lifetime = lifetime;
        }

        public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

        public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

        /// <summary>어댑터를 찾아 로컬 SOCKS5 가 떠야만 실제로 터널을 탑니다.</summary>
        public string TunnelBadgeText => IsTunnelReady ? "L2TP 터널 · " + TunnelStatus : "터널 끊김 - 통신 차단됨";

        public string TunnelBadgeToolTip => IsTunnelReady
            ? "이 창의 소켓만 VPN 어댑터에 바인딩됩니다. PC의 다른 앱은 영향을 받지 않습니다."
            : string.IsNullOrEmpty(TunnelStatus)
                ? "VPN 어댑터를 찾지 못했습니다. 실제 IP 노출을 막기 위해 통신을 차단합니다."
                : TunnelStatus + "\n실제 IP 노출을 막기 위해 통신을 차단합니다.";

        // ================= 시작 =================

        /// <summary>창이 뜬 뒤 한 번 실행됩니다.</summary>
        [RelayCommand]
        private async Task InitializeAsync()
        {
            _settings = _settingsStore.Load();

            var tunnel = await _tunnel.PrepareAsync(_settings, message => StatusText = message);
            TunnelStatus = tunnel.Status;
            IsTunnelReady = tunnel.IsReady;

            if (tunnel.SettingsChanged)
            {
                _settingsStore.Save(_settings);
            }

            if (await InitializeBrowserAsync())
            {
                await CreateTabAsync(_settings.HomePage);
                _ = PrepareSpareTabAsync();
            }

            // 아직 설정이 없으면 터널이 없어 아무것도 못 여니, 바로 설정 창을 띄웁니다.
            if (!_settings.IsConfigured)
            {
                await OpenSettingsAsync();
            }
        }

        /// <summary>터널이 없으면 닫힌 포트를 프록시로 넘겨(fail-closed) 실제 IP 로 새지 않게 합니다.</summary>
        private async Task<bool> InitializeBrowserAsync()
        {
            try
            {
                StatusText = "브라우저 엔진 초기화 중...";

                await _browserFactory.InitializeAsync(
                    _settings.BuildBrowserArguments(_tunnel.Endpoint),
                    _settingsStore.WebViewProfileDirectory);

                StatusText = "준비 완료";
                return true;
            }
            catch (BrowserRuntimeMissingException)
            {
                StatusText = "WebView2 런타임이 설치되어 있지 않습니다.";
                _dialogs.ShowWarning(
                    "WebView2 런타임 없음",
                    "Microsoft Edge WebView2 런타임이 필요합니다.\n\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/ 에서 Evergreen Runtime 을 설치한 뒤 다시 실행해 주세요.");
                return false;
            }
            catch (Exception ex)
            {
                StatusText = "초기화 실패: " + ex.Message;
                _dialogs.ShowError("브라우저 초기화 실패", ex.ToString());
                return false;
            }
        }

        // ================= 탭 =================

        private async Task<BrowserTabViewModel?> CreateTabAsync(string? url, bool spare = false)
        {
            if (!_browserFactory.IsInitialized)
            {
                return null;
            }

            IBrowserView view;
            try
            {
                view = await _browserFactory.CreateAsync();
            }
            catch (Exception ex)
            {
                StatusText = "탭 생성 실패: " + ex.Message;
                return null;
            }

            var tab = new BrowserTabViewModel(view, this);
            AttachTab(tab);

            // 예비 탭은 탭 목록에 넣지 않고 새 창 요청이 올 때 승격시킵니다.
            if (!spare)
            {
                Tabs.Add(tab);
                SelectTab(tab);
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                tab.Navigate(url);
            }

            return tab;
        }

        private void AttachTab(BrowserTabViewModel tab)
        {
            tab.NavigationStarted += (_, uri) =>
            {
                if (tab.IsSelected)
                {
                    StatusText = $"이동 중: {uri}";
                }
            };

            tab.NavigationFinished += (_, result) =>
            {
                if (tab.IsSelected)
                {
                    StatusText = result.IsSuccess ? "완료" : DescribeFailure(result);
                }
            };

            tab.PropertyChanged += Tab_PropertyChanged;

            tab.View.DownloadStarting += (_, operation) => AddDownload(operation);
            tab.View.NewWindowRequested += OnNewWindowRequested;
        }

        private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not BrowserTabViewModel { IsSelected: true } tab)
            {
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(BrowserTabViewModel.Address):
                    SyncAddress();
                    break;

                case nameof(BrowserTabViewModel.Title):
                    WindowTitle = tab.Title;
                    break;

                case nameof(BrowserTabViewModel.ZoomFactor):
                    UpdateZoomIndicator();
                    break;
            }
        }

        /// <summary>
        /// target=_blank 등으로 열리는 창을 새 탭으로 받습니다.
        /// 미리 만들어 둔 예비 탭을 즉시 넘겨주면 링크를 누른 페이지가 멈추지 않습니다.
        /// 예비 탭이 아직 없으면 기다리지 않고 우리가 직접 새 탭을 엽니다.
        /// </summary>
        private void OnNewWindowRequested(object? sender, NewWindowRequest request)
        {
            var spare = _spareTab;

            if (spare is not null)
            {
                _spareTab = null;
                Tabs.Add(spare);
                SelectTab(spare);

                // 이동이 시작되기 전에도 목적지가 보이게 합니다.
                spare.Address = request.Uri;

                // 넘겨준 뷰는 브라우저가 알아서 목적지로 이동시킵니다. window.opener 도 유지됩니다.
                request.NewWindow = spare.View;
            }
            else
            {
                _ = CreateTabAsync(request.Uri);
            }

            request.Handled = true;
            _ = PrepareSpareTabAsync();
        }

        /// <summary>다음 새 창을 위해 탭 하나를 미리 초기화해 둡니다.</summary>
        private async Task PrepareSpareTabAsync()
        {
            if (_spareTab is not null || !_browserFactory.IsInitialized)
            {
                return;
            }

            _spareTab = await CreateTabAsync(url: null, spare: true);
        }

        public void SelectTab(BrowserTabViewModel tab)
        {
            foreach (var other in Tabs)
            {
                other.IsSelected = ReferenceEquals(other, tab);
            }

            ActiveTab = tab;
        }

        public void CloseTab(BrowserTabViewModel tab)
        {
            var index = Tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            Tabs.RemoveAt(index);
            tab.PropertyChanged -= Tab_PropertyChanged;
            tab.Dispose();

            if (Tabs.Count == 0)
            {
                _lifetime.Shutdown();
                return;
            }

            if (ReferenceEquals(ActiveTab, tab))
            {
                SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
            }
        }

        partial void OnActiveTabChanged(BrowserTabViewModel? value)
        {
            SyncAddress();
            WindowTitle = value?.Title ?? DefaultWindowTitle;
            UpdateZoomIndicator();
        }

        partial void OnIsAddressEditingChanged(bool value)
        {
            if (!value)
            {
                SyncAddress();
            }
        }

        private void SyncAddress()
        {
            if (!IsAddressEditing)
            {
                AddressText = ActiveTab?.Address ?? string.Empty;
            }
        }

        [RelayCommand]
        private Task NewTabAsync() => CreateTabAsync(_settings.HomePage);

        [RelayCommand]
        private void CloseActiveTab()
        {
            if (ActiveTab is not null)
            {
                CloseTab(ActiveTab);
            }
        }

        [RelayCommand]
        private void SelectNextTab()
        {
            if (ActiveTab is null || Tabs.Count < 2)
            {
                return;
            }

            SelectTab(Tabs[(Tabs.IndexOf(ActiveTab) + 1) % Tabs.Count]);
        }

        /// <summary>주소창 내용으로 이동한 뒤 포커스를 페이지로 넘깁니다.</summary>
        [RelayCommand]
        private void Navigate()
        {
            if (ActiveTab is null)
            {
                return;
            }

            ActiveTab.Navigate(AddressText);
            ActiveTab.View.Focus();
        }

        [RelayCommand]
        private void GoHome() => ActiveTab?.Navigate(_settings.HomePage);

        private string DescribeFailure(BrowserNavigationResult result) => result.Failure switch
        {
            NavigationFailure.CannotConnect when !_tunnel.IsReady =>
                "VPN 터널이 없어 통신을 차단했습니다. 메뉴 > VPN 설정에서 연결한 뒤 다시 시작하세요.",
            NavigationFailure.Canceled => "이동이 취소되었습니다.",
            _ => $"이동 실패: {result.Detail}",
        };

        // ================= 확대/축소 =================

        [RelayCommand]
        private void ZoomIn() => ActiveTab?.StepZoom(1);

        [RelayCommand]
        private void ZoomOut() => ActiveTab?.StepZoom(-1);

        [RelayCommand]
        private void ZoomReset() => ActiveTab?.ResetZoom();

        [RelayCommand]
        private void ToggleZoomPopup()
        {
            UpdateZoomIndicator();
            IsZoomPopupOpen = !IsZoomPopupOpen;
        }

        /// <summary>현재 탭의 배율을 주소창 오른쪽 표시에 반영합니다. 100% 면 눈에 띄지 않게 둡니다(크롬과 같은 방식).</summary>
        private void UpdateZoomIndicator()
        {
            ZoomText = ActiveTab?.ZoomText ?? "100%";
            IsZoomDefault = ActiveTab?.IsZoomDefault ?? true;
        }

        // ================= 메뉴 / 팝업 =================

        [RelayCommand]
        private void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

        [RelayCommand]
        private void ToggleDownloads() => IsDownloadsOpen = !IsDownloadsOpen;

        [RelayCommand]
        private void Print()
        {
            IsMenuOpen = false;
            ActiveTab?.Print();
        }

        /// <summary>보고 있던 페이지를 잃지 않도록 새 탭에서 엽니다.</summary>
        [RelayCommand]
        private Task CheckIpAsync()
        {
            IsMenuOpen = false;
            return CreateTabAsync(IpCheckUrl);
        }

        [RelayCommand]
        private async Task OpenSettingsAsync()
        {
            IsMenuOpen = false;

            var wasConfigured = _settings.IsConfigured;
            var hadTunnel = _tunnel.IsReady;

            var updated = _dialogs.ShowSettings(_settings.Clone());
            if (updated is null)
            {
                return;
            }

            // 터널은 브라우저 엔진 생성 시점에 고정되므로, 새로 연결됐으면 재시작해야 반영됩니다.
            var needsRestart = updated.AffectsBrowserEnvironment(_settings) || !hadTunnel || !wasConfigured;

            _settingsStore.Save(updated);
            _settings = updated;

            // 저장만 하고 Windows VPN 항목에 반영하지 않으면 사전 공유 키 같은 값이
            // 전화번호부에 빠진 채로 남아 연결이 실패합니다. 저장 = 적용이어야 합니다.
            StatusText = "VPN 연결 항목에 적용하는 중...";
            var applied = await _connections.ApplyAsync(updated);

            if (!applied.Succeeded)
            {
                StatusText = "VPN 항목 적용 실패: " + applied.Message.Split('\n')[0];
                _dialogs.ShowWarning("VPN 설정 적용 실패", applied.Message);
                return;
            }

            if (!needsRestart)
            {
                StatusText = "설정을 적용했습니다.";
                return;
            }

            if (_dialogs.Confirm("다시 시작 필요", "터널 설정은 브라우저 엔진이 시작할 때 적용됩니다.\n지금 프로그램을 다시 시작할까요?"))
            {
                _lifetime.Restart();
            }
            else
            {
                StatusText = "설정이 저장되었습니다. 다음 실행부터 적용됩니다.";
            }
        }

        // ================= 다운로드 =================

        private void AddDownload(IDownloadOperation operation)
        {
            var item = new DownloadItemViewModel(operation);
            item.PropertyChanged += Download_PropertyChanged;
            Downloads.Insert(0, item);

            UpdateDownloadSummary();
            IsDownloadsOpen = true;
        }

        private void Download_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(DownloadItemViewModel.CanCancel) or nameof(DownloadItemViewModel.IsCompleted))
            {
                UpdateDownloadSummary();
            }
        }

        [RelayCommand]
        private void ClearDownloads()
        {
            foreach (var finished in Downloads.Where(d => !d.CanCancel).ToList())
            {
                finished.PropertyChanged -= Download_PropertyChanged;
                Downloads.Remove(finished);
            }

            UpdateDownloadSummary();
        }

        private void UpdateDownloadSummary()
        {
            var active = Downloads.Count(d => d.CanCancel);

            HasDownloads = Downloads.Count > 0;
            HasActiveDownloads = active > 0;
            ActiveDownloadBadge = active > 9 ? "9+" : active.ToString();
        }
    }
}
