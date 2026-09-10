# Stage Manager for Windows

macOS Stage Manager 를 Windows 11 에서 재현하는 프로젝트입니다. C# / .NET 10 / WPF 로 작성되어 있습니다.

활성 창(또는 같은 앱의 창 묶음)만 화면 중앙에 두고, 나머지 창은 최소화해 오른쪽 스트립에 썸네일로 표시합니다. 스트립 위치는 `LayoutSettings.Side` 로 바꿀 수 있습니다.
스트립의 카드를 클릭하면 그 스테이지가 중앙으로 오고 이전 스테이지는 스트립으로 들어갑니다.

## 구성

| 프로젝트 | 역할 |
|---|---|
| `src/StageManager.Core` | 순수 C#. 창 모델, Alt-Tab 규칙의 창 필터, 레이아웃 계산, 스테이지 상태 머신(`StageEngine`). Win32 의존 없음 |
| `src/StageManager.Win32` | `IWindowSystem` 의 Win32 구현. CsWin32 로 생성한 바인딩, WinEvent 훅, PrintWindow 스냅샷 |
| `src/StageManager.App` | WPF 트레이 앱. 스트립 창, 전역 단축키, 크래시 복구 |
| `src/StageManager.Cli` | 진단 도구 `stagectl`. 창을 건드리지 않고 필터, 이벤트, 스냅샷, 계획을 확인 |
| `tests/StageManager.Core.Tests` | 가짜 창 시스템으로 엔진을 검증하는 xunit 테스트 |

## 빌드와 실행

```bash
dotnet build StageManager.slnx
dotnet test tests/StageManager.Core.Tests
dotnet run --project src/StageManager.App
```

앱은 트레이 아이콘만 띄운 채 꺼진 상태로 시작합니다. `Ctrl+Alt+S` 또는 트레이 아이콘 클릭으로 켜고 끕니다.
끄면 최소화했던 창을 모두 복원하고 옮겼던 창을 원래 위치로 되돌립니다.
`--enable` 인수를 주면 시작과 동시에 켜집니다.

로그와 복구 파일은 `%LOCALAPPDATA%\StageManager\` 에 있습니다. 앱이 비정상 종료되면 다음 실행 때 `parked.json` 을 읽어 최소화된 창을 되돌립니다.

## 진단 CLI

```bash
stagectl list            # 엔진이 관리 대상으로 보는 창 목록
stagectl watch 10        # 10초 동안 창 이벤트 출력
stagectl snap fg out.png # 포그라운드 창 썸네일을 PNG 로 저장
stagectl plan            # 켰을 때 무엇을 할지 dry run (창을 건드리지 않음)
```

## 동작 원리

- 창 목록은 `EnumWindows` 로 얻고, 도구창, 클록된 창, 소유된 창, 제목 없는 창, 셸 창을 제외합니다.
- 변화는 `SetWinEventHook` 아웃오브컨텍스트 훅으로 받습니다. 인젝션이 없어 어떤 프로세스에도 침입하지 않습니다.
- 파킹은 최소화(`SW_SHOWMINNOACTIVE`)이고, 썸네일은 최소화 직전에 `PrintWindow(PW_RENDERFULLCONTENT)` 로 찍은 정적 스냅샷입니다.
- 스트립은 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 인 최상위 투명 WPF 창이라 클릭해도 포커스를 뺏지 않습니다.
- 새로 뜬 백그라운드 창은 400ms 동안 포그라운드가 될 기회를 준 뒤 파킹합니다.

## 현재 한계

- 주 모니터만 지원합니다.
- 가상 데스크톱 전환은 아직 고려하지 않습니다.
- 관리자 권한 창은 일반 권한 프로세스에서 제어할 수 없습니다.
- 썸네일은 라이브가 아니라 파킹 시점의 정적 이미지입니다.
- 스왑 애니메이션이 없습니다.

## 다음 단계

1. 실제 사용 피드백으로 필터 규칙과 파킹 타이밍 다듬기
2. 드래그로 수동 그룹핑, 스테이지 순서 저장
3. 스냅샷 기반 스왑 애니메이션
4. 멀티 모니터, 가상 데스크톱
5. Windows.Graphics.Capture 로 라이브 썸네일
