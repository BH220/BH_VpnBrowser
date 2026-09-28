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

            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        /// <summary>WM_GETMINMAXINFO</summary>
        private const int WmGetMinMaxInfo = 0x0024;

        private const uint MonitorDefaultToNearest = 2;

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmGetMinMaxInfo)
            {
                ConstrainMaximizedBoundsToWorkArea(hwnd, lParam);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// WindowStyle=None 창을 최대화하면 Windows 가 모니터 전체 크기에 보이지 않는 테두리까지 더해 배치해서
        /// 작업 표시줄 위로 창(과 DWM 그림자)이 걸칩니다. 최대화 크기와 위치를 모니터의 작업 영역에 맞춥니다.
        /// 다중 모니터를 위해 위치는 해당 모니터 기준 상대 좌표로 줍니다.
        /// </summary>
        private static void ConstrainMaximizedBoundsToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            limits.MaxSize.X = info.Work.Right - info.Work.Left;
            limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            Marshal.StructureToPtr(limits, lParam, fDeleteOld: false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>MINMAXINFO</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        /// <summary>MONITORINFO</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var maximized = WindowState == WindowState.Maximized;

            // 최대화 크기는 WM_GETMINMAXINFO 에서 작업 영역에 맞추므로 여백 보정은 필요 없습니다.
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
