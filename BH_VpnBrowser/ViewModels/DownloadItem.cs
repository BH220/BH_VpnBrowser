using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace BH_VpnBrowser.ViewModels
{
    /// <summary>다운로드 한 건의 진행 상태.</summary>
    public sealed class DownloadItem : ObservableObject
    {
        private readonly CoreWebView2DownloadOperation _operation;

        private string _fullPath;
        private double _progress;
        private string _stateText = "시작하는 중...";
        private bool _canCancel = true;
        private bool _isCompleted;
        private bool _isFailed;

        public DownloadItem(CoreWebView2DownloadOperation operation)
        {
            _operation = operation;
            _fullPath = operation.ResultFilePath;

            operation.BytesReceivedChanged += (_, _) => UpdateProgress();
            operation.StateChanged += (_, _) => UpdateState();

            UpdateProgress();
            UpdateState();
        }

        public string FileName => Path.GetFileName(_fullPath);

        public string FullPath
        {
            get => _fullPath;
            private set
            {
                if (Set(ref _fullPath, value))
                {
                    Raise(nameof(FileName));
                }
            }
        }

        public double Progress
        {
            get => _progress;
            private set => Set(ref _progress, value);
        }

        public string StateText
        {
            get => _stateText;
            private set => Set(ref _stateText, value);
        }

        public bool CanCancel
        {
            get => _canCancel;
            private set => Set(ref _canCancel, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            private set => Set(ref _isCompleted, value);
        }

        public bool IsFailed
        {
            get => _isFailed;
            private set => Set(ref _isFailed, value);
        }

        public void Cancel()
        {
            if (CanCancel)
            {
                _operation.Cancel();
            }
        }

        /// <summary>탐색기에서 파일을 선택한 상태로 폴더를 엽니다.</summary>
        public void ShowInFolder()
        {
            if (!File.Exists(FullPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{FullPath}\"")
            {
                UseShellExecute = true,
            });
        }

        public void OpenFile()
        {
            if (!File.Exists(FullPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(FullPath) { UseShellExecute = true });
        }

        private void UpdateProgress()
        {
            FullPath = _operation.ResultFilePath;

            var total = (long)(_operation.TotalBytesToReceive ?? 0UL);
            var received = (long)_operation.BytesReceived;

            Progress = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : 0;

            if (_operation.State == CoreWebView2DownloadState.InProgress)
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
                case CoreWebView2DownloadState.InProgress:
                    CanCancel = true;
                    IsCompleted = false;
                    IsFailed = false;
                    break;

                case CoreWebView2DownloadState.Completed:
                    CanCancel = false;
                    IsCompleted = true;
                    IsFailed = false;
                    Progress = 100;
                    var size = _operation.TotalBytesToReceive is { } total ? (long)total : _operation.BytesReceived;
                    StateText = $"완료 · {FormatBytes(size)}";
                    break;

                case CoreWebView2DownloadState.Interrupted:
                    CanCancel = false;
                    IsCompleted = false;
                    IsFailed = true;
                    StateText = _operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled
                        ? "취소됨"
                        : $"실패: {_operation.InterruptReason}";
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
