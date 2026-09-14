using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BH_VpnBrowser.ViewModels;
using BH_VpnBrowser.Views.Browser;

namespace BH_VpnBrowser.Views
{
    /// <summary>
    /// 탭 브라우저 창. 상태와 동작은 <see cref="MainViewModel"/> 에 있고,
    /// 여기에는 창 자체를 다루는 일(캡션, DWM 라운딩, 포커스)만 남겼습니다.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel, WebView2BrowserViewFactory browserFactory)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = viewModel;

            // 탭의 WebView2 컨트롤이 놓일 자리를 공장에 알려 줍니다.
            browserFactory.AttachHost(BrowserHost);

            Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);
        }

        // ================= 창 캡션 =================

        /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE</summary>
        private const int DwmCornerPreference = 33;

        /// <summary>DWMWCP_ROUND - 크롬과 동일한 Windows 11 기본 라운딩</summary>
        private const int DwmCornerRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Windows 11 이상에서만 반영되고, 그 이전 버전에서는 조용히 무시됩니다.
            var handle = new WindowInteropHelper(this).Handle;
            var preference = DwmCornerRound;
            _ = DwmSetWindowAttribute(handle, DwmCornerPreference, ref preference, sizeof(int));
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var maximized = WindowState == WindowState.Maximized;

            // WindowStyle=None 으로 최대화하면 리사이즈 테두리만큼 화면 밖으로 나가 잘립니다.
            var border = SystemParameters.WindowResizeBorderThickness;
            RootPanel.Margin = maximized
                ? new Thickness(border.Left, border.Top, border.Right, border.Bottom)
                : new Thickness(0);

            // 최대화 상태에서는 겹친 사각형(복원) 모양으로 바꿉니다.
            MaximizeGlyph.Data = Geometry.Parse(maximized
                ? "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z"
                : "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");

            MaximizeButton.ToolTip = maximized ? "이전 크기로 복원" : "최대화";
        }

        // ================= 포커스 (뷰만 아는 상태) =================

        /// <summary>주소창을 편집하는 동안에는 페이지 이동으로 주소가 덮어써지지 않게 ViewModel 에 알립니다.</summary>
        private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _viewModel.IsAddressEditing = true;
            AddressBox.SelectAll();
        }

        private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            _viewModel.IsAddressEditing = false;

        /// <summary>Ctrl+L: 주소창으로 포커스. 포커스는 뷰의 일이라 여기서 처리합니다. 나머지 단축키는 InputBindings 에 있습니다.</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control)
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
                e.Handled = true;
            }
        }
    }
}
