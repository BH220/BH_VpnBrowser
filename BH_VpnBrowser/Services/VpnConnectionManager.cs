using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    public sealed record VpnConnectionInfo(
        string Name,
        string ServerAddress,
        string TunnelType,
        bool SplitTunneling,
        string EncryptionLevel,
        IReadOnlyList<string> AuthenticationMethods)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(ServerAddress) ? Name : $"{Name}  ({ServerAddress})";

        /// <summary>
        /// 항목이 설정과 같은 서버·PPP 조합으로 만들어져 있는지. 다르면 다이얼 전에 다시 만들어야 합니다.
        /// 예전 빌드가 남긴 "암호화 없음" 항목처럼 실패가 정해진 다이얼에 1분을 쓰는 일을 막습니다.
        /// </summary>
        public bool Matches(VpnSettings settings) =>
            string.Equals(ServerAddress, settings.ServerAddress, StringComparison.OrdinalIgnoreCase)
            && string.Equals(EncryptionLevel, settings.EncryptionLevel, StringComparison.OrdinalIgnoreCase)
            && AuthenticationMethods.Count == settings.AuthMethods.Length
            && AuthenticationMethods.All(m => settings.AuthMethods.Contains(m, StringComparer.OrdinalIgnoreCase));
    }

    public sealed record CommandResult(bool Succeeded, string Message);

    /// <summary>
    /// Windows 의 VPN 연결(RAS 전화번호부)을 다룹니다.
    /// <para>
    /// 항목 생성/삭제는 PowerShell cmdlet 으로, 실제 다이얼은 RasDial API 로 합니다.
    /// 비밀번호는 RasDial 구조체로, PSK 는 자식 프로세스 환경 변수로 넘겨 둘 다 커맨드라인에 노출되지 않습니다.
    /// </para>
    /// </summary>
    public sealed class VpnConnectionManager : IVpnConnectionManager
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// 터널에 얹는 기본 경로의 메트릭. 물리 NIC 의 기본 경로(보통 25 이하)보다 훨씬 커야
        /// PC 전체 트래픽이 계속 물리 NIC 로 나갑니다.
        /// </summary>
        private const int TunnelRouteMetric = 9000;

        /// <summary>연결 항목 제거 재시도 횟수와 간격. 막 끊긴 직후에는 잠시 잠겨 있습니다.</summary>
        private const int RemoveAttempts = 4;

        private static readonly TimeSpan RemoveRetryDelay = TimeSpan.FromMilliseconds(500);

        public async Task<IReadOnlyList<VpnConnectionInfo>> ListAsync()
        {
            var result = await RunPowerShellAsync(
                "Get-VpnConnection -AllUserConnection:$false -ErrorAction SilentlyContinue | " +
                "Select-Object Name,ServerAddress,TunnelType,SplitTunneling,EncryptionLevel,AuthenticationMethod | " +
                "ConvertTo-Json -Compress");

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Message))
            {
                return [];
            }

            try
            {
                using var document = JsonDocument.Parse(result.Message);
                var root = document.RootElement;

                // 항목이 하나면 배열이 아니라 객체로 나옵니다.
                var elements = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToList()
                    : [root];

                return [.. elements.Select(ToConnectionInfo)];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        private static VpnConnectionInfo ToConnectionInfo(JsonElement element) => new(
            Name: element.TryGetProperty("Name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            ServerAddress: element.TryGetProperty("ServerAddress", out var server) ? server.GetString() ?? string.Empty : string.Empty,
            TunnelType: element.TryGetProperty("TunnelType", out var type) ? type.ToString() : string.Empty,
            SplitTunneling: element.TryGetProperty("SplitTunneling", out var split) && split.ValueKind == JsonValueKind.True,
            EncryptionLevel: element.TryGetProperty("EncryptionLevel", out var encryption) ? encryption.ToString() : string.Empty,
            AuthenticationMethods: ReadStrings(element, "AuthenticationMethod"));

        /// <summary>값이 하나면 배열이 아니라 단일 값으로 나올 수 있어 둘 다 받습니다.</summary>
        private static IReadOnlyList<string> ReadStrings(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
            {
                return [];
            }

            return value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Select(v => v.ToString())]
                : [value.ToString()];
        }

        /// <summary>
        /// 설정값으로 Windows VPN 연결 항목을 만들거나 갱신하고, 자격 증명까지 저장합니다.
        /// 이미 있으면 서버 주소/PSK 를 덮어씁니다.
        /// </summary>
        public async Task<CommandResult> ApplyAsync(VpnSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.ServerAddress))
            {
                return new CommandResult(false, "서버 주소를 입력하세요.");
            }

            var name = VpnSettings.ConnectionName;
            var existing = await ListAsync();
            var alreadyExists = existing.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            // Set-VpnConnection 은 PSK 를 바꿀 수 없으므로, 있으면 지우고 다시 만듭니다.
            if (alreadyExists)
            {
                // 연결 중인 항목은 지워지지 않습니다. PPP 조합을 바꿔가며 재시도할 때
                // 앞 시도가 남긴 연결이 그대로 있으면 여기서 걸립니다.
                await DisconnectAsync(name);

                var removed = await RemoveEntryAsync(name);
                if (!removed.Succeeded)
                {
                    return removed;
                }
            }

            var script = new StringBuilder()
                .Append("Add-VpnConnection")
                .Append($" -Name {Quote(name)}")
                .Append($" -ServerAddress {Quote(settings.ServerAddress)}")
                .Append(" -TunnelType L2tp")
                .Append($" -AuthenticationMethod {string.Join(',', settings.AuthMethods)}")
                .Append($" -EncryptionLevel {settings.EncryptionLevel}")
                // 원격 게이트웨이를 쓰면(-SplitTunneling 을 빼면) PC 전체 트래픽이 터널로 갑니다.
                // Windows 가 물리 NIC 메트릭까지 자동으로 올려버려 메트릭으로도 되돌릴 수 없습니다.
                // 터널이 인터넷으로 나가는 데 필요한 기본 경로는 EnsureTunnelRoutingAsync 가
                // 메트릭을 아주 크게 줘서 정적 경로로 따로 얹습니다.
                .Append(" -SplitTunneling")
                .Append(" -RememberCredential")
                .Append(" -Force");

            // PSK 는 커맨드라인에 올리지 않습니다. 커맨드라인은 같은 PC 의 다른 프로세스가 읽을 수 있고
            // 프로세스 생성 감사 로그(4688)에도 그대로 남습니다. 자식 프로세스의 환경 변수로만 넘깁니다.
            var environment = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(settings.PreSharedKey))
            {
                script.Append($" -L2tpPsk $env:{PreSharedKeyVariable}");
                environment[PreSharedKeyVariable] = settings.PreSharedKey;
            }

            var created = await RunPowerShellAsync(script.ToString(), environment);
            if (!created.Succeeded)
            {
                return new CommandResult(false, Shorten(created.Message));
            }

            return new CommandResult(true, "VPN 연결 항목을 준비했습니다.");
        }

        /// <summary>
        /// L2TP 서버마다 받아주는 PPP 조합이 달라서, 연결될 때까지 순서대로 시도합니다.
        /// 앞쪽이 더 안전한 조합입니다.
        /// <para>
        /// "암호화 없음"(NoEncryption, pbk DataEncryption=0) 은 넣지 않습니다. 그 항목으로는 IPsec
        /// Quick Mode 가 매번 실패(789, 약 1분 대기)했고, 같은 서버에 Optional 로 다시 만들면 바로 붙었습니다.
        /// 이 조합이 설정에 저장돼 있으면 실행마다 1분을 잃고 나서야 복구되는 셈이 됩니다.
        /// </para>
        /// </summary>
        private static readonly (string Encryption, string[] Auth, string Label)[] Profiles =
        [
            ("Optional", ["MSChapv2"], "MS-CHAPv2 / 암호화 선택"),
            ("Required", ["MSChapv2"], "MS-CHAPv2 / 암호화 필수"),
            ("Optional", ["MSChapv2", "Pap"], "MS-CHAPv2+PAP / 암호화 선택"),
        ];

        /// <summary>
        /// PPP 조합을 바꿔도 결과가 달라지지 않는 오류. 691 은 계정 거부,
        /// 나머지는 PPP 이전 단계(IPsec/L2TP)에서 나는 오류입니다.
        /// </summary>
        private static readonly string[] ProfileIndependentErrors = ["691", "789", "791", "800", "809", "835"];

        /// <summary>
        /// 조합을 바꿔가며 연결을 시도하고, <b>성공한 조합만</b> <paramref name="settings"/> 에 반영합니다.
        /// 성공한 조합은 저장되므로 다음부터는 한 번에 붙습니다.
        /// 실패한 조합이 설정에 남으면(예전 동작) 다음 실행마다 그 조합으로 먼저 실패하고 나서야 복구됩니다.
        /// </summary>
        public async Task<CommandResult> ApplyAndConnectAsync(
            VpnSettings settings, Action<string>? progress = null)
        {
            var attempts = new List<string>();
            var trial = settings.Clone();

            foreach (var (encryption, auth, label) in Profiles)
            {
                trial.EncryptionLevel = encryption;
                trial.AuthMethods = auth;

                progress?.Invoke($"시도 중: {label}");

                var applied = await ApplyAsync(trial);
                if (!applied.Succeeded)
                {
                    return applied;
                }

                var connected = await ConnectAsync(trial);

                if (connected.Succeeded)
                {
                    settings.EncryptionLevel = encryption;
                    settings.AuthMethods = auth;
                    return new CommandResult(true, $"연결됨 ({label})");
                }

                attempts.Add($"· {label} → {FirstLine(connected.Message)}");

                // 첫 조합에서 항목을 이미 새로 만들었으므로(PSK 갱신), 조합과 무관한 오류면 여기서 끝냅니다.
                // 789 같은 IPsec 오류는 한 번에 1분씩 걸려서 헛되이 반복하면 사용자가 창을 닫아 버립니다.
                if (ProfileIndependentErrors.Any(code => connected.Message.StartsWith(code, StringComparison.Ordinal)))
                {
                    return connected;
                }
            }

            return new CommandResult(
                false,
                "모든 PPP 조합으로 연결에 실패했습니다.\n" + string.Join('\n', attempts));
        }

        private static string FirstLine(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? text;

        /// <summary>
        /// 터널이 올라올 때 적용될 라우팅 설정을 전화번호부에 박아 둡니다.
        /// 항목을 다시 만들면 값이 초기화되므로 다이얼 직전에 매번 확인합니다.
        /// </summary>
        private static void EnsureTunnelRouting()
        {
            var route = RasPhonebook.BuildRouteBlob(IPAddress.Any, prefixLength: 0, metric: TunnelRouteMetric);

            RasPhonebook.SetEntryValues(VpnSettings.ConnectionName, new Dictionary<string, string>
            {
                // 원격 게이트웨이는 계속 끈 채로 둡니다. 켜면 Windows 가 물리 NIC 메트릭까지
                // 자동으로 올려버려 PC 전체 트래픽이 터널로 가고, 메트릭으로 되돌릴 수 없습니다.
                ["IpPrioritizeRemote"] = "0",

                // 터널에만 기본 경로를 얹습니다. 이게 없으면 인터페이스에 넥스트홉이 없어
                // 바인딩한 소켓이 전부 WSAENETUNREACH 로 실패합니다.
                // 메트릭이 커서 시스템 기본 경로 선택에는 지지 않습니다.
                ["NumRoutes"] = "1",
                ["Routes"] = route,
            });
        }

        /// <summary>
        /// 연결 항목을 지웁니다.
        /// <para>
        /// 끊은 직후에는 RAS 가 잠시 더 붙잡고 있어 Remove-VpnConnection 이
        /// "연결된 상태에서는 삭제할 수 없습니다" 를 냅니다. 몇 번 다시 시도해 보고,
        /// 그래도 안 되면 전화번호부에서 직접 지웁니다. rasdial 도 Get-VpnConnection 도
        /// 끊겼다고 하는데 cmdlet 만 계속 연결됐다고 우기는 상태가 실제로 생깁니다.
        /// </para>
        /// </summary>
        private static async Task<CommandResult> RemoveEntryAsync(string name)
        {
            var result = new CommandResult(false, string.Empty);

            for (var attempt = 0; attempt < RemoveAttempts; attempt++)
            {
                result = await RunPowerShellAsync($"Remove-VpnConnection -Name {Quote(name)} -Force");
                if (result.Succeeded)
                {
                    return result;
                }

                await Task.Delay(RemoveRetryDelay);
            }

            return RasPhonebook.RemoveEntry(name)
                ? new CommandResult(true, "전화번호부에서 직접 제거했습니다.")
                : new CommandResult(false, Shorten(result.Message));
        }

        /// <summary>설정에 저장된 계정으로 연결합니다.</summary>
        public Task<CommandResult> ConnectAsync(VpnSettings settings) =>
            Task.Run(() =>
            {
                // 항목을 다시 만들면 경로가 초기화되므로 다이얼 직전에 매번 확인합니다.
                EnsureTunnelRouting();

                var (code, message) = RasDialer.Dial(
                    VpnSettings.ConnectionName, settings.UserName, settings.Password);

                return code == 0
                    ? new CommandResult(true, $"'{VpnSettings.ConnectionName}' 에 연결되었습니다.")
                    : new CommandResult(false, RasErrorGuide.Explain(
                        code, message, !string.IsNullOrEmpty(settings.PreSharedKey)));
            });

        public async Task<CommandResult> DisconnectAsync(string name)
        {
            var result = await RunProcessAsync("rasdial.exe", [name, "/disconnect"]);
            return new CommandResult(result.Succeeded, Shorten(result.Message));
        }

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        private static string Shorten(string message)
        {
            var trimmed = message.Trim();
            if (trimmed.Length == 0)
            {
                return "알 수 없는 오류입니다.";
            }

            var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join('\n', lines.Take(4));
        }

        /// <summary>Add-VpnConnection 에 PSK 를 넘길 때 쓰는 환경 변수 이름. 스크립트는 <c>$env:</c> 로 읽습니다.</summary>
        private const string PreSharedKeyVariable = "BH_VPN_PSK";

        private static Task<CommandResult> RunPowerShellAsync(
            string script, IReadOnlyDictionary<string, string>? environment = null) =>
            RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], environment);

        private static async Task<CommandResult> RunProcessAsync(
            string fileName, string[] arguments, IReadOnlyDictionary<string, string>? environment = null)
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            // 비밀값은 인자가 아니라 자식 프로세스 환경으로만 전달합니다.
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    info.Environment[key] = value;
                }
            }

            try
            {
                using var process = Process.Start(info);
                if (process is null)
                {
                    return new CommandResult(false, $"{fileName} 을(를) 시작하지 못했습니다.");
                }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                using var timeout = new CancellationTokenSource(CommandTimeout);
                await process.WaitForExitAsync(timeout.Token);

                var output = (await stdout).Trim();
                var error = (await stderr).Trim();

                return process.ExitCode == 0
                    ? new CommandResult(true, output)
                    : new CommandResult(false, error.Length > 0 ? error : output);
            }
            catch (OperationCanceledException)
            {
                return new CommandResult(false, "명령이 시간 내에 끝나지 않았습니다.");
            }
            catch (Exception ex)
            {
                return new CommandResult(false, ex.Message);
            }
        }
    }
}
