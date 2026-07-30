; OhMyAgent AI 에이전트 — Windows 인스톨러 (Inno Setup 6)
;
; 직접 컴파일하지 말고 installer\build-installer.ps1 을 쓰세요.
; 그 스크립트가 publish → 바이너리 서명 → 이 스크립트 컴파일 → 인스톨러 서명 순서를 지킵니다.
; 순서가 중요한 이유: 서명은 파일 내용을 바꾸므로, 패키징 전에 바이너리를 서명해야
; 설치된 파일이 서명된 상태가 되고, 인스톨러 자체 서명은 컴파일 이후에만 가능합니다.
;
; 필수 정의(빌드 스크립트가 /D 로 주입):
;   AppVersion   앱 SemVer (예: 1.3.0)
;   SourceDir    dotnet publish 산출 폴더
;   OutputDir    인스톨러 .exe 를 놓을 폴더

#ifndef AppVersion
  #error AppVersion 이 정의되지 않았습니다 — build-installer.ps1 로 실행하세요.
#endif
#ifndef SourceDir
  #error SourceDir 이 정의되지 않았습니다 — build-installer.ps1 로 실행하세요.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName        "OhMyAgent"
#define AppDisplayName "OhMyAgent AI 에이전트"
#define AppPublisher   "KT"
#define AppExeName     "OhMyAgent.AiAgent.Client.exe"
; AppId 는 업그레이드 식별자입니다. 한 번 배포한 뒤에는 절대 바꾸지 마세요 —
; 바꾸면 Windows 가 다른 제품으로 인식해 기존 버전을 덮지 않고 나란히 설치합니다.
#define AppId          "{{8A5E3C71-4D2B-4F9A-9E17-6B0C2D8F5A34}"

[Setup]
AppId={#AppId}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright © {#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
; 앱 데이터가 전부 %APPDATA% 에 있고 설치 폴더에는 쓰지 않으므로 per-user 설치로 충분합니다.
; lowest → UAC 없이 %LOCALAPPDATA%\Programs\OhMyAgent 에 설치됩니다({autopf} 가 그렇게 해석됨).
; 전사 일괄 배포로 per-machine 이 필요해지면 이 값을 admin 으로 바꾸면 {autopf}=Program Files 가 됩니다.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=OhMyAgent-Setup-{#AppVersion}
SetupIconFile=..\OhMyAgent.AiAgent.Client\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppDisplayName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; self-contained 산출물이라 .NET 설치가 필요 없습니다. 대신 용량이 큽니다.
DiskSpanning=no
; 설치 중 앱이 떠 있으면 파일 교체가 실패하므로 종료를 요청합니다.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션"; Flags: unchecked

[Files]
; publish 폴더 전체를 담습니다. self-contained 라 .NET 런타임 파일이 함께 들어갑니다.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 무결성 매니페스트는 설치 경로에서 파생한 키로 %APPDATA% 에 저장되므로 제거 시 같이 지웁니다.
; 대화·설정·로그(%APPDATA%\OhMyAgent)는 남깁니다 — 재설치 시 이어서 쓰기 위함이고,
; 사용자 데이터를 말없이 지우지 않기 위함입니다.
Type: filesandordirs; Name: "{userappdata}\OhMyAgent.AiAgent.Client\integrity"

[Code]
{ 업그레이드 시 무결성 매니페스트를 무효화한다.

  BinaryIntegrityService 는 설치 폴더를 SHA-256 으로 스캔해 %APPDATA% 의 매니페스트와 대조한다.
  업그레이드하면 바이너리가 정당하게 바뀌는데 매니페스트는 구버전 그대로라, 그냥 두면
  트레이 → 무결성 검사가 "변조됨" 으로 뜬다. 설치 직후 매니페스트를 지워
  "매니페스트 없음"(= 기준 재생성 필요) 상태로 만드는 편이 오탐보다 정확하다. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  IntegrityDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    IntegrityDir := ExpandConstant('{userappdata}\OhMyAgent.AiAgent.Client\integrity');
    if DirExists(IntegrityDir) then
      DelTree(IntegrityDir, True, True, True);
  end;
end;
