using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// <see cref="AppPaths.DataDirectory"/>(C:\ProgramData\BH Soft\BH VpnBrowser) 아래에
    /// 설정과 브라우저 프로필을 보관합니다. 저장소(git)에는 아무것도 남지 않습니다.
    /// <para>
    /// 비밀번호와 사전 공유 키는 DPAPI(CurrentUser)로 암호화합니다.
    /// 파일 자체는 PC 공용 위치에 있지만, 복호화는 저장한 Windows 사용자 계정에서만 됩니다.
    /// </para>
    /// </summary>
    public sealed class SettingsStore : ISettingsStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        /// <summary>이름 규칙을 정하기 전 빌드가 쓰던 폴더. 있으면 새 폴더로 한 번 옮깁니다.</summary>
        private static readonly string LegacyDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppPaths.Company,
            "VPN Browser");

        public static string AppDataDirectory { get; } = AppPaths.DataDirectory;

        public static string SettingsPath { get; } = Path.Combine(AppDataDirectory, "settings.json");

        /// <summary>WebView2 전용 프로필 폴더. 다른 앱과 브라우저 프로세스를 공유하지 않기 위해 분리합니다.</summary>
        public string WebViewProfileDirectory { get; } = Path.Combine(AppDataDirectory, "WebView2");

        public VpnSettings Load()
        {
            MigrateLegacyDirectory();
            EnsurePrivateDirectory();

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

                // 예전 빌드가 저장한 "암호화 없음" 조합. 이 조합으로 만든 항목은 IPsec 협상이 매번
                // 실패(789)해서 더는 쓰지 않습니다. 그대로 두면 실행마다 1분을 잃고 나서야 복구됩니다.
                if (string.Equals(settings.EncryptionLevel, "NoEncryption", StringComparison.OrdinalIgnoreCase))
                {
                    settings.EncryptionLevel = "Optional";
                }

                return settings;
            }
            catch (Exception)
            {
                // 설정이 깨졌으면 미설정 상태로 시작합니다(터널이 없으므로 통신은 차단됨).
                return new VpnSettings();
            }
        }

        /// <summary>
        /// 예전 폴더(VPN Browser)만 있고 새 폴더가 없으면 통째로 이름을 바꿉니다.
        /// 설정과 WebView2 프로필(로그인 상태 등)이 그대로 따라옵니다. 브라우저 엔진이 뜨기 전에만 불러야 합니다.
        /// </summary>
        private static void MigrateLegacyDirectory()
        {
            try
            {
                if (Directory.Exists(LegacyDataDirectory) && !Directory.Exists(AppDataDirectory))
                {
                    Directory.Move(LegacyDataDirectory, AppDataDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 옮기지 못하면 새 폴더에서 빈 설정으로 시작합니다. 예전 폴더는 그대로 남습니다.
            }
        }

        /// <summary>
        /// C:\ProgramData 의 기본 권한으로는 PC 의 모든 사용자가 이 폴더를 읽을 수 있습니다.
        /// 서버 주소·사용자 이름은 평문이고 WebView2 프로필(쿠키, 로그인 상태)도 이 아래에 있으므로,
        /// 상속을 끊고 만든 사용자 + SYSTEM + Administrators 만 접근하게 합니다.
        /// 다른 사용자가 만든 폴더면 권한을 바꿀 수 없어 그대로 둡니다(그 사용자의 설정은 어차피 DPAPI 로 못 읽습니다).
        /// </summary>
        private static void EnsurePrivateDirectory()
        {
            try
            {
                var directory = Directory.CreateDirectory(AppDataDirectory);
                var security = directory.GetAccessControl();

                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                {
                    security.RemoveAccessRule(rule);
                }

                var owner = WindowsIdentity.GetCurrent().User;
                if (owner is null)
                {
                    return;
                }

                foreach (var sid in new[]
                {
                    owner,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                })
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        sid,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                }

                directory.SetAccessControl(security);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 권한을 바꾸지 못해도 앱은 동작합니다. 비밀번호·PSK 는 DPAPI 로 따로 보호됩니다.
            }
        }

        public void Save(VpnSettings settings)
        {
            EnsurePrivateDirectory();
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
