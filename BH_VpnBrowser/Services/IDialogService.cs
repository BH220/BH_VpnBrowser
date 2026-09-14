using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// ViewModel 이 창을 직접 띄우지 않도록 대화상자를 대신 열어 줍니다. 구현은 View 계층에 있습니다.
    /// </summary>
    public interface IDialogService
    {
        void ShowWarning(string title, string message);

        void ShowError(string title, string message);

        /// <summary>예/아니요 질문. 예를 고르면 true.</summary>
        bool Confirm(string title, string message);

        /// <summary>VPN 설정 대화상자를 엽니다. 저장했으면 새 설정, 취소했으면 null.</summary>
        VpnSettings? ShowSettings(VpnSettings current);
    }
}
