# 13. 매니페스트 HMAC-SHA256 서명 (변조 탐지 강화)

## 목적
`%APPDATA%\OhMyAgent.AiAgent.Client\integrity\<key>.manifest.json` 평문 매니페스트 파일 자체의
변조를 탐지하기 위해 HMAC-SHA256 서명을 부여한다.

## 수정 파일
- `Models/Integrity/IntegrityManifest.cs` — 서명 필드 3개 추가
- `Services/BinaryIntegrityService.cs` — 서명 생성/검증 로직, 로드 분류 리팩토링
- `Services/IBinaryIntegrityService.cs` — XML 주석에 서명 동작 명시 (시그니처 불변)

> csproj/패키지 추가 없음. DPAPI 미사용(아래 키 관리 참조).

## 1. 모델 확장 (`IntegrityManifest`)
| 필드 | JSON | 타입 | 비고 |
|------|------|------|------|
| `Signature` | `signature` | `string?` | HMAC-SHA256 결과 Base64 |
| `SignatureAlgorithm` | `signature_algorithm` | `string?` | `"HMACSHA256"` |
| `SignatureKeyVersion` | `signature_key_version` | `int?` | 현재 1, 키 로테이션 대비 |

세 필드 모두 nullable → `AgentJson.Options`의 `WhenWritingNull`로 canonical 직렬화 시 출력에서 자동 생략.

## 2. Canonical(서명 대상) 정의
`ComputeManifestSignature`:
- 매니페스트 사본을 만들되 `Signature`/`SignatureAlgorithm`/`SignatureKeyVersion`를 **null**로 둠
  → WhenWritingNull 정책상 직렬화 출력에 해당 키 자체가 등장하지 않음(결정성 핵심).
- `Entries`는 **RelativePath OrdinalIgnoreCase 정렬**(`SortEntries`)을 저장·검증 양쪽 동일 적용.
- 위 사본을 `AgentJson.Options`로 직렬화한 **UTF-8 바이트**에 HMAC-SHA256 → Base64.

저장 시점에도 `manifest.Entries`를 `SortEntries`로 정렬한 뒤 서명·저장하므로,
디스크 저장 순서 = 서명 계산 순서가 항상 일치한다.

## 3. 키 관리 / 위협모델 한계
### 키 파생 (`GetSigningKey`)
- 베이스: 앱 내장 비밀 3조각(`SecretA/B/C`, byte[] 상수) — 단순 문자열 스캔 방해 목적으로 분산.
- 베이스 재료 = SecretA‖SecretB‖SecretC‖keyVersion(솔트).
- 파생 = `HMACSHA256(key: 베이스재료, data: UTF8(MachineName+UserName 소문자))`.
  → 머신/유저 바인딩. 매니페스트만 다른 PC/계정으로 옮겨도 키가 달라 재검증/재서명이 어려움.
- 머신/유저 식별자 취득 실패 시 빈 문자열로 **폴백**(내장 비밀만으로도 1차 목표인
  파일 단독 변조 탐지는 유지). 예외는 Debug 로그.

### DPAPI 미도입 결정 근거
1차 목표("매니페스트 파일 단독 변조 탐지")는 내장 비밀 + 머신/유저 파생만으로 달성된다.
`ProtectedData`(DPAPI)는 추가 의존/플랫폼 결합·복잡도를 늘리지만 위 위협모델 한계를
근본적으로 해소하지 못하므로(아래) **과설계로 판단해 도입하지 않음**.

### 위협모델 한계 (명시)
- HMAC 키는 결국 바이너리에 내장된 비밀에서 파생된다.
- **동일 사용자 권한** 공격자가 바이너리를 리버스 엔지니어링해 비밀 + 파생 로직을 복원하면
  매니페스트를 임의로 위조(재서명) 가능.
- 따라서 본 서명은 기밀성이 아닌 **tamper-evidence(변조 탐지)** 를 제공한다:
  매니페스트 파일만 단독으로 손대는 공격을 탐지하는 데 유효.

