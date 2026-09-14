# BH VpnBrowser

이 프로그램의 트래픽만 L2TP/IPsec VPN 으로 보내는 Windows 전용 브라우저(WPF + WebView2, .NET 8)입니다.
PC 의 다른 프로그램은 평소 네트워크를 그대로 씁니다. 관리자 권한이 필요 없습니다.

## 동작 방식

1. Windows VPN 항목(RAS)을 **원격 게이트웨이 사용 안 함**(split tunneling)으로 만들고 연결합니다.
   PC 의 기본 경로는 바뀌지 않습니다.
2. 터널 인터페이스에만 메트릭 9000 짜리 기본 경로를 얹습니다(전화번호부 `Routes`).
   물리 NIC 경로가 항상 이기므로 다른 프로그램은 영향이 없습니다.
3. 앱 안에서 127.0.0.1 로컬 SOCKS5 를 띄우고, 나가는 소켓을 `IP_UNICAST_IF` 로 VPN 어댑터에 묶습니다.
   DNS 도 VPN 이 준 서버에 직접 질의해 시스템 리졸버로 새지 않습니다.
4. WebView2 는 `--proxy-server=socks5://127.0.0.1:<포트>` 로만 나갑니다.
   터널이 준비되지 않으면 닫힌 포트를 프록시로 주어 통신을 차단합니다(fail-closed).

## 알아 두어야 할 한계

- **WebView2 런타임 자체의 연결은 프록시를 거치지 않습니다.** 런타임(Edge)의 브라우저 프로세스가
  Windows 로그인 계정 정보 조회 등으로 Microsoft 서비스에 직접 연결하는 것이 관찰됐습니다.
  페이지 내용(네트워크 서비스 프로세스)은 전부 SOCKS5 를 거치지만, 이 연결은 물리 NIC 로 나갑니다.
  완전히 막으려면 고정 버전 런타임을 설치 폴더에 두고 방화벽으로 그 실행 파일을 막아야 합니다.
- 로컬 SOCKS5 는 인증이 없습니다. 대신 접속한 프로세스가 이 앱 자신이나 그 자식(WebView2)일 때만 받아 줍니다.
- 설정은 `C:\ProgramData\BH Soft\BH VpnBrowser` 에 저장합니다. 비밀번호·PSK 는 DPAPI(현재 사용자)로 암호화하고,
  폴더 권한을 만든 사용자 + SYSTEM + Administrators 로 제한합니다. 한 PC 를 여러 Windows 계정이 쓰는 경우는
  지원하지 않습니다.
- VPN 항목 생성에 PowerShell `Add-VpnConnection` 을 씁니다. PSK 는 자식 프로세스 환경 변수로만 넘기고
  커맨드라인에는 올리지 않습니다.

## 빌드

Visual Studio 2022 또는 .NET 8 SDK.

```
dotnet build BH_VpnBrowser/BH_VpnBrowser.csproj -c Release
```

WebView2 Evergreen 런타임이 설치돼 있어야 실행됩니다.

## 구조

MVVM(CommunityToolkit.Mvvm) + Microsoft.Extensions.DependencyInjection.

- `DependencyInjection/ServiceRegistration.cs` — 의존성 등록 (한 곳)
- `ViewModels/` — 화면 상태와 명령. WebView2 타입을 참조하지 않음
- `Browser/` — ViewModel 이 보는 브라우저 추상화
- `Views/Browser/` — WebView2 어댑터
- `Services/` — VPN 연결(RAS), 전화번호부 편집, 터널(SOCKS5, DNS, 인터페이스 바인딩), 설정 저장
