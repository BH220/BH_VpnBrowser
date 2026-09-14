using Microsoft.Win32;

namespace BH_VpnBrowser.Services
{
    /// <summary>
    /// rasdial 오류 코드를 실제로 무엇을 고쳐야 하는지로 번역합니다.
    /// 원문 메시지만으로는 무엇을 해야 할지 알기 어려운 코드들이 있어서 따로 둡니다.
    /// </summary>
    public static class RasErrorGuide
    {
        private const string PolicyAgentKey = @"SYSTEM\CurrentControlSet\Services\PolicyAgent";
        private const string NatTraversalValue = "AssumeUDPEncapsulationContextOnSendRule";

        /// <summary>
        /// 클라이언트와 서버가 모두 NAT 뒤에 있을 때 L2TP/IPsec 을 허용하는 값(2)이 설정돼 있는지.
        /// 설정돼 있지 않으면 오류 789 의 흔한 원인입니다.
        /// </summary>
        public static bool IsNatTraversalEnabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(PolicyAgentKey);
                return key?.GetValue(NatTraversalValue) is int value && value == 2;
            }
            catch (Exception)
            {
                // 읽기 권한이 없으면 판단하지 않고 통과시킵니다.
                return true;
            }
        }

        /// <summary>레지스트리 값을 켜는 명령. 관리자 권한과 재부팅이 필요합니다.</summary>
        public static string NatTraversalCommand =>
            $@"New-ItemProperty -Path 'HKLM:\{PolicyAgentKey}' -Name '{NatTraversalValue}' -Value 2 -PropertyType DWord -Force";

        /// <summary>RAS 오류 코드에 대응 방법을 덧붙입니다. 모르는 코드면 원문을 그대로 돌려줍니다.</summary>
        public static string Explain(uint code, string rasMessage, bool hasPreSharedKey)
        {
            var guidance = code switch
            {
                789 => BuildIpsecGuidance(hasPreSharedKey),
                691 => "691: 사용자 이름 또는 비밀번호가 거부되었습니다. 계정을 확인해 주세요.",
                809 => "809: 서버에 닿지 못했습니다. NAT-T 설정이 필요할 수 있습니다.\n" + NatTraversalHint(),
                800 => "800: 터널을 만들지 못했습니다. 서버 주소와 방화벽(UDP 500/1701/4500)을 확인해 주세요.",
                628 => "628: 서버가 PPP 협상 단계에서 연결을 끊었습니다. " +
                       "계정이 VPN 사용 허용 상태인지, 인증 방식이 맞는지 확인해 주세요.",
                835 => "835: 인증서 인증에 실패했습니다. IPsec 사전 공유 키를 입력해 주세요.",
                _ => null,
            };

            return guidance is null ? $"{code}: {rasMessage}" : $"{guidance}\n\n(원문: {rasMessage})";
        }

        private static string BuildIpsecGuidance(bool hasPreSharedKey)
        {
            var reasons = new List<string> { "789: IPsec 협상에 실패했습니다." };

            if (!hasPreSharedKey)
            {
                reasons.Add(
                    "· 사전 공유 키가 비어 있습니다. 키를 넣지 않으면 Windows 가 인증서 인증을 시도하다 실패합니다. " +
                    "VPN 서버(공유기) 설정에서 Pre-shared Key 를 확인해 입력해 주세요.");
            }

            if (!IsNatTraversalEnabled())
            {
                reasons.Add("· " + NatTraversalHint());
            }

            return string.Join('\n', reasons);
        }

        private static string NatTraversalHint() =>
            "이 PC 와 서버가 모두 공유기(NAT) 뒤에 있으면 Windows 가 L2TP 를 기본적으로 거부합니다.\n" +
            "  관리자 PowerShell 에서 아래를 실행하고 재부팅해 주세요:\n" +
            $"  {NatTraversalCommand}";

        /// <summary>"Remote Access error 789 - ..." 또는 "오류 789" 형태에서 번호를 뽑습니다.</summary>
        private static int ExtractErrorCode(string output)
        {
            foreach (var token in output.Split([' ', '\t', '\r', '\n', '-', ':'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length is 3 && int.TryParse(token, out var code) && code >= 600)
                {
                    return code;
                }
            }

            return 0;
        }

        private static string FirstLine(string text) =>
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("오류"))
            ?? text.Trim();
    }
}
