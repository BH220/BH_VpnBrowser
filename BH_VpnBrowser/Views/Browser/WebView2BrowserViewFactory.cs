using System.IO;
using System.Windows;
using System.Windows.Controls;
using BH_VpnBrowser.Browser;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BH_VpnBrowser.Views.Browser
{
    /// <summary>
    /// WebView2 컨트롤을 만들어 호스트 패널에 겹쳐 쌓습니다.
    /// 모든 컨트롤이 하나의 <see cref="CoreWebView2Environment"/> 를 공유하므로 프록시 설정이 창 전체에 적용됩니다.
    /// </summary>
    public sealed class WebView2BrowserViewFactory : IBrowserViewFactory
    {
        private static readonly System.Drawing.Color PageBackground = System.Drawing.Color.FromArgb(255, 20, 22, 25);

        private CoreWebView2Environment? _environment;
        private Panel? _host;

        public bool IsInitialized => _environment is not null;

        /// <summary>뷰들을 담을 패널. 창이 만들어질 때 한 번 붙입니다.</summary>
        public void AttachHost(Panel host) => _host = host;

        public async Task InitializeAsync(string browserArguments, string userDataFolder)
        {
            Directory.CreateDirectory(userDataFolder);

            try
            {
                _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = browserArguments });
            }
            catch (WebView2RuntimeNotFoundException ex)
            {
                throw new BrowserRuntimeMissingException(ex.Message);
            }
        }

        public async Task<IBrowserView> CreateAsync()
        {
            if (_environment is null)
            {
                throw new InvalidOperationException("InitializeAsync 를 먼저 호출해야 합니다.");
            }

            if (_host is null)
            {
                throw new InvalidOperationException("AttachHost 로 호스트 패널을 먼저 붙여야 합니다.");
            }

            // Collapsed 로 두면 WPF 가 창(HWND)을 만들지 않아 초기화가 끝나지 않으므로 Hidden 으로 시작합니다.
            // 보일지 말지는 초기화 뒤 IBrowserView.IsVisible 로 정합니다.
            var control = new WebView2
            {
                DefaultBackgroundColor = PageBackground,
                Visibility = Visibility.Hidden,
            };

            _host.Children.Add(control);

            try
            {
                await control.EnsureCoreWebView2Async(_environment);
            }
            catch
            {
                _host.Children.Remove(control);
                control.Dispose();
                throw;
            }

            return new WebView2BrowserView(control, detach: c => _host.Children.Remove(c));
        }
    }
}
