using System.Windows;
using BH_VpnBrowser.Browser;
using BH_VpnBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BH_VpnBrowser.Views.Browser
{
    /// <summary>
    /// 초기화가 끝난 WebView2 컨트롤을 <see cref="IBrowserView"/> 로 감쌉니다.
    /// WebView2 의 이벤트·타입을 ViewModel 이 이해하는 형태로 바꿔 주는 유일한 자리입니다.
    /// </summary>
    public sealed class WebView2BrowserView : IBrowserView
    {
        private readonly WebView2 _control;
        private readonly Action<WebView2> _detach;
        private bool _isVisible;

        public WebView2BrowserView(WebView2 control, Action<WebView2> detach)
        {
            _control = control;
            _detach = detach;
            Core = control.CoreWebView2 ?? throw new InvalidOperationException("CoreWebView2 가 초기화되지 않았습니다.");

            DetachDefaultWindowCloseHandler(control, Core);

            Core.Settings.IsGeneralAutofillEnabled = false;

            Core.NavigationStarting += (_, e) =>
            {
                DiagnosticLog.Write("nav", $"시작 {e.Uri} (userInitiated={e.IsUserInitiated}, redirect={e.IsRedirected})");
                NavigationStarting?.Invoke(this, e.Uri);
            };
            Core.SourceChanged += (_, _) => SourceChanged?.Invoke(this, EventArgs.Empty);
            Core.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
            Core.DocumentTitleChanged += (_, _) => DocumentTitleChanged?.Invoke(this, EventArgs.Empty);

            Core.NavigationCompleted += (_, e) =>
            {
                DiagnosticLog.Write("nav", $"완료 success={e.IsSuccess} status={e.WebErrorStatus} http={e.HttpStatusCode} src={Core.Source}");
                NavigationCompleted?.Invoke(
                    this, new BrowserNavigationResult(e.IsSuccess, Classify(e.IsSuccess, e.WebErrorStatus), e.WebErrorStatus.ToString()));
            };

            Core.DownloadStarting += (_, e) =>
            {
                DiagnosticLog.Write("download", $"시작 {e.DownloadOperation.Uri} → {e.ResultFilePath} (state={e.DownloadOperation.State})");

                var request = new DownloadRequest(new WebView2DownloadOperation(e.DownloadOperation));
                DownloadStarting?.Invoke(this, request);

                e.Cancel = request.Cancel;
                e.Handled = true; // 기본 다운로드 UI 억제
            };

            Core.NewWindowRequested += (_, e) =>
            {
                // Ctrl+클릭은 Chromium 이 "새 창(배경 탭)" 요청으로 보냅니다. 이 경우는 우클릭 메뉴의
                // "기본 브라우저로 열기"와 똑같이 Windows 기본 브라우저로 넘깁니다(VPN 터널을 타지 않습니다).
                // 스크립트가 여는 창(window.open)이 Ctrl 을 누른 채라고 밖으로 새지 않게 사용자 동작일 때만 합니다.
                if (e.IsUserInitiated && IsControlDown() && IsWebLink(e.Uri, out var external))
                {
                    DiagnosticLog.Write("window", $"Ctrl+클릭 {e.Uri} → 기본 브라우저");
                    OpenInDefaultBrowser(external);
                    e.Handled = true;
                    return;
                }

                var request = new NewWindowRequest(e.Uri);
                NewWindowRequested?.Invoke(this, request);

                if (request.NewWindow is WebView2BrowserView view)
                {
                    e.NewWindow = view.Core;
                    e.Handled = true;
                }
                else
                {
                    e.Handled = request.Handled;
                }

                DiagnosticLog.Write("window", $"새 창 요청 {e.Uri} → 예비 뷰 {(request.NewWindow is not null ? "제공" : "없음")}, handled={e.Handled}");
            };

            Core.WindowCloseRequested += (_, _) =>
            {
                DiagnosticLog.Write("window", $"닫기 요청 src={Core.Source}");
                CloseRequested?.Invoke(this, EventArgs.Empty);
            };

            Core.ContextMenuRequested += OnContextMenuRequested;

            control.ZoomFactorChanged += (_, _) => ZoomFactorChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>WebView2 가 "새 창에서 링크 열기" 항목에 붙이는 이름. 우리 항목을 그 바로 아래에 넣습니다.</summary>
        private const string OpenLinkInNewWindowItem = "openLinkInNewWindow";

        /// <summary>
        /// 링크를 우클릭했을 때 기본 메뉴에 "기본 브라우저로 열기"를 더합니다.
        /// 그 링크는 Windows 의 기본 브라우저(크롬 등)에서 열리므로 <b>VPN 터널을 타지 않고 PC 의 평소 네트워크로</b> 나갑니다.
        /// </summary>
        private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var target = e.ContextMenuTarget;
            if (!target.HasLinkUri || !IsWebLink(target.LinkUri, out var link))
            {
                return;
            }

            var item = Core.Environment.CreateContextMenuItem(
                "기본 브라우저로 열기 (VPN 미사용)", null, CoreWebView2ContextMenuItemKind.Command);

            item.CustomItemSelected += (_, _) => OpenInDefaultBrowser(link);

            var items = e.MenuItems;
            var index = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Name == OpenLinkInNewWindowItem)
                {
                    index = i + 1;
                    break;
                }
            }

            items.Insert(index, item);
        }

        /// <summary>
        /// 셸에 넘길 주소는 http/https 로만 한정합니다. 페이지가 준 주소를 그대로 셸에 넘기면
        /// file: 이나 임의의 프로토콜 처리기(다른 프로그램 실행)까지 열릴 수 있습니다.
        /// </summary>
        private static bool IsWebLink(string? uri, out Uri link) =>
            Uri.TryCreate(uri, UriKind.Absolute, out link!)
            && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps);

        /// <summary>VK_CONTROL</summary>
        private const int VirtualKeyControl = 0x11;

        /// <summary>
        /// 지금 Ctrl 이 눌려 있는지. 포커스가 WebView2 의 자식 창에 있을 때는 WPF 의 키보드 상태가 갱신되지 않으므로
        /// Windows 에 직접 묻습니다.
        /// </summary>
        private static bool IsControlDown() => (GetAsyncKeyState(VirtualKeyControl) & 0x8000) != 0;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private static void OpenInDefaultBrowser(Uri link)
        {
            try
            {
                DiagnosticLog.Write("window", $"기본 브라우저로 열기 {link}");
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 기본 브라우저가 지정돼 있지 않거나 실행이 막힌 경우. 페이지 동작에는 영향을 주지 않습니다.
                DiagnosticLog.Write("window", $"기본 브라우저 실행 실패 {ex.Message}");
            }
        }

        internal CoreWebView2 Core { get; }

        public string Source => Core.Source;

        public string DocumentTitle => Core.DocumentTitle;

        public bool CanGoBack => Core.CanGoBack;

        public bool CanGoForward => Core.CanGoForward;

        public double ZoomFactor
        {
            get => _control.ZoomFactor;
            set => _control.ZoomFactor = value;
        }

        /// <summary>보이지 않는 탭은 Hidden 으로 둡니다. 호스트 그리드가 모든 뷰를 겹쳐 놓으므로 레이아웃에는 영향이 없습니다.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                _control.Visibility = value ? Visibility.Visible : Visibility.Hidden;
            }
        }

        public event EventHandler<string>? NavigationStarting;

        public event EventHandler? SourceChanged;

        public event EventHandler? HistoryChanged;

        public event EventHandler? DocumentTitleChanged;

        public event EventHandler<BrowserNavigationResult>? NavigationCompleted;

        public event EventHandler? ZoomFactorChanged;

        public event EventHandler<DownloadRequest>? DownloadStarting;

        public event EventHandler<NewWindowRequest>? NewWindowRequested;

        public event EventHandler? CloseRequested;

        public void Navigate(string url)
        {
            DiagnosticLog.Write("nav", $"Navigate 호출 {url} (현재 {Core.Source})");
            Core.Navigate(url);
        }

        public void GoBack() => Core.GoBack();

        public void GoForward() => Core.GoForward();

        public void Reload() => Core.Reload();

        public void Stop() => Core.Stop();

        public void ShowPrintDialog() => Core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);

        public void Focus() => _control.Focus();

        /// <summary>
        /// WPF WebView2 컨트롤은 페이지가 창 닫기를 요청하면(window.close, 다운로드로 끝난 팝업 등)
        /// 기본으로 <b>자기를 담은 WPF Window 를 닫습니다</b>. 탭 브라우저에서는 그게 메인 창이라 앱이 그대로 종료됩니다.
        /// 공개된 끄기 옵션이 없어 컨트롤이 등록한 처리기를 이벤트에서 떼어냅니다. 닫기는 우리가 탭 단위로 처리합니다.
        /// 처리기는 "WebView2 WPF 어셈블리가 붙인 것"으로 가려내므로 컨트롤 안의 메서드 이름이 바뀌어도 찾습니다.
        /// 다만 이벤트 뒤의 필드 이름(WindowCloseRequested 포함)에는 여전히 의존하며, 1.0.4191.47 에서 확인했습니다.
        /// 못 찾으면 로그만 남기고 기본 동작이 유지되므로, SDK 를 올린 뒤에는 팝업을 닫아 메인 창이 살아 있는지 확인해야 합니다.
        /// </summary>
        private static void DetachDefaultWindowCloseHandler(WebView2 control, CoreWebView2 core)
        {
            // CoreWebView2 의 WindowCloseRequested 이벤트 뒤에 있는 대리자 목록에서 컨트롤이 등록한 항목을 찾아 뺍니다.
            // 컨트롤의 처리기는 내부 타입에 있어 타입 계층으로는 찾을 수 없고, 이벤트의 호출 목록에서만 보입니다.
            var backingFields = typeof(CoreWebView2)
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType) && f.Name.Contains("WindowCloseRequested", StringComparison.OrdinalIgnoreCase));

            var controlAssembly = typeof(WebView2).Assembly;
            var removed = 0;
            foreach (var field in backingFields)
            {
                if (field.GetValue(core) is not Delegate handlers)
                {
                    continue;
                }

                foreach (var handler in handlers.GetInvocationList())
                {
                    // 이 시점에는 우리 처리기를 아직 붙이지 않았으므로, WebView2 WPF 어셈블리가 붙인 것은 전부 컨트롤의 기본 처리기입니다.
                    // 메서드 이름만 보면 SDK 가 이름을 바꿨을 때 조용히 놓치므로 어셈블리로도 가려냅니다.
                    var fromControl = handler.Method.DeclaringType?.Assembly == controlAssembly
                                      || handler.Target?.GetType().Assembly == controlAssembly
                                      || handler.Method.Name == "CoreWebView2_WindowCloseRequested";

                    if (handler is EventHandler<object> typed && fromControl)
                    {
                        core.WindowCloseRequested -= typed;
                        removed++;
                    }
                }
            }

            if (removed == 0)
            {
                DiagnosticLog.Write("window", "경고: WebView2 기본 창 닫기 처리기를 찾지 못했습니다. 팝업이 닫히면 메인 창이 닫힐 수 있습니다.");
            }
        }

        private static NavigationFailure Classify(bool isSuccess, CoreWebView2WebErrorStatus status)
        {
            if (isSuccess)
            {
                return NavigationFailure.None;
            }

            return status switch
            {
                CoreWebView2WebErrorStatus.ConnectionAborted or
                CoreWebView2WebErrorStatus.ConnectionReset or
                CoreWebView2WebErrorStatus.CannotConnect => NavigationFailure.CannotConnect,
                CoreWebView2WebErrorStatus.OperationCanceled => NavigationFailure.Canceled,
                _ => NavigationFailure.Other,
            };
        }

        public void Dispose()
        {
            DiagnosticLog.Write("window", $"뷰 폐기 src={Core.Source}");
            _detach(_control);
            _control.Dispose();
            DiagnosticLog.Write("window", "뷰 폐기 완료");
        }
    }
}
