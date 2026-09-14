namespace BH_VpnBrowser.Browser
{
    /// <summary>
    /// 탭 하나가 다루는 웹 페이지 뷰.
    /// <para>
    /// ViewModel 은 WebView2 를 직접 알지 않고 이 인터페이스만 씁니다.
    /// 구현(WebView2 어댑터)은 View 계층인 <c>Views/Browser</c> 에 있습니다.
    /// </para>
    /// </summary>
    public interface IBrowserView : IDisposable
    {
        string Source { get; }

        string DocumentTitle { get; }

        bool CanGoBack { get; }

        bool CanGoForward { get; }

        double ZoomFactor { get; set; }

        /// <summary>화면에 보이는지. 선택된 탭의 뷰만 보이게 합니다.</summary>
        bool IsVisible { get; set; }

        void Navigate(string url);

        void GoBack();

        void GoForward();

        void Reload();

        void Stop();

        void ShowPrintDialog();

        /// <summary>키보드 포커스를 페이지로 옮깁니다(주소창에서 Enter 를 친 뒤 등).</summary>
        void Focus();

        /// <summary>이동을 시작했을 때. 인자는 목적지 URI.</summary>
        event EventHandler<string>? NavigationStarting;

        event EventHandler? SourceChanged;

        event EventHandler? HistoryChanged;

        event EventHandler? DocumentTitleChanged;

        event EventHandler<BrowserNavigationResult>? NavigationCompleted;

        /// <summary>Ctrl+휠처럼 뷰가 스스로 바꾼 배율도 여기로 옵니다.</summary>
        event EventHandler? ZoomFactorChanged;

        event EventHandler<IDownloadOperation>? DownloadStarting;

        /// <summary>target=_blank 등으로 새 창을 요청받았을 때.</summary>
        event EventHandler<NewWindowRequest>? NewWindowRequested;

        /// <summary>페이지가 window.close() 로 스스로 닫히려 할 때.</summary>
        event EventHandler? CloseRequested;
    }

    /// <summary>이동 실패를 ViewModel 이 이해할 수 있는 정도로만 분류합니다.</summary>
    public enum NavigationFailure
    {
        None,

        /// <summary>서버에 닿지 못함(연결 거부/중단/재설정). 터널이 없을 때 나는 종류입니다.</summary>
        CannotConnect,

        Canceled,

        Other,
    }

    public sealed record BrowserNavigationResult(bool IsSuccess, NavigationFailure Failure, string Detail);

    /// <summary>
    /// 새 창 요청에 대한 ViewModel 의 응답.
    /// <see cref="NewWindow"/> 에 미리 초기화된 뷰를 넘겨주면 요청한 페이지가 멈추지 않고 바로 이동합니다.
    /// 뷰를 넘기지 않더라도 <see cref="Handled"/> 를 켜면 WebView2 가 별도 창을 띄우지 않습니다.
    /// </summary>
    public sealed class NewWindowRequest(string uri)
    {
        public string Uri { get; } = uri;

        public IBrowserView? NewWindow { get; set; }

        public bool Handled { get; set; }
    }
}
