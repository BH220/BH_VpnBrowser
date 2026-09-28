using BH_VpnBrowser.Browser;
using BH_VpnBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace BH_VpnBrowser.Views.Browser
{
    /// <summary>WebView2 의 다운로드 객체를 <see cref="IDownloadOperation"/> 으로 감쌉니다.</summary>
    internal sealed class WebView2DownloadOperation : IDownloadOperation
    {
        private readonly CoreWebView2DownloadOperation _operation;
        private int _progressEvents;

        public WebView2DownloadOperation(CoreWebView2DownloadOperation operation)
        {
            _operation = operation;

            operation.BytesReceivedChanged += (_, _) =>
            {
                if (DiagnosticLog.IsEnabled && ++_progressEvents % 50 == 0)
                {
                    DiagnosticLog.Write("download", $"진행 {BytesReceived}/{TotalBytesToReceive}");
                }

                ProgressChanged?.Invoke(this, EventArgs.Empty);
            };

            operation.StateChanged += (_, _) =>
            {
                DiagnosticLog.Write("download", $"상태 {State} {InterruptReason} {BytesReceived}/{TotalBytesToReceive} → {ResultFilePath}");
                StateChanged?.Invoke(this, EventArgs.Empty);
            };
        }

        public string Uri => _operation.Uri;

        public string ResultFilePath => _operation.ResultFilePath;

        public long BytesReceived => (long)_operation.BytesReceived;

        public long? TotalBytesToReceive => _operation.TotalBytesToReceive is { } total ? (long)total : null;

        public DownloadState State => _operation.State switch
        {
            CoreWebView2DownloadState.InProgress => DownloadState.InProgress,
            CoreWebView2DownloadState.Completed => DownloadState.Completed,
            _ => DownloadState.Interrupted,
        };

        public bool WasCanceled => _operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;

        public string InterruptReason =>
            _operation.State == CoreWebView2DownloadState.Interrupted ? _operation.InterruptReason.ToString() : string.Empty;

        public event EventHandler? ProgressChanged;

        public event EventHandler? StateChanged;

        public void Cancel() => _operation.Cancel();
    }
}
