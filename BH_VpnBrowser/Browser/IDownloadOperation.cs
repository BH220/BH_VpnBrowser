namespace BH_VpnBrowser.Browser
{
    public enum DownloadState
    {
        InProgress,
        Completed,
        Interrupted,
    }

    /// <summary>
    /// 진행 중인 다운로드 한 건. WebView2 의 다운로드 객체를 ViewModel 이 직접 다루지 않도록 감쌉니다.
    /// </summary>
    public interface IDownloadOperation
    {
        /// <summary>저장될(된) 파일의 전체 경로. 진행 중에 바뀔 수 있습니다.</summary>
        string ResultFilePath { get; }

        long BytesReceived { get; }

        /// <summary>서버가 크기를 알려주지 않으면 null.</summary>
        long? TotalBytesToReceive { get; }

        DownloadState State { get; }

        /// <summary>중단 사유가 사용자의 취소인지.</summary>
        bool WasCanceled { get; }

        /// <summary>중단 사유 설명. 중단되지 않았으면 빈 문자열.</summary>
        string InterruptReason { get; }

        event EventHandler? ProgressChanged;

        event EventHandler? StateChanged;

        void Cancel();
    }
}
