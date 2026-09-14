using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    public interface ISettingsStore
    {
        /// <summary>WebView2 전용 프로필 폴더.</summary>
        string WebViewProfileDirectory { get; }

        VpnSettings Load();

        void Save(VpnSettings settings);
    }
}
