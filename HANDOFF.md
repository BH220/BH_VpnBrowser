# 인계 메모 (2026-09-01)

원래 세션(`ae509f36-72a9-48d3-a257-785a86db134f`)이 계정 한도로 끊긴 뒤,
다른 세션에서 이어서 작업한 내용입니다. 여기서 이어가면 됩니다.

## 끝난 것: 앱 전용 터널링이 관리자 권한 없이 동작합니다

검증 결과(앱의 실제 코드 경로, 비관리자):

```
브라우저(SOCKS5) 출구 IP : 120.142.150.217   ← 터널
PC 전체 트래픽 출구 IP    : 112.219.104.18    ← 물리 NIC (변화 없음)
DNS 누수                 : 없음
```

### 원리

터널에 기본 경로(0.0.0.0/0)가 없으면 `IP_UNICAST_IF` 로 바인딩한 소켓이
넥스트홉을 못 찾아 전부 `WSAENETUNREACH` 로 실패합니다.
그래서 **메트릭 9000짜리 기본 경로를 터널에만** 얹습니다.
물리 NIC 의 기본 경로(메트릭 25)가 이기므로 PC 전체 트래픽은 그대로입니다.

권한 상승 없이 이걸 넣는 방법이 핵심인데, RAS 전화번호부(rasphone.pbk)의
`NumRoutes` / `Routes` 키에 직접 써 넣습니다. `Add-VpnConnectionRoute` cmdlet 이
쓰는 바로 그 자리인데, cmdlet 은 `0.0.0.0/0` 을 거부하므로 우회한 것입니다.
RAS 가 연결을 올릴 때 스스로 적용합니다.

`Routes` 블롭 형식(리틀엔디언):
`[메트릭 4바이트][주소 체계 4바이트][프리픽스 길이 4바이트][주소 24바이트]` → hex 72자

### 막다른 길 (다시 시도하지 말 것)

- **`IpPrioritizeRemote=1`(원격 게이트웨이 사용)은 메트릭으로 못 막습니다.**
  인터페이스 메트릭 9000 + 라우트 메트릭 9000 을 주고 물리 NIC 를 25 로 낮춰도
  Windows 는 계속 터널을 고릅니다. 그 옵션은 곧 "PC 전체 터널링"이라 이 앱엔 쓸 수 없습니다.
  (원래 세션이 이 방향으로 가려던 참이었습니다.)
- **pbk 를 PowerShell `Get-Content`/`Set-Content -Encoding Unicode` 로 고치면 안 됩니다.**
  pbk 는 **UTF-8(BOM 없음) + CRLF** 입니다. UTF-16 으로 저장하면 RAS 가 전화번호부 전체를
  못 읽어 **항목이 하나도 없는 것처럼** 동작합니다(오류도 안 납니다). 실제로 이 상태였습니다.
- **키를 섹션 머리 바로 뒤에 끼워 넣으면 안 됩니다.** `Encoding=` 이 섹션 첫 줄에서
  밀려나면 RAS 가 항목을 못 읽습니다. 반드시 섹션 끝에 붙입니다.

## 바뀐 파일

- `Services/RasPhonebook.cs` (신규) — pbk 를 인코딩 보존하며 편집, 라우트 블롭 생성
- `Services/VpnConnectionManager.cs`
  - 다이얼 직전 `EnsureTunnelRouting()` 으로 라우트를 pbk 에 주입
  - `ApplyAsync` 가 항목을 지우기 전에 연결을 끊고, 제거 실패를 무시하지 않도록 수정
    (연결 중이면 `Remove-VpnConnection` 이 실패해서 "이미 만들어진 연결입니다" 오류로 이어졌습니다)
- `Services/TunnelDnsResolver.cs` — DNS 서버 **동시 질의**로 변경
  (VPN 이 주는 1차 DNS `121.88.255.50` 이 자주 무응답이라, 순차 질의로는
   연결 직후 이름 해석이 통째로 실패했습니다)

## 실제 앱 검증 완료 (18:1x)

수정된 빌드(17:56:56)로 실행 중인 인스턴스를 실계정(bhjin220)에서 검사했습니다.

- 앱의 외부 연결 25개 전부 출발지 `192.168.0.254`(터널). 물리 NIC 로 나가는 소켓 0개
- WebView2 → `127.0.0.1:51025`(로컬 SOCKS5)로만 나감
- 같은 시각 PC 외부 IP `112.219.104.18`(물리 NIC, 직결)
- `mail.google.com` 이 Windows DNS 캐시에 없음 → 브라우저 이름 해석이 시스템 리졸버를 타지 않음

## 남은 것

- **VPN 항목의 PSK 가 테스트 계정(`vpntest`) 것일 수 있습니다.** 실계정으로 처음 연결하면
  한 번 실패한 뒤 `ApplyAndConnectAsync` 가 항목을 다시 만들어 스스로 복구됩니다.
- **`1.1.1.1` 은 이 VPN 에서 차단**돼 있습니다(TCP/UDP 모두). 테스트 대상에서 빼세요.
- 앱은 **단일 인스턴스**입니다. 이미 떠 있으면 새로 실행해도 즉시 종료(code 0)됩니다.

## 테스트 하네스

`%LOCALAPPDATA%\Temp\claude\c--git-BHS-Api\4db8e783-.../scratchpad/`
- `routecheck` — 항목 생성부터 판정까지 종단 검증 (`dotnet run`)
- `socksprobe` — 터널 DNS/TCP 도달성 + SOCKS5 상세 진단 (연결된 상태에서 실행)

이전 세션 하네스는 `...\C--git-BH-VpnBrowser-BH-VpnBrowser\ae509f36-...\scratchpad\` 에 있습니다
(`vpntest`, `routetest`, `socksvpn`).
