using System.Runtime.InteropServices;
using System.Text;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// RAS 항목을 직접 다이얼합니다.
    /// <para>
    /// rasdial.exe 는 (1) 비밀번호를 커맨드라인에 노출시키고,
    /// (2) RasSetCredentials 로 저장한 자격 증명을 실제 다이얼에 쓰지 않아 오류 628 이 납니다.
    /// 그래서 자격 증명을 구조체로 직접 넘기는 RasDial API 를 씁니다.
    /// </para>
    /// </summary>
    public static class RasDialer
    {
        private const int EntryNameLength = 257;      // RAS_MaxEntryName + 1
        private const int PhoneNumberLength = 129;    // RAS_MaxPhoneNumber + 1
        private const int CallbackNumberLength = 129; // RAS_MaxCallbackNumber + 1
        private const int UserNameLength = 257;       // UNLEN + 1
        private const int PasswordLength = 257;       // PWLEN + 1
        private const int DomainLength = 16;          // DNLEN + 1

        private const uint Success = 0;

        /// <summary>동기로 연결합니다. 성공하면 0, 아니면 RAS 오류 코드를 돌려줍니다.</summary>
        public static (uint Code, string Message) Dial(string entryName, string userName, string password)
        {
            var parameters = new RasDialParams
            {
                Size = Marshal.SizeOf<RasDialParams>(),
                EntryName = Truncate(entryName, EntryNameLength),
                PhoneNumber = string.Empty,
                CallbackNumber = string.Empty,
                UserName = Truncate(userName, UserNameLength),
                Password = Truncate(password, PasswordLength),
                Domain = string.Empty,
                SubEntry = 0,
                CallbackId = IntPtr.Zero,
                IfIndex = 0,
            };

            // notifierType 0 + notifier null = 연결이 끝날 때까지 블로킹.
            var code = RasDialW(IntPtr.Zero, null, ref parameters, 0, IntPtr.Zero, out _);

            return code == Success
                ? (Success, string.Empty)
                : (code, DescribeError(code));
        }

        public static string DescribeError(uint code)
        {
            var buffer = new StringBuilder(512);
            return RasGetErrorStringW(code, buffer, buffer.Capacity) == Success
                ? buffer.ToString().Trim()
                : $"RAS 오류 {code}";
        }

        private static string Truncate(string value, int maxLengthWithNull) =>
            value.Length >= maxLengthWithNull ? value[..(maxLengthWithNull - 1)] : value;

        /// <summary>
        /// RASDIALPARAMSW (Windows Vista 이상). dwIfIndex 까지 포함해 x64 에서 2120 바이트입니다.
        /// dwSize 가 맞지 않으면 RasDial 이 632(ERROR_INVALID_SIZE)를 돌려주므로 Marshal.SizeOf 로 채웁니다.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RasDialParams
        {
            public int Size;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = EntryNameLength)]
            public string EntryName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PhoneNumberLength)]
            public string PhoneNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CallbackNumberLength)]
            public string CallbackNumber;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UserNameLength)]
            public string UserName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PasswordLength)]
            public string Password;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DomainLength)]
            public string Domain;

            public int SubEntry;
            public IntPtr CallbackId;
            public int IfIndex;
        }

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasDialW(
            IntPtr extensions,
            string? phonebook,
            ref RasDialParams parameters,
            uint notifierType,
            IntPtr notifier,
            out IntPtr connection);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasGetErrorStringW(uint error, StringBuilder buffer, int bufferSize);
    }
}
