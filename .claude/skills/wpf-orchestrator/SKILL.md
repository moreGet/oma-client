---
name: wpf-orchestrator
description: >
  OhMyAgent.AiAgent.Client WPF 앱의 모든 기능 구현을 조율하는 메인 오케스트레이터.
  새 기능 추가, UI 컴포넌트 생성, MVVM 리팩토링, 서비스 레이어 구현, WPF/XAML 작업,
  ViewModel 구현, 화면 개발, 다시 실행, 재실행, 업데이트, 수정, 보완 요청 시 반드시 이 스킬을 사용할 것.
  단순 질문은 직접 응답 가능하나, 코드 생성이 포함된 작업은 항상 이 스킬을 통해 실행한다.
---

# WPF 기능 구현 오케스트레이터

## 실행 모드

**하이브리드:**
- Phase 2 (서비스 + ViewModel): 팬아웃 — 서브 에이전트 병렬 실행
- Phase 3 (UI): 단일 서브 에이전트
- Phase 4 (QA): 단일 서브 에이전트

모든 Agent 호출에 `model: "opus"` 명시.

## Phase 0: 컨텍스트 확인

`_workspace/` 디렉토리 존재 여부를 확인하여 실행 모드를 결정한다:

- `_workspace/` **없음** → 초기 실행 (Phase 1부터)
- `_workspace/` **있음** + 사용자가 부분 수정 요청 → 해당 Phase의 에이전트만 재호출
- `_workspace/` **있음** + 새 기능 요청 → 기존 `_workspace/`를 `_workspace_prev/`로 이동 후 새 실행

## Phase 1: Architect (설계)

**실행 모드:** 서브 에이전트 (단일, 순차)

Architect 에이전트를 호출한다:
```
에이전트 파일: .claude/agents/architect.md
스킬: wpf-architect
입력: 사용자 요청 + 현재 프로젝트 파일 목록
출력: _workspace/01_architect_spec.md
```

Architect 완료 후 `_workspace/01_architect_spec.md` 존재를 확인한다. 없으면 재시도 1회.

## Phase 2: ServiceEngineer + ViewModelEngineer (병렬 구현)

**실행 모드:** 팬아웃 — 서브 에이전트 2개 병렬 실행 (`run_in_background: true`)

두 에이전트를 동시에 호출한다:

```
에이전트 A: .claude/agents/service-engineer.md
스킬: wpf-service
입력: _workspace/01_architect_spec.md
출력: Models/, Services/ 파일들 + _workspace/02_service_summary.md

에이전트 B: .claude/agents/viewmodel-engineer.md
스킬: wpf-viewmodel
입력: _workspace/01_architect_spec.md
출력: ViewModels/ 파일들 + _workspace/03_viewmodel_summary.md
```

두 에이전트 모두 완료 후 Phase 3으로 진행한다.

## Phase 3: UIDesigner (뷰 구현)

**실행 모드:** 서브 에이전트 (단일, 순차)

```
에이전트: .claude/agents/ui-designer.md
스킬: wpf-ui
입력: _workspace/01_architect_spec.md + _workspace/03_viewmodel_summary.md
출력: Views/, Controls/ 파일들 + _workspace/04_ui_summary.md
```

## Phase 4: QAReviewer (검증)

**실행 모드:** 서브 에이전트 (단일, 순차)

```
에이전트: .claude/agents/qa-reviewer.md
스킬: wpf-qa
입력: _workspace/ 전체 + 생성된 소스 파일들
출력: _workspace/05_qa_report.md + 직접 수정 (필요 시)
```

## Phase 5: 결과 보고

사용자에게 다음을 보고한다:
1. 생성된 파일 목록 (경로 포함)
2. `_workspace/05_qa_report.md` 요약 (PASS/FAIL + 주요 발견 사항)
3. 다음 단계 제안 (패키지 추가, DI 등록 등)
4. 개선 피드백 요청

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 출력 파일 없음 | 1회 재시도, 재실패 시 해당 Phase 결과 없이 진행 + 보고 |
| Phase 2 한쪽만 실패 | 성공한 결과로 Phase 3 진행, 실패 에이전트 재호출 후 QA에서 통합 |
| QA FAIL | QAReviewer가 직접 수정한 항목 보고, 미수정 항목은 사용자에게 전달 |

## 데이터 전달 프로토콜

```
_workspace/
├── 01_architect_spec.md      (Phase 1 → 모든 Phase)
├── 02_service_summary.md     (Phase 2A → Phase 2B 참조)
├── 03_viewmodel_summary.md   (Phase 2B → Phase 3)
├── 04_ui_summary.md          (Phase 3 → Phase 4)
└── 05_qa_report.md           (Phase 4 → 사용자 보고)
```

중간 파일(`_workspace/`)은 삭제하지 않는다. 사후 검증·부분 재실행 시 활용한다.

## 프로젝트 컨벤션

- **네임스페이스**: `OhMyAgent.AiAgent.Client.{Layer}` (예: `.Models`, `.Services`, `.ViewModels`, `.Views`)
- **파일 위치**: `OhMyAgent.AiAgent.Client/{Layer}/{FileName}.cs`
- **타겟 프레임워크**: `net10.0-windows`
- **WPF 활성화**: `<UseWPF>true</UseWPF>`

## 테스트 시나리오

### 정상 흐름
```
입력: "AI 에이전트와 채팅할 수 있는 ChatWindow를 만들어줘"
예상: Architect → (Service + ViewModel 병렬) → UI → QA → 파일 생성 완료 보고
```

### 부분 재실행
```
입력: "방금 만든 ChatWindow의 ViewModel만 수정해줘"
예상: _workspace/ 감지 → ViewModelEngineer만 재호출 → QA → 보고
```

### 에러 흐름
```
상황: ServiceEngineer 출력 파일 없음
처리: 1회 재시도 → 실패 시 ViewModelEngineer 결과만으로 UIDesigner 진행 → QA 보고에 서비스 누락 명시
```
