using System.Diagnostics;
using System.IO;
using BH_VpnBrowser.Browser;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>다운로드 한 건의 진행 상태.</summary>
    public sealed partial class DownloadItemViewModel : ObservableObject
    {
        private readonly IDownloadOperation _operation;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FileName))]
        private string _fullPath;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _stateText = "시작하는 중...";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
        private bool _canCancel = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isFailed;

        public DownloadItemViewModel(IDownloadOperation operation)
        {
            _operation = operation;
            _fullPath = operation.ResultFilePath;

            operation.ProgressChanged += (_, _) => UpdateProgress();
            operation.StateChanged += (_, _) => UpdateState();

            UpdateProgress();
            UpdateState();
        }

        public string FileName => Path.GetFileName(FullPath);

        [RelayCommand(CanExecute = nameof(CanCancel))]
        private void Cancel() => _operation.Cancel();

        /// <summary>탐색기에서 파일을 선택한 상태로 폴더를 엽니다.</summary>
        [RelayCommand]
        private void ShowInFolder()
        {
            if (File.Exists(FullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{FullPath}\"") { UseShellExecute = true });
            }
        }

        [RelayCommand(CanExecute = nameof(IsCompleted))]
        private void Open()
        {
            if (File.Exists(FullPath))
            {
                Process.Start(new ProcessStartInfo(FullPath) { UseShellExecute = true });
            }
        }

        private void UpdateProgress()
        {
            FullPath = _operation.ResultFilePath;

            var total = _operation.TotalBytesToReceive ?? 0;
            var received = _operation.BytesReceived;

            Progress = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : 0;

            if (_operation.State == DownloadState.InProgress)
            {
                StateText = total > 0
                    ? $"{FormatBytes(received)} / {FormatBytes(total)}"
                    : FormatBytes(received);
            }
        }

        private void UpdateState()
        {
            switch (_operation.State)
            {
                case DownloadState.InProgress:
                    CanCancel = true;
                    IsCompleted = false;
                    IsFailed = false;
                    break;

                case DownloadState.Completed:
                    CanCancel = false;
                    IsCompleted = true;
                    IsFailed = false;
                    Progress = 100;
                    StateText = $"완료 · {FormatBytes(_operation.TotalBytesToReceive ?? _operation.BytesReceived)}";
                    break;

                case DownloadState.Interrupted:
                    CanCancel = false;
                    IsCompleted = false;
                    IsFailed = true;
                    StateText = _operation.WasCanceled ? "취소됨" : $"실패: {_operation.InterruptReason}";
                    break;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }
    }
}