## 4. 저장/로드 동작
### 저장 (`GenerateBaselineAsync` → `SaveManifestAsync`)
1. entries 수집 후 `SortEntries`로 정렬한 매니페스트 구성(서명 필드 null).
2. `ComputeManifestSignature`로 HMAC 계산.
3. `Signature`/`SignatureAlgorithm`/`SignatureKeyVersion` 채운 사본으로 교체.
4. 기존 원자적 저장(tmp 쓰기 → `File.Move(overwrite)`) 그대로 유지.

### 로드/검증 분기 표
로드는 `LoadManifestCoreAsync`가 4분류(`Absent`/`Corrupted`/`SignatureFailed`/`Valid`)로 판정.
서명 검증은 `VerifyManifestSignature`(서명 부재/Base64 손상 → false, 그 외 `CryptographicOperations.FixedTimeEquals` 상수시간 비교).

| 상태 | `LoadManifestAsync` 반환 | `VerifyAsync`(manifest=null 경로) |
|------|--------------------------|-----------------------------------|
| 파일 없음(Absent) | `null` | `AgentException("매니페스트 없음 — '기준 생성'을 먼저 실행하세요.")` |
| 손상(역직렬화 실패, Corrupted) | `null` + Debug 로그 | `AgentException("매니페스트 없음 — '기준 생성'을 먼저 실행하세요.")` |
| 서명 부재/불일치(SignatureFailed) | `null` + Debug 로그 | `AgentException("매니페스트 서명 검증 실패 — 변조 가능성. '기준 생성'으로 재생성하세요.")` |
| 정상(Valid) | 매니페스트 객체 | 정상 검증 진행 |

핵심: `VerifyAsync`는 `LoadManifestAsync`(null만 노출)에 의존하지 않고 `LoadManifestCoreAsync`를
직접 호출해 **부재/손상과 서명실패를 구분** → 서명실패가 "매니페스트 없음"으로 묻히지 않음.
호출자가 `manifest` 인자를 직접 넘긴 경우는 신뢰된 객체로 간주하여 서명 검증을 건너뜀(시그니처/동작 변화 없음).

## 5. 하위호환 / 마이그레이션
- **signature 부재(구버전) 매니페스트 = 검증 실패**로 간주.
  - `LoadManifestAsync` → null, `VerifyAsync` → "서명 검증 실패" 예외(변조와 동일 취급).
  - 사용자는 **'기준 생성'으로 재생성**해야 한다.
- JSON 스키마는 필드 추가만 했고 `SchemaVersion`은 1 유지(신규 필드는 nullable, 구 파서 무해).
- **마이그레이션 주의**: 기존에 생성된 무서명 매니페스트를 그대로 쓰던 환경은
  업데이트 직후 첫 검증에서 서명 실패 → 재생성 유도. 키가 머신/유저 바인딩이므로
  매니페스트는 PC/계정 간 이전 불가(이전 시 재생성 필요).

## 6. VM/View 영향
- `IBinaryIntegrityService` **public 시그니처 변경 없음**. 동작/예외 메시지만 추가·변경.
- VM/View 파일 미수정. 새 예외 메시지는 기존 `AgentException` 처리 경로에서 그대로 표시됨.

## 7. 컨벤션 준수
- `Task.Run` + `ConfigureAwait(false)`, `AgentJson.Options`, 원자적 저장, `AgentException`,
  `Debug.WriteLine`, nullable enable 모두 기존 관용구 유지.
- 추가 헬퍼: `ComputeManifestSignature`, `GetSigningKey`, `SafeMachineUserIdentity`,
  `VerifyManifestSignature`, `SortEntries`, `LoadManifestCoreAsync`(+`ManifestLoadStatus` enum).

## 8. 빌드
`dotnet build -c Debug` → 오류 0개(경고 7개는 기존). 최종 빌드는 오케스트레이터가 수행.
