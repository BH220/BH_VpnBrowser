using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// C:\ProgramData\BH Soft\VPN Browser 아래에 설정과 브라우저 프로필을 보관합니다.
    /// 저장소(git)에는 아무것도 남지 않습니다.
    /// <para>
    /// 비밀번호와 사전 공유 키는 DPAPI(CurrentUser)로 암호화합니다.
    /// 파일 자체는 PC 공용 위치에 있지만, 복호화는 저장한 Windows 사용자 계정에서만 됩니다.
    /// </para>
    /// </summary>
    public static class SettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string AppDataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BH Soft",
            "VPN Browser");

        public static string SettingsPath { get; } = Path.Combine(AppDataDirectory, "settings.json");

        /// <summary>WebView2 전용 프로필 폴더. 다른 앱과 브라우저 프로세스를 공유하지 않기 위해 분리합니다.</summary>
        public static string WebViewProfileDirectory { get; } = Path.Combine(AppDataDirectory, "WebView2");

        public static VpnSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new VpnSettings();
                }

                var settings = JsonSerializer.Deserialize<VpnSettings>(File.ReadAllText(SettingsPath))
                               ?? new VpnSettings();
                settings.Password = Unprotect(settings.ProtectedPassword);
                settings.PreSharedKey = Unprotect(settings.ProtectedPreSharedKey);
                return settings;
            }
            catch (Exception)
            {
                // 설정이 깨졌으면 미설정 상태로 시작합니다(터널이 없으므로 통신은 차단됨).
                return new VpnSettings();
            }
        }

        public static void Save(VpnSettings settings)
        {
            Directory.CreateDirectory(AppDataDirectory);
            settings.ProtectedPassword = Protect(settings.Password);
            settings.ProtectedPreSharedKey = Protect(settings.PreSharedKey);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }

        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText))
            {
                return string.Empty;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                // 다른 사용자/PC 에서 복사해 온 설정이면 복호화가 불가능합니다.
                return string.Empty;
            }
        }
    }
}
