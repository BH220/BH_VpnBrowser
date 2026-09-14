namespace BH_VpnBrowser.Services
{
    /// <summary>프로세스 수준 동작. ViewModel 이 <c>Application</c> 을 직접 만지지 않게 합니다.</summary>
    public interface IAppLifetime
    {
        /// <summary>같은 실행 파일을 새로 띄우고 지금 인스턴스를 끝냅니다.</summary>
        void Restart();

        void Shutdown();
    }
}
