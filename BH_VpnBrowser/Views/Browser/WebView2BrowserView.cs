using System.Windows;
using BH_VpnBrowser.Browser;
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

            Core.Settings.IsGeneralAutofillEnabled = false;

            Core.NavigationStarting += (_, e) => NavigationStarting?.Invoke(this, e.Uri);
            Core.SourceChanged += (_, _) => SourceChanged?.Invoke(this, EventArgs.Empty);
            Core.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
            Core.DocumentTitleChanged += (_, _) => DocumentTitleChanged?.Invoke(this, EventArgs.Empty);

            Core.NavigationCompleted += (_, e) => NavigationCompleted?.Invoke(
                this, new BrowserNavigationResult(e.IsSuccess, Classify(e.IsSuccess, e.WebErrorStatus), e.WebErrorStatus.ToString()));

            Core.DownloadStarting += (_, e) =>
            {
                DownloadStarting?.Invoke(this, new WebView2DownloadOperation(e.DownloadOperation));
                e.Handled = true; // 기본 다운로드 UI 억제
            };

            Core.NewWindowRequested += (_, e) =>
            {
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
            };

            Core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

            control.ZoomFactorChanged += (_, _) => ZoomFactorChanged?.Invoke(this, EventArgs.Empty);
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

        /// <summary>보이지 않는 탭은 Collapsed 로 두어 레이아웃에서도 빠지게 합니다.</summary>
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                _control.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public event EventHandler<string>? NavigationStarting;

        public event EventHandler? SourceChanged;

        public event EventHandler? HistoryChanged;

        public event EventHandler? DocumentTitleChanged;

        public event EventHandler<BrowserNavigationResult>? NavigationCompleted;

        public event EventHandler? ZoomFactorChanged;

        public event EventHandler<IDownloadOperation>? DownloadStarting;

        public event EventHandler<NewWindowRequest>? NewWindowRequested;

        public event EventHandler? CloseRequested;

        public void Navigate(string url) => Core.Navigate(url);

        public void GoBack() => Core.GoBack();

        public void GoForward() => Core.GoForward();

        public void Reload() => Core.Reload();

        public void Stop() => Core.Stop();

        public void ShowPrintDialog() => Core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);

        public void Focus() => _control.Focus();

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
            _detach(_control);
            _control.Dispose();
        }
    }
}
