using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BH_VpnBrowser.Models;

namespace BH_VpnBrowser.Services
{
    public sealed record VpnConnectionInfo(string Name, string ServerAddress, string TunnelType, bool SplitTunneling)
    {
        public override string ToString() =>
            string.IsNullOrEmpty(ServerAddress) ? Name : $"{Name}  ({ServerAddress})";
    }

    public sealed record CommandResult(bool Succeeded, string Message);

    /// <summary>
    /// Windows 의 VPN 연결(RAS 전화번호부)을 다룹니다.
    /// <para>
    /// 항목 생성/삭제는 PowerShell cmdlet 으로, 실제 다이얼은 RasDial API 로 합니다.
    /// 비밀번호가 커맨드라인에 노출되지 않게 하기 위해서입니다.
    /// </para>
    /// </summary>
    public static class VpnConnectionManager
    {
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// 터널에 얹는 기본 경로의 메트릭. 물리 NIC 의 기본 경로(보통 25 이하)보다 훨씬 커야
        /// PC 전체 트래픽이 계속 물리 NIC 로 나갑니다.
        /// </summary>
        private const int TunnelRouteMetric = 9000;

        public static async Task<IReadOnlyList<VpnConnectionInfo>> ListAsync()
        {
            var result = await RunPowerShellAsync(
                "Get-VpnConnection -AllUserConnection:$false -ErrorAction SilentlyContinue | " +
                "Select-Object Name,ServerAddress,TunnelType,SplitTunneling | ConvertTo-Json -Compress");

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
            SplitTunneling: element.TryGetProperty("SplitTunneling", out var split) && split.ValueKind == JsonValueKind.True);

        /// <summary>
        /// 설정값으로 Windows VPN 연결 항목을 만들거나 갱신하고, 자격 증명까지 저장합니다.
        /// 이미 있으면 서버 주소/PSK 를 덮어씁니다.
        /// </summary>
        public static async Task<CommandResult> ApplyAsync(VpnSettings settings)
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

                var removed = await RunPowerShellAsync($"Remove-VpnConnection -Name {Quote(name)} -Force");
                if (!removed.Succeeded)
                {
                    return new CommandResult(false, Shorten(removed.Message));
                }
            }

            var script = new StringBuilder()
                .Append("Add-VpnConnection")
                .Append($" -Name {Quote(name)}")
                .Append($" -ServerAddress {Quote(settings.ServerAddress)}")
                .Append(" -TunnelType L2tp")
                .Append($" -AuthenticationMethod {string.Join(',', settings.AuthMethods)}")
                .Append($" -EncryptionLevel {settings.EncryptionLevel}")
                // 원격 게이트웨이 사용(-SplitTunneling 을 빼는 것)은 PC 전체 트래픽을 터널로
                // 보내버리고, 메트릭으로도 되돌릴 수 없습니다. 터널이 인터넷으로 나가는 데 필요한
                // 기본 경로는 EnsureTunnelRouting 이 메트릭을 크게 줘서 따로 얹습니다.
                .Append(" -SplitTunneling")
                .Append(" -RememberCredential")
                .Append(" -Force");

            if (!string.IsNullOrEmpty(settings.PreSharedKey))
            {
                script.Append($" -L2tpPsk {Quote(settings.PreSharedKey)}");
            }

            var created = await RunPowerShellAsync(script.ToString());
            if (!created.Succeeded)
            {
                return new CommandResult(false, Shorten(created.Message));
            }

            return new CommandResult(true, "VPN 연결 항목을 준비했습니다.");
        }

        /// <summary>
        /// L2TP 서버마다 받아주는 PPP 조합이 달라서, 연결될 때까지 순서대로 시도합니다.
        /// 앞쪽이 더 안전한 조합입니다.
        /// </summary>
        private static readonly (string Encryption, string[] Auth, string Label)[] Profiles =
        [
            ("Optional", ["MSChapv2"], "MS-CHAPv2 / 암호화 선택"),
            ("Required", ["MSChapv2"], "MS-CHAPv2 / 암호화 필수"),
            ("Optional", ["MSChapv2", "Pap"], "MS-CHAPv2+PAP / 암호화 선택"),
            ("NoEncryption", ["MSChapv2", "Pap"], "MS-CHAPv2+PAP / 암호화 없음"),
        ];

        /// <summary>
        /// 조합을 바꿔가며 연결을 시도하고, 성공한 조합을 <paramref name="settings"/> 에 반영합니다.
        /// 성공한 조합은 저장되므로 다음부터는 한 번에 붙습니다.
        /// </summary>
        public static async Task<CommandResult> ApplyAndConnectAsync(
            VpnSettings settings, Action<string>? progress = null)
        {
            var attempts = new List<string>();

            foreach (var (encryption, auth, label) in Profiles)
            {
                settings.EncryptionLevel = encryption;
                settings.AuthMethods = auth;

                progress?.Invoke($"시도 중: {label}");

                var applied = await ApplyAsync(settings);
                if (!applied.Succeeded)
                {
                    return applied;
                }

                var connected = await ConnectAsync(settings);

                if (connected.Succeeded)
                {
                    return new CommandResult(true, $"연결됨 ({label})");
                }

                attempts.Add($"· {label} → {FirstLine(connected.Message)}");

                // 자격 증명이 거부된 것이면 조합을 바꿔도 소용없습니다.
                if (connected.Message.StartsWith("691", StringComparison.Ordinal))
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
                // 원격 게이트웨이는 계속 끈 채로 둡니다(켜면 PC 전체가 터널로 갑니다).
                ["IpPrioritizeRemote"] = "0",

                // 터널에만 기본 경로를 얹습니다. 이게 없으면 인터페이스에 넥스트홉이 없어
                // 바인딩한 소켓이 전부 WSAENETUNREACH 로 실패합니다.
                // 메트릭이 커서 시스템 기본 경로 선택에는 지지 않습니다.
                ["NumRoutes"] = "1",
                ["Routes"] = route,
            });
        }

        /// <summary>설정에 저장된 계정으로 연결합니다.</summary>
        public static Task<CommandResult> ConnectAsync(VpnSettings settings) =>
            Task.Run(() =>
            {
                EnsureTunnelRouting();

                var (code, message) = RasDialer.Dial(
                    VpnSettings.ConnectionName, settings.UserName, settings.Password);

                return code == 0
                    ? new CommandResult(true, $"'{VpnSettings.ConnectionName}' 에 연결되었습니다.")
                    : new CommandResult(false, RasErrorGuide.Explain(
                        code, message, !string.IsNullOrEmpty(settings.PreSharedKey)));
            });

        public static async Task<CommandResult> DisconnectAsync(string name)
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

        private static Task<CommandResult> RunPowerShellAsync(string script) =>
            RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);

        private static async Task<CommandResult> RunProcessAsync(string fileName, string[] arguments)
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
