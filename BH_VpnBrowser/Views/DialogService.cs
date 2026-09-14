using System.Windows;
using BH_VpnBrowser.Models;
using BH_VpnBrowser.Services;
using BH_VpnBrowser.ViewModels;

namespace BH_VpnBrowser.Views
{
    /// <summary>메시지 상자와 설정 창을 대신 띄워 줍니다. 소유자는 현재 주 창입니다.</summary>
    public sealed class DialogService(Func<VpnSettings, SettingsViewModel> createSettingsViewModel) : IDialogService
    {
        private static Window? Owner => Application.Current?.MainWindow;

        public void ShowWarning(string title, string message) => Show(title, message, MessageBoxImage.Warning);

        public void ShowError(string title, string message) => Show(title, message, MessageBoxImage.Error);

        public bool Confirm(string title, string message)
        {
            var answer = Owner is { } owner
                ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

            return answer == MessageBoxResult.Yes;
        }

        public VpnSettings? ShowSettings(VpnSettings current)
        {
            var viewModel = createSettingsViewModel(current);
            var window = new SettingsWindow(viewModel) { Owner = Owner };

            return window.ShowDialog() == true ? viewModel.Result : null;
        }

        private static void Show(string title, string message, MessageBoxImage image)
        {
            if (Owner is { } owner)
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
            }
            else
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, image);
            }
        }
    }
}
