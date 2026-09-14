using System.Diagnostics;
using System.Windows;
using BH_VpnBrowser.Services;

namespace BH_VpnBrowser
{
    /// <summary>WPF <see cref="Application"/> 에 대한 재시작/종료 구현.</summary>
    public sealed class AppLifetime : IAppLifetime
    {
        public void Restart()
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            Shutdown();
        }

        public void Shutdown() => Application.Current.Shutdown();
    }
}
