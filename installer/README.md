# Windows 인스톨러

`OhMyAgent.AiAgent.Client`(WPF 데스크톱 클라이언트)를 Inno Setup 기반 설치 파일로 묶는다.
헤드리스 호스트(`OhMyAgent.AiAgent.Host`)는 대상이 아니다 — 그쪽은 단일 실행 파일 배포이며
[`docs/headless-deployment.md`](../docs/headless-deployment.md)를 따른다.

## 준비물

| | 설치 |
|---|---|
| .NET 10 SDK | 이미 있음 (`dotnet --list-sdks` 로 확인) |
| Inno Setup 6 | `winget install --id JRSoftware.InnoSetup` |
| 코드 서명 인증서 | 선택 — 없어도 인스톨러는 만들어진다([서명](#서명) 참조) |

> Windows SDK(`signtool`)는 **필수가 아니다.** 없으면 빌드 스크립트가 PowerShell 내장
> `Set-AuthenticodeSignature` 로 자동 대체한다.

## 빌드

```powershell
# 서명 없이 (파이프라인 점검용)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

# 서명 포함
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 `
    -CertThumbprint <지문> -TimestampUrl http://timestamp.digicert.com
```

산출물: `artifacts\OhMyAgent-Setup-<버전>.exe` (약 55MB). `artifacts/` 는 gitignore 대상이다.

주요 옵션:

| 옵션 | 용도 |
|---|---|
| `-SkipPublish` | 기존 publish 폴더 재사용. `.iss` 만 고칠 때 publish(수 분)를 건너뛴다 |
| `-Runtime` | 기본 `win-x64`. arm64 배포 시 `win-arm64` |
| `-CertThumbprint` | `Cert:\CurrentUser\My` 의 코드 서명 인증서 지문. 생략하면 서명 생략 |
| `-TimestampUrl` | RFC 3161 TSA. 폐쇄망이면 사내 TSA 주소로 교체 |

## 왜 이 순서인가

```
publish → 우리 바이너리 서명 → ISCC 컴파일 → 인스톨러 서명
```

서명은 파일 내용을 바꾼다. 그래서 **패키징 전에** 바이너리를 서명해야 설치된 파일이 서명된 상태가 되고,
인스톨러 자체 서명은 **컴파일 이후에만** 가능하다. 순서를 뒤집으면 "겉은 서명됐는데 속은 안 된"
인스톨러가 나온다.

서명 대상은 **우리가 만든 3개뿐**이다 — `OhMyAgent.AiAgent.Client.exe` / `.Client.dll` / `.Core.dll`.
서드파티·.NET 런타임 어셈블리는 각 배포사가 이미 서명했으므로 재서명하면 원래 서명이 깨진다.

## self-contained 인 이유

클라이언트 `.csproj` 에는 `RuntimeIdentifier` 가 없어 기본이 **framework-dependent** 다.
.NET 10 **Desktop** 런타임이 없는 PC에서 그대로 실행하면 우리 코드가 한 줄도 돌기 전에
`You must install or update .NET` 대화상자만 뜨고 끝난다. 빌드 스크립트가
`--self-contained` 로 publish 해 런타임을 동봉하므로 대상 PC에 .NET 설치가 필요 없다.
대가는 용량(publish ~190MB → 압축 후 설치본 ~55MB)이다.

## 설치 형태

- **per-user 설치** (`PrivilegesRequired=lowest`) → `%LOCALAPPDATA%\Programs\OhMyAgent`, **관리자 권한 불필요**
- 앱 데이터는 전부 `%APPDATA%\OhMyAgent`(설정·세션·프로젝트·로그)에 있고 설치 폴더에는 쓰지 않으므로
  per-user 로 충분하다
- 전사 일괄 배포(Intune/GPO)로 per-machine 이 필요해지면 `.iss` 의 `PrivilegesRequired` 를 `admin` 으로
  바꾸면 `{autopf}` 가 `Program Files` 로 해석된다

무인 설치/제거:
```powershell
OhMyAgent-Setup-1.3.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
"$env:LOCALAPPDATA\Programs\OhMyAgent\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

## 무결성 매니페스트 처리

`BinaryIntegrityService` 는 설치 폴더를 SHA-256 으로 스캔해 `%APPDATA%\OhMyAgent.AiAgent.Client\integrity`
의 매니페스트와 대조한다. 업그레이드하면 바이너리가 **정당하게** 바뀌는데 매니페스트는 구버전 그대로라,
그냥 두면 트레이 → 무결성 검사가 "변조됨" 으로 뜬다.

그래서 `.iss` 의 `CurStepChanged(ssPostInstall)` 가 설치 직후 매니페스트를 지운다.
검사는 "매니페스트 없음"(= 기준 재생성 필요) 상태가 되며, 이는 오탐보다 정확한 표현이다.
설치 후 트레이 → 무결성 검사에서 **기준 생성**을 한 번 눌러 새 기준을 잡으면 된다.

제거 시에는 매니페스트만 지우고 `%APPDATA%\OhMyAgent`(대화·설정·로그)는 **남긴다** — 재설치 시
이어 쓰기 위함이고, 사용자 데이터를 말없이 지우지 않기 위함이다.

## 아이콘

`installer\New-AppIcon.ps1` 이 `OhMyAgent.AiAgent.Client\Resources\app.ico` 를 생성한다.
도안은 앱이 트레이 아이콘을 그리는 코드(`App.xaml.cs` `CreateAppIcon()`)와 동일하다 —
라운드 사각형 + 보라→블루 그라데이션 + 화이트 4포인트 스파클. 16·24·32·48·64·128·256px 를
32bpp DIB 로 담는다.

**트레이 아이콘 도안을 바꾸면 이 스크립트도 같이 고쳐야** 트레이/창/바로가기/인스톨러가 어긋나지 않는다.
`.ico` 는 커밋되어 있으므로 도안을 안 바꾸면 다시 실행할 필요는 없다.

## 서명

인증서 없이도 인스톨러는 만들어진다. 다만 두 가지가 따라온다:

- 브라우저로 내려받으면 SmartScreen 경고 (사내 파일서버·Intune 배포는 MOTW 가 안 붙어 무관)
- 앱의 무결성 검사 화면이 바이너리를 "서명 없음" 으로 표시

무료 경로는 **사내 AD CS 코드 서명 인증서**다. 도메인 가입 PC 는 사내 루트 CA 를 이미 신뢰하므로
별도 배포 없이 검증을 통과한다. IT 요청 시 **Code Signing 용도(EKU `1.3.6.1.5.5.7.3.3`)** 임을 명시할 것.

개발 중 파이프라인 점검용 자체 서명 인증서:

```powershell
$c = New-SelfSignedCertificate -Type CodeSigningCert `
     -Subject 'CN=OhMyAgent Client (DEV), O=KT, C=KR' `
     -KeyAlgorithm RSA -KeyLength 3072 `
     -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
$c.Thumbprint
```

자체 서명으로 서명하면 서명 자체는 정상 삽입되지만 `Get-AuthenticodeSignature` 는 `UnknownError`
(신뢰되지 않는 루트)를 보고한다. 빌드 스크립트는 **서명 유무와 신뢰 여부를 분리 검증**하므로
이 경우 실패하지 않고 안내 문구만 남긴다.

> **타임스탬프(`-TimestampUrl`)를 빼지 말 것.** 서명 시점에 인증서가 유효했음이 기록되어,
> 인증서 만료 후에도 서명이 계속 유효하다. 없으면 인증서 만료일에 배포된 모든 설치본이 한꺼번에 무효가 된다.

> **`.pfx`/`.p12` 는 저장소에 커밋 금지** (gitignore 처리됨). 개인키가 유출되면 누구나 우리 이름으로
> 서명할 수 있다. 배포용 공개키 `.cer` 은 무해하다.
