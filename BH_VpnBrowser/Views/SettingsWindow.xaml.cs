using System.Windows;
using BH_VpnBrowser.ViewModels;

namespace BH_VpnBrowser.Views
{
    /// <summary>L2TP/IPsec 터널 설정 대화상자. 상태와 동작은 <see cref="SettingsViewModel"/> 에 있습니다.</summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // PasswordBox 는 보안상 Password 가 의존 속성이 아니어서 바인딩할 수 없습니다. 뷰에서 직접 옮겨 줍니다.
            PasswordBox.Password = viewModel.Password;
            PskBox.Password = viewModel.PreSharedKey;
            PasswordBox.PasswordChanged += (_, _) => viewModel.Password = PasswordBox.Password;
            PskBox.PasswordChanged += (_, _) => viewModel.PreSharedKey = PskBox.Password;

            viewModel.CloseRequested += (_, saved) => DialogResult = saved;
        }
    }
}
