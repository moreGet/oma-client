# 매니페스트 저장 위치 이전 (%APPDATA%) — 자기위조 방지

## 변경 파일
- `Services/BinaryIntegrityService.cs` — 경로 계산/키 파생/스캔 제외 로직 수정
- `Services/IBinaryIntegrityService.cs` — XML 주석 갱신 (시그니처 불변)

## 배경 / 문제
기존 `GetManifestPath`는 `Path.Combine(targetDirectory, "integrity.manifest.json")`을 반환했다.
즉 매니페스트가 검사 대상 폴더(설치 디렉토리)에 평문 저장되어, 변조자가 바이너리와
매니페스트를 함께 재생성하면 검증을 우회(self-forgery)할 수 있었다.
매니페스트를 검사 대상과 분리된 사용자 프로필 영역으로 옮겨 이를 차단한다.

## 새 경로 규칙
- 저장 루트: `Environment.GetFolderPath(SpecialFolder.ApplicationData)` 하위
  `OhMyAgent.AiAgent.Client/integrity/`
  → 예: `%APPDATA%\OhMyAgent.AiAgent.Client\integrity\<key>.manifest.json`
- 상수 추가: `AppDataFolderName`, `ManifestSubFolderName`, `KeyHashLength(32)`, `LabelPrefixMaxLength(24)`
- `GetManifestStorageRoot()` 헬퍼가 루트 경로 조립.

## 키 파생 방식 (`DeriveManifestKey`)
같은 앱이 서로 다른 폴더를 검사할 수 있으므로 대상별로 파일을 구분한다.
1. `Path.GetFullPath(targetDirectory)`로 절대경로 정규화 (실패 시 원문 fallback)
2. 후행 디렉토리 구분자(`\`, `/`) 제거
3. `ToLowerInvariant()` 소문자화 — 무결성 맵 비교가 OrdinalIgnoreCase이므로 키도 대소문자 무시
4. `SHA256.HashData(Encoding.UTF8.GetBytes(normalized))` → `Convert.ToHexString` → 소문자 → 앞 32자
5. 가독성 보조: 디렉토리명(`TryGetRootLabel`)을 `SanitizeLabel`로 영숫자/`-`/`_`만 남겨(최대 24자) `<label>_<hash>` 접두. 라벨 없으면 해시만.
- 파일명에 안전한 문자만 사용. 파일 확장자는 `.manifest.json`.

## GetManifestPath 동작 변화
- 시그니처 불변: `string GetManifestPath(string targetDirectory)` (VM/View 의존 — 그대로).
- 반환값만 `%APPDATA%` 기반 `<key>.manifest.json` 으로 변경.
- **부작용 없는 순수 경로 계산 유지** — 디렉토리 생성 안 함.
- 저장 디렉토리(`%APPDATA%\...\integrity`) 생성은 `SaveManifestAsync`의 원자적 저장 직전
  기존 `Directory.CreateDirectory(Path.GetDirectoryName(path))` 로직이 그대로 처리(이미 존재, 경로만 바뀜).
- `ManifestExists`, `LoadManifestAsync`, `VerifyAsync`, `GenerateBaselineAsync` 는 모두
  `GetManifestPath`를 경유하므로 새 위치 자동 반영(추가 수정 불필요).

## ExcludeManifestFile 처리
- 매니페스트가 대상 폴더 밖으로 이동하여 스캔 중 `Unexpected`로 잡힐 일이 없어졌다.
- 자기제외 로직(`EnumerateTargets`)은 제거하지 않고 **안전망으로 유지**:
  대상이 우연히 `%APPDATA%` 하위인 극단적 경우 대비. 옵션도 하위호환 위해 존속.
- `Path.GetFullPath` 호출을 예외 흡수형 `SafeGetFullPath`(null 반환)로 교체하여
  비정상 경로에서 스캔 전체가 중단되지 않도록 견고성 보강.

## RootLabel / 매니페스트 내부 식별
- 기존 `IntegrityManifest.RootLabel`(대상 디렉토리명)을 그대로 보존 — 원본 대상 표시 용도로 충분.
- 키 파생은 `RootLabel` 값과 독립적으로 `targetDirectory`에서 직접 계산하므로 라벨 변경이 키에 영향 없음.

## VM/View 영향
- 없음. `GetManifestPath` 시그니처/반환타입 불변. 인터페이스 메서드 시그니처 전부 동일.
- 서비스 레이어만 변경. ViewModel/View 파일 미수정.

## 마이그레이션 주의
- **기존에 대상 폴더에 저장된 매니페스트는 자동 이전되지 않는다.**
  업데이트 후 첫 `VerifyAsync`는 `%APPDATA%` 위치에서 매니페스트를 찾지 못해
  "매니페스트 없음 — 기준 생성을 먼저 실행하세요" 예외가 발생할 수 있다.
  → **기준(baseline) 재생성 필요.** 대상 폴더에 남은 구 `integrity.manifest.json`은
  더 이상 사용되지 않으므로 수동 정리 가능(검증 결과엔 영향 없음).

## 검증
- `dotnet build` 결과: 오류 0개, 경고 5개(기존 무관 경고). 컴파일 정상.
- 최종 빌드/QA는 QAReviewer 담당.
