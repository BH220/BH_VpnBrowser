using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>
    /// 탭 하나. WebView2 인스턴스를 각자 가지되 CoreWebView2Environment 는 공유하므로
    /// 모든 탭이 동일한 프록시(터널) 설정을 따릅니다.
    /// </summary>
    public sealed class BrowserTab : ObservableObject
    {
        private string _title = "새 탭";
        private string _address = string.Empty;
        private bool _isSelected;
        private bool _isLoading;
        private bool _canGoBack;
        private bool _canGoForward;

        public BrowserTab(WebView2 view)
        {
            View = view;
            View.Visibility = Visibility.Collapsed;
        }

        public WebView2 View { get; }

        public string Title
        {
            get => _title;
            set => Set(ref _title, string.IsNullOrWhiteSpace(value) ? "새 탭" : value);
        }

        public string Address
        {
            get => _address;
            set => Set(ref _address, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (Set(ref _isSelected, value))
                {
                    View.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        public bool CanGoBack
        {
            get => _canGoBack;
            set => Set(ref _canGoBack, value);
        }

        public bool CanGoForward
        {
            get => _canGoForward;
            set => Set(ref _canGoForward, value);
        }
    }
}
