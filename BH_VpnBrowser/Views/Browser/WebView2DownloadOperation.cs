using BH_VpnBrowser.Browser;
using Microsoft.Web.WebView2.Core;

namespace BH_VpnBrowser.Views.Browser
{
    /// <summary>WebView2 의 다운로드 객체를 <see cref="IDownloadOperation"/> 으로 감쌉니다.</summary>
    internal sealed class WebView2DownloadOperation : IDownloadOperation
    {
        private readonly CoreWebView2DownloadOperation _operation;

        public WebView2DownloadOperation(CoreWebView2DownloadOperation operation)
        {
            _operation = operation;
            operation.BytesReceivedChanged += (_, _) => ProgressChanged?.Invoke(this, EventArgs.Empty);
            operation.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        }

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
