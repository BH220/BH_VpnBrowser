using BH_VpnBrowser.Browser;
using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;
using BH_VpnBrowser.ViewModels;
using BH_VpnBrowser.Views;
using BH_VpnBrowser.Views.Browser;
using Microsoft.Extensions.DependencyInjection;

namespace BH_VpnBrowser.DependencyInjection
{
    /// <summary>
    /// 앱의 의존성 등록을 한곳에 모았습니다. <see cref="App"/> 은 여기서 만든 컨테이너로 주 창을 꺼내 띄우고,
    /// 종료 시 컨테이너를 폐기해 싱글턴(터널의 로컬 SOCKS5 등)을 정리합니다.
    /// </summary>
    public static class ServiceRegistration
    {
        public static ServiceProvider BuildServiceProvider()
        {
            var services = new ServiceCollection();

            services
                .AddServices()
                .AddViewModels()
                .AddViews();

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }

        private static IServiceCollection AddServices(this IServiceCollection services)
        {
            services.AddSingleton<ISettingsStore, SettingsStore>();
            services.AddSingleton<IVpnConnectionManager, VpnConnectionManager>();
            services.AddSingleton<ITunnelService, TunnelService>();
            services.AddSingleton<IAppLifetime, AppLifetime>();
            services.AddSingleton<IDialogService, DialogService>();

            // 브라우저 뷰 공장은 View 계층 구현이지만 ViewModel 은 인터페이스로만 씁니다.
            // MainWindow 가 호스트 패널을 붙여야 하므로 구체 타입도 같은 인스턴스로 노출합니다.
            services.AddSingleton<WebView2BrowserViewFactory>();
            services.AddSingleton<IBrowserViewFactory>(provider => provider.GetRequiredService<WebView2BrowserViewFactory>());

            return services;
        }

        private static IServiceCollection AddViewModels(this IServiceCollection services)
        {
            services.AddSingleton<MainViewModel>();

            // 설정 창은 열 때마다 현재 설정의 복제본으로 새 ViewModel 을 만듭니다.
            services.AddSingleton<Func<VpnSettings, SettingsViewModel>>(provider =>
                settings => new SettingsViewModel(settings, provider.GetRequiredService<IVpnConnectionManager>()));

            return services;
        }

        private static IServiceCollection AddViews(this IServiceCollection services)
        {
            services.AddSingleton<MainWindow>();
            return services;
        }
    }
}
