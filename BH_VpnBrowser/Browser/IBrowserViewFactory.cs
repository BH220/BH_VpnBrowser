namespace BH_VpnBrowser.Browser
{
    /// <summary>
    /// 브라우저 뷰를 만들어 화면에 붙여 주는 공장. 모든 뷰가 하나의 엔진 환경을 공유하므로
    /// 프록시(터널) 설정은 <see cref="InitializeAsync"/> 시점에 창 전체에 고정됩니다.
    /// </summary>
    public interface IBrowserViewFactory
    {
        bool IsInitialized { get; }

        /// <summary>브라우저 엔진(공유 환경)을 준비합니다.</summary>
        /// <param name="browserArguments">Chromium 커맨드라인 인자(프록시 등).</param>
        /// <param name="userDataFolder">이 앱 전용 프로필 폴더.</param>
        /// <exception cref="BrowserRuntimeMissingException">WebView2 런타임이 설치돼 있지 않을 때.</exception>
        Task InitializeAsync(string browserArguments, string userDataFolder);

        /// <summary>
        /// 뷰를 만들어 호스트에 붙이고 엔진 초기화까지 마칩니다.
        /// 보이지 않는 상태로 만들어지며, 표시는 <see cref="IBrowserView.IsVisible"/> 로 정합니다.
        /// </summary>
        Task<IBrowserView> CreateAsync();
    }

    public sealed class BrowserRuntimeMissingException(string message) : Exception(message);
}
