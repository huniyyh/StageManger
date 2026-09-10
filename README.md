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
한 번에 하나만 실행됩니다. 이미 실행 중일 때 다시 실행하면 새 인스턴스는 바로 종료되고 기존 인스턴스가 트레이 풍선으로 알려 주며, `--enable` 로 실행했다면 기존 인스턴스를 켭니다.
끄면 최소화했던 창을 모두 복원하고 옮겼던 창을 원래 위치로 되돌립니다.
`--enable` 인수를 주면 시작과 동시에 켜집니다.

로그와 복구 파일은 `%LOCALAPPDATA%\StageManager\` 에 있습니다. 앱이 비정상 종료되면 다음 실행 때 `parked.json` 을 읽어 최소화된 창을 되돌립니다.

## 설치 파일과 업데이트

설치와 자동 업데이트는 Velopack 으로 처리합니다.

```bash
dotnet tool restore
.\build\publish.ps1 -Version 0.1.0
```

`Releases\StageManager-win-Setup.exe` 가 설치 파일입니다. 사용자별로 `%LocalAppData%\StageManager` 에 설치되어 관리자 권한이 필요 없고, .NET 10 데스크톱 런타임이 없으면 설치 프로그램이 받아 줍니다.

업데이트는 GitHub Releases 를 서버로 씁니다. 앱은 시작 30초 뒤와 6시간마다 새 릴리스를 확인해 조용히 내려받고, 트레이 풍선과 메뉴로 알립니다. 적용을 누르면 Stage Manager 를 끄고 창을 모두 되돌린 뒤 재시작합니다. 트레이 메뉴의 "업데이트 확인" 으로 바로 확인할 수도 있습니다. `dotnet run` 이나 bin 폴더에서 실행한 개발 빌드는 확인을 건너뜁니다.

릴리스 절차는 태그 하나입니다. `v0.2.0` 처럼 태그를 푸시하면 GitHub Actions 가 테스트, 게시, 패키징을 거쳐 https://github.com/huniyyh/StageManger/releases 에 올립니다. 앱이 바라보는 저장소 주소는 [StageManager.App.csproj](src/StageManager.App/StageManager.App.csproj) 의 `UpdateRepository` 값입니다.

서명하지 않은 설치 파일은 처음 실행 시 SmartScreen 경고가 뜹니다. 코드 서명 인증서가 생기면 `vpk pack` 에 `--signParams` 를 추가하면 됩니다.

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
- 새로 뜬 백그라운드 창은 400ms 동안 포그라운드가 될 기회를 준 뒤 파킹합니다. 배치 뒤 앱이 스스로 창을 옮기면 한 번 더 제자리에 놓습니다.
- 카드 클릭 시 스왑은 세 단계입니다. 엔진이 전환을 준비(`PrepareSwap`)하고, 클릭 통과되는 투명 오버레이 위에서 스냅샷 이미지가 날아가는 동안 실제 창은 그 뒤에서 파킹(`CommitPark`)과 표시(`CommitPresent`)됩니다. 그 순간의 OS 최소화/복원 효과는 `DWMWA_TRANSITIONS_FORCEDISABLED` 로 창 단위로 끕니다.
- 움직임은 감쇠 스프링 곡선(`SpringEase`, 360ms)을 따르고, 날아가는 그림은 둥근 모서리와 그림자를 가진 창처럼 그려집니다. 스트립 카드는 다시 만들지 않고 유지되므로 자리가 바뀌면 미끄러지고, 새 카드는 서서히 나타나며, 호버하면 살짝 커집니다. 카드를 중앙에 드롭할 때는 `PlanMerge` 로 미리 계산한 자리까지 그림이 커진 뒤 실제 창이 그 아래에 나타납니다.
- 마우스가 스트립에 들어오면 나갈 창의 그림을 미리 찍어 두어 클릭 시 지연이 거의 없습니다. 축소는 GDI 하프톤 `StretchBlt` 로 처리합니다.
- 드래그 앤 드롭은 macOS 와 같이 양방향입니다. 카드를 스트립 밖으로 끌어 놓으면 그 스테이지가 활성 스테이지에 합류하고 놓은 지점에 창이 놓입니다(`MergeIntoActive`). Shift 클릭도 같은 동작입니다. 반대로 활성 창을 제목 표시줄로 끌어 스트립에 놓으면 그 창만 분리되어 스트립의 독립 항목이 되고(`CommitDetach`), 다시 꺼내면 드래그를 시작했던 자리로 돌아갑니다. 창을 끌고 있는 동안 스트립이 밝아져 드롭 위치를 알려 줍니다.
- 스트립은 절대 활성화되지 않아 마우스 캡처를 쓸 수 없으므로, 카드 드래그는 타이머로 포인터와 버튼 상태를 직접 읽어 추적합니다.

## 현재 한계

- 주 모니터만 지원합니다.
- 가상 데스크톱은 macOS 의 Space 처럼 데스크톱마다 스테이지 집합을 따로 둡니다. 현재 데스크톱은 셸이 레지스트리에 남기는 `CurrentVirtualDesktop` 값으로 알아냅니다. 공개 API 나 이벤트가 없어서 틱과 창 이벤트마다 읽고, 전환 직후 들어오는 창 이벤트는 다음 틱에서 소속 데스크톱을 확인한 뒤 처리합니다.
- 관리자 권한 창은 일반 권한 프로세스에서 제어할 수 없습니다.
- 썸네일은 라이브가 아니라 파킹 시점의 정적 이미지입니다.
- 다른 프로세스 창에 DWM 클록을 거는 것은 접근 거부되므로 파킹은 최소화 방식만 가능합니다.

## 다음 단계

1. 실제 사용 피드백으로 필터 규칙과 파킹 타이밍 다듬기
2. 드래그로 수동 그룹핑, 스테이지 순서 저장
3. 멀티 모니터, 가상 데스크톱마다 스트립 표시
4. Windows.Graphics.Capture 로 라이브 썸네일
