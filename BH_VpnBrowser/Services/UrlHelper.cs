using System.Text.RegularExpressions;

namespace BH_VpnBrowser.Services
{
    /// <summary>주소창 입력을 실제 이동 가능한 URI 로 정규화합니다.</summary>
    public static partial class UrlHelper
    {
        private const string SearchTemplate = "https://www.google.com/search?q={0}";

        public static Uri? Normalize(string? input)
        {
            var text = input?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) &&
                (absolute.Scheme == Uri.UriSchemeHttp ||
                 absolute.Scheme == Uri.UriSchemeHttps ||
                 absolute.Scheme == Uri.UriSchemeFile ||
                 absolute.Scheme == "about"))
            {
                return absolute;
            }

            // 공백이 없고 점이 있거나 localhost 형태면 도메인으로 간주.
            if (!text.Contains(' ') && (HostLikeRegex().IsMatch(text) || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                if (Uri.TryCreate("https://" + text, UriKind.Absolute, out var guessed))
                {
                    return guessed;
                }
            }

            return new Uri(string.Format(SearchTemplate, Uri.EscapeDataString(text)));
        }

        [GeneratedRegex(@"^[^\s/]+\.[^\s/]{2,}")]
        private static partial Regex HostLikeRegex();
    }
}
