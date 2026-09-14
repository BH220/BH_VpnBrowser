using BH_VpnBrowser.Browser;
using BH_VpnBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>탭을 소유하는 쪽(<see cref="MainViewModel"/>)이 탭의 선택/닫기 요청을 받는 통로.</summary>
    public interface IBrowserTabOwner
    {
        void SelectTab(BrowserTabViewModel tab);

        void CloseTab(BrowserTabViewModel tab);
    }

    /// <summary>
    /// 탭 하나. 페이지 상태(제목·주소·로딩·히스토리·배율)를 뷰에서 받아 바인딩 가능한 속성으로 내놓고,
    /// 뒤로/앞으로/새로고침 같은 조작을 명령으로 제공합니다.
    /// </summary>
    public sealed partial class BrowserTabViewModel : ObservableObject, IDisposable
    {
        private const string DefaultTitle = "새 탭";

        /// <summary>배율이 같은지 비교할 때 쓰는 여유값(부동소수 오차 흡수).</summary>
        private const double ZoomEpsilon = 0.001;

        /// <summary>크롬과 같은 배율 단계입니다.</summary>
        private static readonly double[] ZoomSteps =
            [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

        private readonly IBrowserTabOwner _owner;

        [ObservableProperty]
        private string _title = DefaultTitle;

        [ObservableProperty]
        private string _address = string.Empty;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StopCommand))]
        private bool _isLoading;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
        private bool _canGoBack;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
        private bool _canGoForward;

        [ObservableProperty]
        private double _zoomFactor = 1.0;

        public BrowserTabViewModel(IBrowserView view, IBrowserTabOwner owner)
        {
            View = view;
            _owner = owner;

            view.NavigationStarting += (_, uri) =>
            {
                IsLoading = true;
                Address = uri;
                NavigationStarted?.Invoke(this, uri);
            };

            view.SourceChanged += (_, _) => Address = view.Source;

            view.HistoryChanged += (_, _) => SyncHistory();

            view.DocumentTitleChanged += (_, _) =>
                Title = string.IsNullOrWhiteSpace(view.DocumentTitle) ? DefaultTitle : view.DocumentTitle;

            view.NavigationCompleted += (_, result) =>
            {
                IsLoading = false;
                SyncHistory();
                NavigationFinished?.Invoke(this, result);
            };

            // Ctrl+휠처럼 뷰가 스스로 바꾼 배율도 표시에 반영합니다.
            view.ZoomFactorChanged += (_, _) => ZoomFactor = view.ZoomFactor;

            view.CloseRequested += (_, _) => _owner.CloseTab(this);

            Address = view.Source;
        }

        public IBrowserView View { get; }

        /// <summary>이동을 시작했을 때(목적지 URI). 상태 표시줄용.</summary>
        public event EventHandler<string>? NavigationStarted;

        public event EventHandler<BrowserNavigationResult>? NavigationFinished;

        public bool IsZoomDefault => Math.Abs(ZoomFactor - 1.0) < ZoomEpsilon;

        public string ZoomText => $"{Math.Round(ZoomFactor * 100)}%";

        /// <summary>선택된 탭의 뷰만 화면에 보입니다.</summary>
        partial void OnIsSelectedChanged(bool value) => View.IsVisible = value;

        partial void OnZoomFactorChanged(double value)
        {
            if (Math.Abs(View.ZoomFactor - value) > ZoomEpsilon)
            {
                View.ZoomFactor = value;
            }

            OnPropertyChanged(nameof(IsZoomDefault));
            OnPropertyChanged(nameof(ZoomText));
        }

        /// <summary>주소창 입력을 정규화해 이동합니다. 검색어면 구글 검색으로 갑니다.</summary>
        public void Navigate(string? input)
        {
            var uri = UrlHelper.Normalize(input);
            if (uri is not null)
            {
                View.Navigate(uri.ToString());
            }
        }

        /// <summary>한 단계 확대(+1) 또는 축소(-1). 양 끝이면 그대로 둡니다.</summary>
        public void StepZoom(int direction)
        {
            var current = ZoomFactor;
            var index = direction > 0
                ? Array.FindIndex(ZoomSteps, step => step > current + ZoomEpsilon)
                : Array.FindLastIndex(ZoomSteps, step => step < current - ZoomEpsilon);

            if (index >= 0)
            {
                ZoomFactor = ZoomSteps[index];
            }
        }

        public void ResetZoom() => ZoomFactor = 1.0;

        public void Print() => View.ShowPrintDialog();

        [RelayCommand]
        private void Select() => _owner.SelectTab(this);

        [RelayCommand]
        private void Close() => _owner.CloseTab(this);

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private void GoBack() => View.GoBack();

        [RelayCommand(CanExecute = nameof(CanGoForward))]
        private void GoForward() => View.GoForward();

        /// <summary>일반 브라우저처럼 로딩 중에는 중단, 아니면 새로고침입니다.</summary>
        [RelayCommand]
        private void ReloadOrStop()
        {
            if (IsLoading)
            {
                View.Stop();
            }
            else
            {
                View.Reload();
            }
        }

        [RelayCommand(CanExecute = nameof(IsLoading))]
        private void Stop() => View.Stop();

        private void SyncHistory()
        {
            CanGoBack = View.CanGoBack;
            CanGoForward = View.CanGoForward;
        }

        public void Dispose() => View.Dispose();
    }
}
