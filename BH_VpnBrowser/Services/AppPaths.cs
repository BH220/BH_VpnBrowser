using System.IO;
using System.Reflection;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// 설치 / 데이터 / 레지스트리 위치 규칙. 세 곳 모두 <c>BH Soft\{제품 이름}</c> 아래를 씁니다.
    /// <para>
    /// 제품 이름은 프로젝트(어셈블리) 이름에서 <c>_</c> 를 공백으로 바꾼 것입니다:
    /// <c>BH_VpnBrowser</c> → <c>BH VpnBrowser</c>. 이름을 여기서만 정해 두어야
    /// 설치 프로그램·런처·본체가 같은 폴더와 키를 보게 됩니다.
    /// </para>
    /// <list type="bullet">
    ///   <item>설치: <c>C:\Program Files\BH Soft\BH VpnBrowser</c></item>
    ///   <item>데이터: <c>C:\ProgramData\BH Soft\BH VpnBrowser</c></item>
    ///   <item>레지스트리: <c>HKLM\SOFTWARE\BH Soft\BH VpnBrowser</c></item>
    /// </list>
    /// </summary>
    public static class AppPaths
    {
        public const string Company = "BH Soft";

        /// <summary>이 어셈블리(BH_VpnBrowser)의 이름에서 <c>_</c> 를 공백으로 바꾼 제품 이름.</summary>
        public static string ProductName { get; } =
            (typeof(AppPaths).Assembly.GetName().Name ?? "BH_VpnBrowser").Replace('_', ' ');

        /// <summary>C:\Program Files\BH Soft\BH VpnBrowser</summary>
        public static string InstallDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Company, ProductName);

        /// <summary>C:\ProgramData\BH Soft\BH VpnBrowser</summary>
        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Company, ProductName);

        /// <summary>SOFTWARE\BH Soft\BH VpnBrowser (HKLM / HKCU 어느 쪽에서든 이 상대 경로를 씁니다).</summary>
        public static string RegistryKeyPath { get; } = $@"SOFTWARE\{Company}\{ProductName}";
    }
}
