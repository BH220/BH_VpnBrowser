using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>Windows 의 VPN 연결 항목(RAS 전화번호부)을 만들고 걸고 끊습니다.</summary>
    public interface IVpnConnectionManager
    {
        Task<IReadOnlyList<VpnConnectionInfo>> ListAsync();

        /// <summary>설정값으로 연결 항목을 만들거나 갱신합니다(있으면 지우고 다시 만듭니다).</summary>
        Task<CommandResult> ApplyAsync(VpnSettings settings);

        /// <summary>PPP 조합을 바꿔가며 연결하고, 성공한 조합을 <paramref name="settings"/> 에 반영합니다.</summary>
        Task<CommandResult> ApplyAndConnectAsync(VpnSettings settings, Action<string>? progress = null);

        Task<CommandResult> ConnectAsync(VpnSettings settings);

        Task<CommandResult> DisconnectAsync(string name);
    }
}
