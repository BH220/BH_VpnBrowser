using System.Windows;
using BH_VpnBrowser.DependencyInjection;
using BH_VpnBrowser.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BH_VpnBrowser
{
    /// <summary>
    /// 컨테이너를 만들고 주 창을 띄우는 것만 합니다. 의존성 등록은 <see cref="ServiceRegistration"/> 에 있습니다.
    /// </summary>
    public partial class App : Application
    {
        private ServiceProvider? _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _services = ServiceRegistration.BuildServiceProvider();

            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }

        /// <summary>싱글턴(터널의 로컬 SOCKS5 등)을 정리합니다.</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            _services?.Dispose();
            base.OnExit(e);
        }
    }
}
