# AgentCLIHub

**코딩 에이전트 CLI 통합 실행·관리 도구**

여러 코딩 에이전트를 한곳에서 실행하고, 대화와 작업을 이어가세요.

**한국어** | [English](README.en.md)


Claude Code CLI · OpenAI Codex CLI · Google Antigravity CLI 를 **탭으로 여러 개 띄워 쓰는 Windows 데스크톱 앱**이다.

CLI 를 흉내 내지 않는다. ConPTY(가짜 콘솔)에 진짜 CLI 를 붙이고 그 화면을 xterm.js 로 그린다 —
VS Code 통합 터미널과 같은 구조다. 그래서 슬래시 명령·플랜 모드·승인 프롬프트·MCP·스킬이
평소 터미널에서와 **똑같이** 산다. 우리가 하는 일은 넷뿐이다.

1. 어느 CLI 를, 어느 폴더에서, 어떤 인자로 띄울지 정한다
2. 그 화면을 그린다
3. 하단 입력창의 한 턴을 CLI 에 넣어 준다(첨부 포함)
4. 지난 대화 목록을 보여 주고 골라서 이어가게 한다

---

## 폴더의 뜻 — 대화 기록이 아니라 **작업 폴더**다

[＋ 새 대화]에서 고르는 폴더는 **CLI 의 작업 폴더(CWD), 곧 프로젝트 루트**다.
거기에 대화가 저장되는 것이 아니다.

| 무엇 | 어디에 |
| --- | --- |
| 고른 폴더 | CLI 가 파일을 읽고 고치는 대상. `CLAUDE.md`(Claude) · `AGENTS.md`(Codex) 를 자동으로 읽는다 |
| 대화 기록 | **CLI 자신의 홈** — `~/.claude/projects/<폴더>/<세션>.jsonl` · `$CODEX_HOME/sessions/<날짜>/rollout-*.jsonl` |
| 이 앱의 설정 | `%LOCALAPPDATA%\AgentCLIHub\settings.json` (열어 둔 탭 목록·모델·권한·CLI 경로·바로가기) |

입력창에 **이미지를 붙여넣거나 📷 로 화면을 캡처해 보낼 때** `<폴더>\.agentclihub-attachments\` 에 그 이미지를 저장한다.
클립보드 이미지는 경로가 없어 stdin 으로 못 보내고, CLI 는 자기 작업 폴더 **바깥** 파일은
`--add-dir` 없이 못 읽기 때문이다. 📎 로 고른 파일은 경로만 넘기므로 아무것도 안 쓴다.
다른 CLI의 대화를 **Antigravity로 넘길 때**는 `<폴더>\.agentclihub-handoff\` 에 이전 대화의 인계 파일을 저장한다.
인계 파일은 탭 복원과 다른 CLI로 다시 넘길 때도 사용하므로 해당 대화를 사용하는 동안 유지한다.

---

## 화면

```
┌──────────────────────────────────────────────────────────────────┐
│ [＋ 새 대화] [↻ 이어하기] [Jira][GitLab] [●CC probe ×]   [⚙ 설정] │  ← 탭 = 대화 = CLI 세션
├──────────────────────────────────────────────────────────────────┤
│                                                                  │
│   (선택한 탭의 CLI 터미널 — xterm.js)                            │
│                                                                  │
├──────────────────────────────────────────────────────────────────┤
│ [📎][📷] │ 무엇을 시킬까요?                          │ [전송]    │
│ ▸ 기록                                                           │
├──────────────────────────────────────────────────────────────────┤
│ Claude 5h ▬ 12% 2h 10m  주 ▬ 64% 3d 4h   (GPT 월 ▬ 91% 20d)      │  ← 계정 사용량(늘 보인다)
└──────────────────────────────────────────────────────────────────┘
```

* **탭 하나 = CLI 프로세스 하나 = 폴더 하나.** 탭마다 CLI 종류도 다를 수 있다(배지 `CC`/`CX`/`AG`).
  탭 앞의 점은 초록=실행 중, 빨강=끝남, 회색=켜는 중.
* **[＋ 새 대화]** — **CLI(Claude Code · Codex · Antigravity)를 고르고**, 폴더를 고른 뒤 **새 탭**에서 처음부터 시작한다.
  CLI 는 설정에 박아 두지 않고 **누를 때마다 고른다.** 고르기 창에는 CLI 마다 걸릴 값
  (모델 · 추론 강도 · 권한 — 설정에서 CLI 별로 정한 것)이 적혀 있고, 못 찾은 CLI 는 까닭과 함께 흐리게 떠 고를 수 없다.
  마지막으로 고른 CLI 에 커서가 가 있어 **Enter 한 번**이면 같은 CLI 로 연다 — ↑↓ 로 옮기고, 1·2 로 바로 고르고, Esc 로 닫는다.
  같은 폴더를 골라도 새 탭이다(이어가려면 [↻ 이어하기]).
* **[↻ 이어하기]** — 세 CLI 의 지난 대화를 최근 순으로 보여 준다(첫 발화 · 시각 · 폴더).
  고르면 **그 대화를 새 탭에서 이어간다** (`claude --resume <id>` · `codex resume <id>` · `agy --conversation <id>`).
* **[⚙ 설정]** — **CLI 별** 모델 · 추론 강도 · 권한(Claude Code · Codex · Antigravity) · 실행 파일 경로 ·
  **브라우저 바로가기** · **로그인**(Claude 로그인 지키기 · 로그인 유지 · 비밀번호 저장 · 저장된 로그인 지우기).
* **맨 아래 사용량 줄** — Claude · GPT 계정의 한도 사용률(5시간 · 주 · 월)과 리셋까지 남은 시간.
  **설정 화면·브라우저 탭에서도 늘 보인다** — CLI 를 새 대화마다 고르므로 고르기 전에 양쪽 잔량이 보여야 한다.
  지금 탭의 계정은 초록 테두리로 짚는다. 60% 부터 노랑, 85% 부터 빨강. 못 읽었으면 `—`
  (마우스를 올리면 까닭이 보인다). 깔려 있지 않은 CLI 의 계정은 줄에서 빠진다.
* **바로가기 버튼** — 머리줄에 선 북마크. 누르면 그 주소가 **앱 안의 브라우저 탭**으로 열린다.
  설정에서 이름·주소를 원하는 만큼 넣고 지운다(웹 브라우저 북마크와 같다) — 적은 순서가 곧 버튼 순서다.
  [숨김] 은 주소를 남기고 버튼만 치운다. 로그인·SSO 는 브라우저 프로필에 남아 앱을 껐다 켜도 유지된다 —
  **세션 쿠키를 쓰는 사이트**(사내 GitLab 등)는 [⚙ 설정] → [브라우저 로그인] → [로그인 유지] 가
  켜져 있어야 남는다(기본 켜짐). 만료 시각이 있는 쿠키를 쓰는 곳(Jira 등)은 원래도 유지된다.
* **탭 오른쪽 버튼** — 이름 바꾸기(더블클릭도 된다) · 이름 되돌리기 · 닫기 · 다른 탭 모두 닫기,
  그리고 CLI 탭이면 **다른 CLI·계정으로 이어가기** · 폴더 열기 · 경로 복사, 브라우저 탭이면 주소 복사 · 새로고침.
  붙인 이름은 **앱을 껐다 켜도 남는다.** 비우면 자동 이름(폴더 이름 · 문서 제목)으로 돌아간다.
* **계정 여러 개** — [⚙ 설정] → [계정] 에서 CLI 마다 계정을 더하고 로그인한다. 한 계정의 한도가 차면
  탭 오른쪽 버튼 → [다른 CLI·계정으로 이어가기] 로 **같은 탭에서** 다른 계정·다른 CLI 로 넘겨 이어간다(아래 절).
  더 넣은 계정으로 연 탭은 배지 옆에 계정 이름이 붙는다(`CC 회사`).
* **탭 ×** — 그 탭의 CLI 도 함께 끝난다.
* 앱을 껐다 켜면 **열어 두었던 탭이 그대로 돌아온다**(각각 이어하기로 열린다).

### 화면 언어 (1.7.0)

[⚙ 설정] → **화면 언어**에서 **한국어 / English**를 선택한다. 즉시 적용하고 자동으로 저장하며,
앱을 다시 열어도 선택한 언어를 사용한다. 기존 설정에 언어 값이 없으면 한국어로 표시한다.
메뉴·설정·계정·대화 목록·전환 창·앱이 제공하는 파일 선택/화면 캡처 안내에 적용된다.
CLI 자체 출력, 대화 내용, 사용자가 붙인 이름과 입력한 값은 번역하지 않는다. Windows 공용 대화상자의
기본 버튼과 외부 웹사이트는 해당 시스템·사이트의 언어 설정을 따른다.

### Antigravity CLI (1.6.0)

[＋ 새 대화] → **Antigravity** → 작업 폴더를 고르면 `agy` 가 내장 터미널에서 실행된다.
기존 CLI 로그인을 사용하고, 로그인 전이면 CLI 화면의 안내를 따르면 된다.
실행 파일은 PATH 의 `agy.exe`·`agy.cmd`·`agy.ps1`·`agy.bat`, 공식 Windows 설치 경로
`%LOCALAPPDATA%\agy\bin\agy.exe`, PowerShell 순서로 찾는다. `antigravity` IDE 실행기는 사용하지 않는다.
Windows 도구 실행에 필요한 Git Bash 는 설치된 Git 의 `bin` 경로를 자식 환경에 추가해 찾는다.

* 설정에 Antigravity 실행 경로·모델·추론 강도(`low/medium/high/max`)·권한을 따로 저장한다.
  빈 값은 CLI 설정을 따른다. 모델/강도 실행 옵션은 CLI 1.2.14 에서 확인했으며 구버전은 업데이트가 필요할 수 있다.
  1.6.2부터 모델은 선택박스로 표시한다. 설정을 열 때 [공식 `agy models` 명령](https://antigravity.google/docs/cli/headless/#select-a-model-effort-or-agent)을
  백그라운드에서 실행해 현재 모델 목록을 받아 온다. 표시 이름으로 고르고 CLI에는 모델 ID를 넘긴다.
  [다시 불러오기]로 즉시 갱신할 수 있고, [직접 입력…]도 유지한다. 조회 실패나 목록 변경으로 기존 설정을 지우지 않는다.
* 권한은 CLI 기본값, `--sandbox`, `--dangerously-skip-permissions` 중에서 고른다.
  PATH 에 직접 만든 래퍼가 있으면 그 래퍼의 옵션도 적용된다.
* [↻ 이어하기] 는 `~/.gemini/antigravity-cli/history.jsonl` 과 `cache/last_conversations.json` 을 읽는다.
  대화 본문 `.db` 또는 `.pb` 가 남아 있는 항목만 표시하고, 고르면 `--conversation <ID>` 로 연다.
  앱 재시작으로 복원하는 탭은 `--continue` 로 해당 작업 폴더의 마지막 대화를 잇는다.
  다른 CLI에서 인계받은 탭은 인계 파일을 전달했던 세션을 찾아 복원한다.
* 1.7.0부터 탭 우클릭의 **다른 CLI·계정으로 이어가기**에서 Claude/Codex와 양방향으로 대화를 옮긴다.
  Antigravity로 넘길 때는 설정에서 선택한 모델로 인계 파일을 읽고, 확인 답변 뒤 다음 요청을 기다린다.
* 설정 → 계정에서 Antigravity 계정을 추가할 수 있다. 터미널의 Google 로그인 안내를 따르고 완료 후 `/exit`로 종료한다. 사용량은 각 CLI의 `/usage`에서 확인한다.
  브라우저 조작 안내문 자동 주입은 Claude/Codex 에만 적용된다.

공식 안내: [설치·로그인](https://www.antigravity.google/docs/cli/install/),
[이어하기](https://www.antigravity.google/docs/cli/commands/resume/).

### 다른 CLI·계정으로 이어가기

한 계정의 사용 한도(5시간·주·월)가 차면, 그 탭의 대화를 **한도가 남은 다른 계정 · 다른 CLI 로 넘겨 이어간다.**
탭 오른쪽 버튼 → [다른 CLI·계정으로 이어가기…] → 고르기 창(넘길 대화의 첫 발화 · 계정마다 사용량 · 넘어가는 방식) →
Enter. 탭은 그대로(이름·자리·폴더) 두고 안의 CLI 만 갈아 끼운다.

| 넘기기 | 어떻게 | 앞 대화 |
| --- | --- | --- |
| Claude Code A → Claude Code B | B 계정으로 `claude --resume <원본 파일 절대 경로>` | **그대로**(도구 호출·생각까지) |
| Claude Code → Codex | 앞 대화를 Codex 기록 형식으로 옮겨 쓴 **새 대화**를 `codex resume` | 요청·답·도구 호출과 결과를 글로 |
| Codex → Claude Code | 앞 대화를 Claude 기록 형식으로 옮겨 쓴 **새 대화**를 `claude --resume` | 〃 |
| Codex A → Codex B | B 계정 홈에 옮겨 쓴 새 대화를 `codex resume` | 〃 |
| Claude Code / Codex → Antigravity | 작업 폴더의 JSON 인계 파일을 `agy --prompt-interactive`로 읽는 새 대화 | 요청·답·도구 기록을 문맥으로 전달 |
| Antigravity → Claude Code / Codex | CLI가 남긴 텍스트 기록을 대상 CLI 기록 형식으로 옮긴 새 대화 | 인계받았던 대화와 이후 요청·답·도구 기록 |

* **계정을 담는 곳이 CLI 마다 다르다.**
  * Claude Code — 더 넣은 계정마다 **설정 폴더를 따로** 둔다(`CLAUDE_CONFIG_DIR` = `%LOCALAPPDATA%\AgentCLIHub\accounts\<번호>\`).
    로그인은 그 폴더 안에서 **평소 하던 대로** `claude auth login` 을 한다 — claude.ai 구독(Pro·Max·Team·Enterprise),
    회사 SSO(`--sso`), Console API 과금(`--console`) 셋 중 계정을 더할 때 고른다. 로그인(`.credentials.json` · `.claude.json`)만
    따로고 기본 계정의 로그인은 그대로다. **`projects`(대화 기록·자동 메모리) · `file-history` · `plugins` · `skills` 등은 기본 폴더와
    연결(junction)로 같이 쓰고**, `settings.json` · `CLAUDE.md` 는 띄울 때마다 기본 폴더 것으로 맞춘다. 사용자 범위 MCP 서버는
    `.claude.json` 안에 있어 목록만 따로 적어 `--mcp-config` 로 얹는다. 첫 준비 때만 온보딩 완료 표시와 기본 계정이 믿은
    폴더 목록을 적어 둔다(그 뒤로 계정 폴더의 `.claude.json` 은 claude 만 쓴다). 권한이 온전해 **사용량도 읽힌다.**
    대안으로 `claude setup-token` 의 1년짜리 토큰(추론 전용 · `CLAUDE_CODE_OAUTH_TOKEN`)도 쓸 수 있다 — [토큰]. 일반 로그인이 있으면 그쪽이 먼저다.
  * **회사 계정** — Team·Enterprise 는 claude.ai 계정(회사 이메일) 또는 회사 SSO 로, API 과금 조직은 Console 로 더한다.
    이 PC 에 회사가 관리 정책(`forceLoginMethod` · `forceLoginOrgUUID`)을 깔아 두었으면 claude 가 그것을 따른다 —
    예를 들어 `forceLoginMethod: console` 이면 claude.ai 로그인과 setup-token 이 막힌다.
  * Codex — 로그인(`auth.json`)을 환경변수로 대신할 길이 없어 계정마다 **홈(`CODEX_HOME`)을 따로** 둔다
    (`%LOCALAPPDATA%\AgentCLIHub\accounts\<번호>\`). 띄울 때마다 기본 홈에 맞춘다: `config.toml`(이 계정에서만 믿은
    폴더는 살림) · `AGENTS.md` 는 따라가고, `skills` · `plugins` · 샌드박스(`.sandbox*`)는 **연결(junction)로 같이 쓴다.**
    샌드박스를 따로 두면 그 계정의 codex 가 샌드박스를 새로 세우며 같은 Windows 사용자의 비밀번호를 바꿔 기본 계정 쪽이 막힌다.
    `auth.json` 은 절대 복사하지 않는다(한쪽이 갱신하면 다른 쪽 로그인이 죽는다).
* **옮겨 쓰기**(`Transcript.cs`) — 원본은 건드리지 않고 늘 **새 번호로 새 파일**을 만든다. 도구 호출은 codex 가
  Claude Code 기록을 가져올 때 쓰는 모양(`[external_agent_tool_call: X] … [/external_agent_tool_call]`)으로 글이 된다.
  첫 머리에 인계문(어디서 왔는지 · 표식의 뜻 · 원본 경로)이 붙는다.
  * Claude 기록은 `parentUuid` 사슬을 거꾸로 따라 읽는다 — 되감기로 버린 가지는 빠지고, 압축(/compact) 경계에서
    요약부터 시작한다(보존 구간 머리에서 요약으로 건너뛴다). 이어하기 때 부모 없이 적히는 훅 첨부에서는 끊지 않고 앞 대화로 잇는다.
    한꺼번에 부른 도구의 결과는 사슬이 아니라 호출 번호로 붙인다(형제 가지로 적혀 사슬만 따르면 빠진다).
  * Codex 기록은 모델이 본 것(`response_item`)만 읽는다. 추론·원격 압축본은 **발급 계정에 묶인 암호문**이라 옮기지 않는다 —
    그래서 Codex → Codex 도 원본 복사가 아니라 옮겨 쓰기다. 파일에 남은 압축 전 원문을 대신 읽는다.
  * Antigravity는 `brain/<번호>/.system_generated/logs/transcript.jsonl`에서 사용자 요청·답변·도구 기록을 읽는다.
    바이너리 대화 DB는 수정하지 않는다. 본문이 아직 없거나 읽을 수 없으면 전환을 중단하고 원래 탭을 유지한다.
    인계로 시작한 대화는 `.agentclihub-handoff`의 원래 내용도 포함해 다시 옮긴다.
  * 16만 자를 넘으면 맨 앞 요약과 **첫 요청**은 남기고 중간을 줄인다(빠진 개수와 원본 경로를 적어 둔다).
* **넘길 대화 찾기** — 띄울 때 준 번호만 믿지 않는다(`/clear` · `/resume` 으로 바뀐다). 그 탭이 뜬 뒤에 쓰인 기록 중 가장
  최근 것을 보되, 같은 폴더의 **다른 탭이 쥔 번호는 뺀다**. 고르기 창에 넘길 대화의 첫 발화가 떠서 고르기 전에 확인할 수 있다.
  Antigravity는 알고 있는 세션 번호를 우선하며, 인계로 연 탭은 해당 인계 파일을 받은 세션을 찾는다.
* **실패하면 탭은 그대로다** — 옮겨 쓰기가 끝난 뒤에야 옛 CLI 를 끈다. 같은 파일을 잇는 Claude 는 옛 프로세스가
  완전히 끝난 뒤에 새로 띄운다(둘이 한 파일에 덧붙이면 기록이 엉킨다).
* **계정 로그인** — [⚙ 설정] → [계정] → [＋ 계정 추가] 가 곧바로 로그인 탭을 연다(계정 폴더로 `claude auth login`
  · 계정 홈으로 `codex login`). 브라우저 승인 뒤 코드가 보이면 그 탭의 `Paste code here` 에 붙여 넣는다.
  로그인 페이지는 앱 브라우저의 **InPrivate 탭**(탭에 `InPrivate` 표시)으로 열린다 — 앱 브라우저에 이미 로그인된 다른
  claude.ai·Google 계정이 섞여 엉뚱한 계정이 승인되지 않게, 빈 세션에서 시작한다. 탭을 닫으면 그 세션도 사라진다.
  InPrivate 탭·로그인 팝업은 앱을 껐다 켜도 되살리지 않는다.
* **로그인 팝업** — 브라우저 탭의 `window.open`(구글·마이크로소프트·회사 SSO 로그인 창)은 **opener 가 이어진 새 탭**으로 연다
  (WebView2 `NewWindow`). 1.5.1 까지는 주소만 받아 새 탭에서 따로 열어, 로그인 팝업이 인증을 마치고도 결과를 원래 창에
  못 돌려주고 흰 화면으로 멈췄다(claude.ai 의 [Google로 계속하기] 가 팝업이다). 팝업이 스스로 닫히면 그 탭만 닫히고
  화면은 원래 탭으로 돌아간다.
  **WPF WebView2 컨트롤은 페이지의 `window.close()` 를 받으면 자기를 품은 창을 닫는다**(창 하나에 WebView2 하나를 가정한 동작) —
  이 앱은 탭이 전부 메인 창 안이라 그대로 두면 로그인 팝업이 닫히는 순간 앱이 통째로 꺼진다. 그 내장 처리기를 탭마다 떼어 내고,
  못 뗐을 때를 위해 메인 창이 그 경로로 온 닫기를 거절한다.
  [토큰] → [setup-token 열기] 는 화면에 찍힌 토큰을 읽어 저장한다(접힌 줄·테두리·제어문자를 걷고 잇는다). 못 읽으면 붙여 넣기 칸.
  **로그인 페이지가 열린 브라우저가 이미 다른 계정으로 로그인돼 있으면 그 계정이 승인된다** — 먼저 로그아웃하거나 계정을 바꾼다.
* 계정을 바꿔 이어간 탭은 **껐다 켜도 그 계정으로** 되살아난다(`Tabs[].Account`). [↻ 이어하기] 목록은 Codex 계정 홈의
  기록까지 보여 주고, 고르면 그 계정으로 연다. [＋ 새 대화] 도 계정마다 한 줄이라 처음부터 다른 계정으로 열 수 있다.
* 사용량 줄은 계정마다 한 칸이다(`Claude · Claude·회사 · GPT · GPT·개인`). Claude 계정은 그 계정 폴더의 로그인으로 읽고
  (토큰은 갱신하지 않는다 — 한동안 안 쓴 계정은 `—` 일 수 있고, 한 번 띄우면 다시 읽힌다), setup-token 계정과 Console 계정은 `—`.
  Codex 계정은 기록에 남은 값이 없으면 codex 클라이언트가 쓰는 조회 경로(`chatgpt.com/backend-api/wham/usage`, 공개 API 아님)로 읽는다.

### 입력

| 하는 일 | 방법 |
| --- | --- |
| 한 턴 보내기 | 입력창에 쓰고 Enter(또는 [전송]) |
| 줄바꿈 | Shift+Enter |
| 파일 첨부 | 📎 — 경로를 본문에 붙여 CLI 가 읽게 한다 |
| 이미지 붙여넣기 | 입력창 또는 터미널에 Ctrl+V (캡처 → 붙여넣기) |
| 화면 영역 캡처 | 📷 — 화면이 어두워지면 끌어서 고른다. 잘린 그림이 첨부에 붙는다 (Esc·오른쪽 버튼 취소) |
| 오른쪽 버튼 | 입력칸·설정칸: 잘라내기·복사·붙여넣기·전체 선택·칸 비우기 · 터미널: 복사·붙여넣기·전체 선택·화면 지우기 · 기록: 전체 복사 |
| 터미널에서 복사 | 글자를 고른 뒤 Ctrl+C (고른 것이 없으면 평소대로 중단 신호가 간다) · 언제나 복사는 Ctrl+Shift+C |
| 터미널에 붙여넣기 | Ctrl+V |
| TUI 직접 조작 | 터미널을 클릭하고 그냥 치면 된다 — 슬래시 명령·화살표·Esc 가 그대로 간다 |

---

## 실행

```powershell
git clone https://github.com/OnTechnologyCo/AgentCLIHub.git
cd AgentCLIHub
dotnet build AgentCLIHub.sln
.\src\AgentCLIHub\bin\Debug\net9.0-windows\AgentCLIHub.exe
```

배포본을 만들려면:

```powershell
dotnet publish src\AgentCLIHub\AgentCLIHub.csproj -c Release -r win-x64 --self-contained false
```

**필요한 것**

* Windows 10 1809(빌드 17763) 이상 — ConPTY 가 그때 들어왔다
* .NET 9 데스크톱 런타임
* Microsoft Edge WebView2 Runtime (Windows 11 에는 대개 이미 있다)
* `claude` 또는 `codex` 중 쓰려는 CLI. 프로세스·사용자·시스템 PATH 와 관례 설치 경로에서 자동으로 찾고,
  못 찾으면 설정에서 직접 지정한다.

---

## 파일 배치

```
AgentCLIHub\
  AgentCLIHub.sln
  src\AgentCLIHub\
    AgentCLIHub.csproj
    App.xaml(.cs)            미처리 예외를 창으로 보여 준다
    MainWindow.xaml(.cs)     WebView2 + 탭 관리 + Windows 대화상자
    ConPty.cs                가짜 콘솔 — 파이프·크기 변경·환경 덮어쓰기
    CliProfile.cs            CLI 마다 다른 것만: 탐지 경로·플래그·지침 파일·끊을 환경변수
    CliLocator.cs            PATH → 관례 경로 → PowerShell 탐지, npm 래퍼는 node 직접 호출
    CliCatalog.cs            모델·추론 강도·권한 목록과 검증 (표는 여기 한 벌뿐)
    SessionStore.cs          지난 대화 읽기 ([↻ 이어하기] 목록 — Codex 계정 홈까지)
    Settings.cs              %LOCALAPPDATA%\AgentCLIHub\settings.json
    TerminalHost.cs          탭 하나 = pty 하나. 붙여넣기 전송·출력 합치기 · 계정 환경 · 로그인 탭(토큰 읽기)
    Accounts.cs              계정 — Claude 계정 폴더 · Codex 계정 홈 준비(설정 맞추기·연결) · 토큰(DPAPI) · 안전한 삭제
    Transcript.cs            대화 옮기기 — 두 CLI 기록 읽기 · 받을 CLI 형식으로 쓰기 · 탭의 지금 대화 찾기
  web\
    index.html               탭·터미널·입력창·설정·지난 대화 목록
    vendor\                  xterm.js · addon-fit.js · xterm.css (로컬 번들 — 폐쇄망 대응)
```

---

## 알아 둘 것

* **설정은 다음에 여는 대화부터 적용된다.** 이미 떠 있는 탭은 그 인자로 실행 중인
  프로세스라 바꿀 수 없다.
* **모델·추론 강도는 CLI 마다 다르다.** Codex 는 목록을 자기가 준다
  (`$CODEX_HOME/models_cache.json`) — **모델마다 받는 강도가 다르므로**(예: 어떤 모델은 `ultra`
  까지, 어떤 모델은 `xhigh` 까지) 모델을 바꾸면 강도 목록이 따라 바뀐다.
  Claude 는 별칭(`opus`·`sonnet`…) 말고 전체 모델 이름도 받으므로 목록에 없어도 칠 수 있다.
* **그래서 설정에도 CLI 마다 따로 적는다**(`ClaudeModel`·`ClaudeEffort`·`CodexModel`·`CodexEffort`).
  새 대화마다 CLI 를 고르므로 한 칸을 같이 쓰면 Claude 에서 고른 `opus` 가 `codex -m opus` 로 나가 뜨자마자 죽는다.
  예전 설정 파일(CLI 하나 + 모델·강도 한 칸)은 **처음 켤 때 그때 고른 CLI 의 칸으로 옮겨지고**,
  고르기 창도 그 CLI 를 먼저 짚는다 — 업데이트 뒤 첫 [＋ 새 대화] 에서 Enter 만 누르면 전과 같다.
* **권한은 CLI 마다 축이 다르다.** Claude 는 `--permission-mode` 하나, Codex 는
  샌드박스(`-s`)와 승인(`-a`) 둘이다. 하나로 합치지 않았다 — 합치면 화면에 적힌 이름과
  실제로 걸리는 플래그가 갈라진다.
* **탭 안의 에이전트가 브라우저를 부리는 창구**(`agentclihub-browser.exe` ↔ 이름 있는 파이프)는
  Claude·Codex 둘 다 쓴다. Codex 는 명령을 **쓰기 제한 토큰의 샌드박스**로 돌리므로(elevated 면
  `CodexSandboxOffline/Online` 이라는 다른 계정으로까지) 파이프를 그만큼만 더 연다 —
  나 · `CodexSandboxUsers` · **내 로그온 SID**. 대신 요청마다 **표**(`AGENTCLIHUB_BROWSER_TICKET`,
  앱을 켤 때마다 새로 뽑아 이 앱이 띄운 터미널에만 심는다)를 본다: 앱 **밖**에서 돈 Codex 도
  같은 샌드박스 계정·로그온 SID 를 갖기 때문이다. 틀린 표는 거절하고 [기록] 에 경고로 남긴다.
* 쪽지(“이 세션에서 쓸 수 있는 브라우저”)는 Claude 에는 `--append-system-prompt-file`, Codex 에는
  `$CODEX_HOME/agentclihub.config.toml` 의 `developer_instructions` + `-p agentclihub` 로 얹는다.
  그 파일은 **TOML 기본 문자열이라 역슬래시를 이스케이프해야 한다** — 1.4.0 까지는 안 해서
  `C:\Program Files\…` 의 `\P` 때문에 Codex 가 설정을 통째로 못 읽고 멈췄다.
* **처음 여는 폴더에서는 CLI 가 "이 폴더를 믿습니까"를 먼저 묻는다.** 터미널에서
  화살표와 Enter 로 답하면 된다(우리가 대신 답하지 않는다 — 그건 사용자가 할 판단이다).
  Codex 0.155 는 승인(`-a`)·샌드박스(`-s`)를 줘도 이것을 묻는다.
* **Codex 는 폴더를 믿는다고 답한 직후, Windows 샌드박스를 아직 고른 적 없는 PC 면 "샌드박스를 설정하세요" 창을 띄운다.**
  설정을 마치면 codex 가 **그 대화만** 권한을 자기 기본값(승인 on-request · workspace-write)으로 바꾼다 —
  설정의 샌드박스·승인보다 나중에 걸리므로 그 대화에서는 설정이 안 먹는 것처럼 보인다. 탭을 닫고 다시 열면
  설정대로 뜬다(고른 샌드박스는 기억된다). 그런 PC 에서 첫 Codex 탭을 열 때 [기록] 에 한 번 알린다.
* **`-p agentclihub` 로 뜬 Codex 는 설정 쓰기(폴더 신뢰 · 샌드박스 선택 · /model)를 `config.toml` 이 아니라
  `agentclihub.config.toml` 에 한다.** 1.5.2 까지는 앱이 그 파일을 켤 때마다(계정 홈은 탭을 띄울 때마다) 통째로
  다시 써서 그것들이 지워졌다 — 이 앱으로만 Codex 를 쓰던 PC 에서는 새 대화·이어하기마다 위 두 창이 다시 뜨고
  권한이 매번 codex 기본값으로 바뀌었다(다른 PC 에서 신고). 1.5.3 부터는 쪽지(`developer_instructions`)만
  고쳐 쓰고 나머지는 그대로 둔다. 관리자 샌드박스 설정을 마친 흔적(`.sandbox/setup_marker.json`)이 있는데
  선택이 어디에도 없으면(= 예전 버전이 지운 것) `[windows] sandbox = "elevated"` 를 되살린다.
* **앱을 강제 종료하면(작업 관리자 등) CLI 자식이 남는다.** 창을 정상으로 닫으면
  모두 정리된다. 남았다면 작업 관리자에서 `claude.exe`/`codex.exe` 를 끝내면 된다.
* **Codex 의 진행 시간이 멎고 클릭하면 한꺼번에 갱신되는 현상(1.5.4 호환 처리).**
  Codex 0.159.3 + ConPTY 에서 입력이 없을 때 새 화면 출력 자체가 멎는 것을 재현했다.
  화면을 다시 그리는 것만으로는 해결되지 않아, 현재 보고 있는 Codex 탭에서 500ms 이상 출력이 없으면
  터미널 활성화 알림(`CSI I`, FocusIn)을 보낸다. 포커스 보고를 요청한 터미널에서만 동작하며,
  정상 출력 중·다른 탭·설정/고르기 창·로그인·종료 후·앱이 비활성일 때는 보내지 않는다.
  초당 최대 두 번이며 하단 입력창의 초점과 작성 중인 글은 그대로 둔다.
* **Codex 실행 경로 탐지 보완(1.6.1).** 경로가 비어 있으면 PowerShell `Get-Command codex` 를 먼저
  확인하고 같은 프로필 환경에서 실행한다. 공식 설치의 `bin → current → releases/<버전>` 연결은
  매번 실제 `codex.exe` 경로까지 풀어 사용한다. PATH/연결 폴더가 누락된 경우에는
  `CODEX_HOME` 또는 기본 `~/.codex` 의 `packages/standalone/current` 와 `auto-update-version` 도 확인한다.
  버전 경로를 자동으로 설정에 저장하지 않는다. 기록에는 선택한 실행 경로와 탐지 방식,
  실행 실패 시 Windows 오류 번호·메시지를 남긴다. 수동으로 지정한 유효한 경로는 계속 우선한다.
* **Codex 자동 탐지와 대화 반복 출력 수정(1.5.5).** 앱을 켠 뒤 CLI 를 설치해도 사용자·시스템 PATH 를
  다시 읽고 공식 설치 경로·npm 설치본을 찾는다. 그래도 없으면 PowerShell `Get-Command` 로 확인한다.
  npm 설치본은 `node + bin/codex.js`, 일반 `.cmd`·`.ps1` 은 PowerShell 로 실행한다.
  설정의 CLI 경로를 비워 두면 매번 자동 탐지한다. `releases/<버전>/bin/codex.exe` 를 직접 지정하면
  그 구버전이 계속 실행되므로, 자동 업데이트를 따라가려면 설정 경로를 비운다.
  [기록] 은 터미널 크기를 바꾸지 않는 덮개로 열리고, 창 크기 변경은 200ms 동안 모아 최종 크기만 전달한다.
  Codex 에는 `tui.alternate_screen=always`, `tui.fullscreen_transcript=true` 를 실행 인자로 적용해
  전체 화면 대화를 사용한다(0.159.3 실측). 사용자 Codex 설정 파일은 바꾸지 않는다.
  전체 화면 대화를 지원하지 않는 구버전은 CLI 업데이트가 필요하다.

---

## 검증한 것 (실측)

이 기계에서 실제로 돌려 확인했다.

* `dotnet build` — 경고 0 · 오류 0
* CLI 자동 탐지 — `claude.exe`(PATH) · `codex.exe`(PATH) 둘 다 찾음
* Codex 모델별 추론 강도 — `gpt-5.6-terra`→`…max,ultra`, `gpt-5.5`→`…xhigh` (실제로 다르다)
* ConPTY 로 **Claude Code** TUI 가 그려짐 — truecolor SGR·커서 이동·OSC-8 하이퍼링크·신뢰 프롬프트
* ConPTY 로 **Codex** TUI 가 그려짐 — `╭─ >_ OpenAI Codex (v0.152.1) ─╮` 배너·작업 폴더 표시·
  입력 프롬프트·동기 출력 모드(`?2026`)·커서 모양 제어까지
* 터미널 키 입력(`term.in`)으로 신뢰 프롬프트에 응답 → 세션 시작
* 하단 입력창의 한 턴이 대괄호 붙여넣기 + CR 로 제출되고 CLI 가 답함
  (`1+1 은? 숫자만 답하라` → `2`)
* 탭: 폴더 선택으로 새 탭 생성 · 지난 대화 목록에서 골라 `--resume <id>` 로 새 탭 · 탭 닫으면
  그 CLI 만 종료 · 앱을 켜면 탭 복원(`--continue`)
* 이어간 대화가 앞 맥락을 기억함(전사 기록으로 확인)
* 설정 화면: CLI·모델·강도·권한·탐지 결과가 모두 그려짐
* 브라우저 바로가기(북마크): 옛 고정 세 칸(JiraUrl…)이 목록으로 옮겨지고 · 추가/삭제/순서/숨김이
  머리줄에 바로 반영되고 · 저장하면 `Bookmarks` 로만 기록됨(옛 키는 파일에서 사라짐)
* 📷 영역 캡처: 두 모니터를 합친 화면이 얼어붙고 · 고른 크기(500×250)와 잘린 크기가 일치하고 ·
  잘린 그림이 첨부 목록에 미리보기와 함께 붙음
* 브라우저 로그인 유지: 사내 GitLab 의 `_gitlab_session`(세션 쿠키)이 앱을 껐다 켜도 살아남음 —
  탭을 하나도 안 연 채 껐다 켠 뒤에도 쿠키 저장소에 남아 있는 것으로 확인. [유지 안 함] 이면 `session.dat` 이 지워짐
* 오른쪽 버튼 차림표: 입력칸에서 [붙여넣기] 로 한글이 그대로 들어감(호스트가 클립보드를 읽어 준다) ·
  터미널에서는 터미널용 차림표가 뜸(xterm 이 안에 숨겨 둔 textarea 에 안 속는다) ·
  탭 이름을 바꾸면 settings.json 의 `Name` 으로 남음
* 에이전트 브라우저 `shot`: 화면 밖 탭에 걸어도 그 탭을 먼저 띄워 제대로 찍고, 이어지는
  명령이 계속 처리됨(예전에는 그 하나에 창구가 물려 앱을 껐다 켜야 했다)
* 새 대화마다 CLI 고르기 — 호스트 코드는 원본 파일을 그대로 엮은 시험 프로그램으로(33개),
  화면은 헤드리스 Edge 에 가짜 호스트를 붙여 **실제 마우스·키 입력**으로(60개) 확인:
  * 옛 설정(`Backend=claude_code` · `Model=opus` · `Effort=max`)이 `ClaudeModel`·`ClaudeEffort` 로 옮겨지고
    옛 키는 파일에서 사라짐 · codex 였던 파일은 codex 칸으로 · 두 모양이 섞인 파일은 새 칸이 이김
  * 커맨드라인이 자기 칸만 읽음 — Claude 칸만 채우면 `codex` 뒤에 아무것도 안 붙고, 그 반대도 같음
  * 고르기 창: 호스트 순서대로 두 줄 · 마지막 CLI 에 초점 · ↑↓ 순환 · Enter/1·2 로 고르면 `tab.new` 에
    그 CLI 가 실려 감 · Esc·바깥 누르기는 아무것도 안 보냄 · 두 번 눌러도 열리자마자 닫히지 않음 ·
    못 찾은 CLI 는 흐리고 못 고름 · 떠 있는 동안 브라우저 판을 숨겼다가 닫으면 되돌림
  * 설정 화면: CLI 카드 두 장 · Codex 모델을 바꾸면 강도 목록이 그 모델 것으로 바뀜 ·
    저장하면 CLI 별 칸과 `GuardClaudeLogin` 이 실려 가고 옛 키는 안 감
  * 사용량 줄: 설정 화면·브라우저 탭에서도 보임 · 지금 탭의 계정만 초록 · 60/85% 에서 색이 바뀜
* 이어하기 · 앱 재시작 복원 — 이 PC 의 실제 설정(읽기만)과 실제 대화 기록(Claude 51 · Codex 9)으로
  만든 커맨드라인을 **설치된 claude 2.1.270 · codex 0.154.0 에 그대로 넣어**(끝에 `--version` 을 붙여
  인자 검사만 하고 세션은 안 연다) 둘 다 받아들이는 것을 확인:
  `claude --resume <id> --model opus --effort max --permission-mode bypassPermissions --append-system-prompt-file …` ·
  `claude --continue …` · `codex resume <id> -p agentclihub` · `codex resume --last …`(그 폴더에 기록이 있을 때만) ·
  모든 칸을 채운 `codex resume <id> -m … -c model_reasoning_effort=… -s … -a …`.
  1.3.1 에서는 Codex 대화를 이어가도 Claude 칸의 `-m opus -c model_reasoning_effort=max` 가 붙어 나갔다(칸이 하나였다).
  화면에서는 [↻ 이어하기] 목록 60개 · 거르기 · Codex/Claude 줄을 고르면 그 세션 번호가 `tab.resume` 으로 가고
  새 탭이 그 번호로 `term.start` 하는 것 · Esc/[닫기] · 목록이 떠 있는 동안 브라우저 판 숨김을 확인
* 설정 화면의 권한 선택지 전부를 설치된 CLI 에 넣어 봄 — Claude 6개는 다 받고, **Codex 승인 `untrusted` 는
  codex 0.154.0 이 거절한다**(`error: invalid value 'untrusted' for '--ask-for-approval'`, exit 2).
  그래서 목록에서 뺐고, 전에 저장해 둔 값이 남아 있어도 띄울 때 걸러 낸다(안 거르면 Codex 탭이 뜨자마자 죽는다)
* **위 `--version` 검사의 빈틈** — 인자만 보고 `-p` 의 프로필 파일은 **읽지 않는다.** 그래서 1.4.0 의
  TOML 오류(Codex 가 `agentclihub.config.toml:10:5: missing escaped value` 로 멈춤)를 못 잡았다.
  설정까지 읽히는 검사로 바꿔 확인(1.4.1):
  * `codex -p agentclihub mcp list` — 1.4.0 이 쓴 파일은 사용자 화면과 같은 오류, 새 파일은 정상 로드.
    파이썬 `tomllib` 로도 새 파일이 쪽지 본문과 **글자 하나 안 틀리고** 되돌아옴(`"""`·`'''`·끝 역슬래시·짝 없는 CR 도)
  * `codex -p agentclihub debug prompt-input` — 모델이 볼 입력에 쪽지가 developer 역할로, 경로가 그대로 들어감
* Codex 에서 브라우저 쓰기 — 새 창구 코드를 그대로 엮은 가짜 앱(가짜 탭 `FAKE-TAB-7Q`)으로:
  * 도우미를 `codex sandbox` 안에서(elevated · unelevated) 실행 → 1.4.0 권한은 `Access to the path is denied`,
    새 권한은 통과. 샌드박스 토큰을 직접 찍어 원인 확인(쓰기 제한 토큰 · 제한 목록에 그룹이 아니라
    계정·로그온 SID·Everyone 이 있음)
  * 표가 없거나 틀리면 샌드박스 안이든 밖이든 거절 + 앱 기록에 경고
  * **실제 `codex exec` 한 번**: 모델이 쪽지를 읽고 샌드박스에서 `agentclihub-browser.exe tabs` 를 돌려
    (175ms) `FAKE-TAB-7Q` 로 답함 — 환경변수(창구 이름·표)가 Codex 의 셸까지 넘어가는 것도 이것으로 확인
  * 설치본(1.4.1)에 실제로 들어간 트리밍된 도우미로 같은 결과

* **다른 계정으로 이어가기(1.5.0)** — claude 2.1.278 · codex 0.155.0 에서:
  * `CLAUDE_CODE_OAUTH_TOKEN` 이 저장된 로그인보다 앞선다 — 틀린 토큰을 심으면 `401 OAuth access token is invalid`,
    `claude auth status` 는 `authMethod: oauth_token`(기본 계정 이메일·요금제가 안 섞인다)
  * **Codex → Claude**: 이 PC 의 실제 codex 기록(도구 호출 28개)을 옮겨 쓴 파일을 `claude -p --resume <새 번호>` 로 열어
    "첫 요청 · 이전 에이전트가 쓴 도구 · 어디서 왔나" 를 물음 → 셋 다 기록대로 답함
  * **Claude → Codex**: 실제 Claude 기록(사용자 말 3 · 도구 195쌍)을 옮겨 쓴 파일을 `codex resume <새 번호>` 로 열어
    (가짜 콘솔 · 상태 DB 는 임시 폴더) 앞 대화가 그려지고 입력 대기까지 감. 상태 DB 에 없는 번호를 파일 이름으로 찾음
  * Claude 계정 폴더(1.5.1): 새 폴더에서 `claude auth login` 이 claude.ai 로그인 주소(권한 전체 · 코드 붙여 넣기 칸)를 띄움 ·
    준비한 계정 폴더에서 `claude auth status` → 로그인 없음(기본 로그인이 안 샘, 기본 쪽은 그대로 `claude.ai · max`) ·
    `claude plugin list` 에 기본 폴더의 caveman 플러그인이 켜진 채 보임 · `projects` 연결로 기본 대화 기록이 보임 ·
    Console 로그인 판정(`primaryApiKey`) · 첫 준비 때만 온보딩·믿은 폴더를 적고 두 번째부터 계정의 `.claude.json` 은 안 건드림 ·
    사용자 범위 MCP 가 `--mcp-config` 로 얹힘 · 계정 삭제 후 기본 `projects`·`plugins`·`skills`·`file-history` 그대로 ·
    `mklink` 에 슬래시 경로를 주면 "구문이 틀립니다" 로 실패하는 것을 찾아 경로를 펴서 넘기게 고침
  * 로그인 팝업(1.5.2 — "구글 로그인 뒤 흰 화면" 신고): 빈 프로필의 Edge 로 claude.ai 로그인의 [Google로 계속하기] 가
    **opener 달린 팝업**을 열고 본 창은 그대로인 것을 확인. claude 가 BROWSER 에 넘기는 주소는 `redirect_uri=http://localhost:<포트>/callback`.
    앱의 `BrowserHost.cs` 를 그대로 엮은 **실제 WebView2(런타임 153)** 시험 창에서 로그인 팝업 흉내(팝업이 쿠키를 심고 opener 에
    postMessage 한 뒤 window.close) 34개: 팝업이 NewWindow 로 이어짐 · 숨겨진 원래 탭이 결과를 받음 · 팝업이 닫혀도 그 탭만 닫히고
    창은 그대로 · 팝업에서 생긴 로그인 쿠키가 원래 탭에 보임(보통·InPrivate) · InPrivate 와 기본 브라우저의 로그인이 양방향으로 안 섞임 ·
    내장 닫기 처리기를 도로 붙여도 메인 창 울타리가 거절 · **옛 방식은 버그 그대로 재현**(원래 탭은 소식 없음, 팝업은 opener 없음).
    처음 돌렸을 때 팝업의 window.close() 가 **시험 창 전체를 닫는 것**을 보고 WPF 컨트롤 안의 처리기를 찾아냈다.
    도우미: 로그인 탭에서만 `open <주소> --private` 를 보냄(가짜 창구). 화면: InPrivate 표시 · 팝업이 닫히면 맨 끝 탭이 아니라 원래 탭으로
  * Codex 계정 홈: 준비한 홈에서 `codex login status` → `Not logged in`(기본 로그인이 안 샘) ·
    `codex -p agentclihub mcp list` → 기본 홈의 설정·MCP 가 읽힘 · `debug prompt-input` 에 브라우저 쪽지가 들어감
  * 계정 삭제: 계정 홈의 연결(skills · plugins · `.sandbox*`)만 끊기고 **기본 홈의 파일 수가 그대로**(샌드박스 비밀 포함)
  * 기록 읽기(실제 파일): Claude 병렬 도구 결과 196/196(사슬만 따르면 28개가 빠졌다) · 압축 5번 한 대화가 요약부터 시작 ·
    이어하기 때 부모 없이 적힌 훅 첨부를 넘어 앞 대화로 이어짐 · Codex 0.155(원격 압축 · AGENTS.md · 개발자 지침 빠짐) ·
    Codex 가져오기본(legacy) · 0.152 TUI 기록. 16만 자 예산에서 첫 요청·마지막 조각이 남음
  * `setup-token` 화면 토큰 읽기 9가지(접힌 줄 · 테두리 · 커서 이동 · OSC 링크 · 덜 그린 화면 · 마지막 화면) ·
    토큰은 평문으로 안 남음 · 설정 동기화(이 계정에서만 믿은 폴더 유지 · 두 번 해도 같음)
  * 화면(헤드리스 Edge · 가짜 호스트 · 실제 마우스·키, 66개): 탭 배지 · 오른쪽 버튼 → 고르기 창(넘길 대화 · 계정별 사용량 ·
    같은 대화/옮겨 쓰기 표시 · 로그인 전 계정 막힘) · ↑↓/Enter/Esc · 사용량이 새로 와도 초점 유지 · `tab.switched` 로 같은 탭이
    새 세션 번호로 다시 뜸 · 로그인 탭엔 메뉴 없음 · 새 대화 계정 줄 · 이어하기 계정 · 설정 [계정] 카드(추가 · 로그인 ·
    토큰 넣기(암호 칸) · 이름 · 두 번 눌러 삭제) · 페이지 오류 0

* **Codex 에서 설정의 샌드박스·승인이 안 먹던 문제(1.5.3 — 다른 PC 에서 신고)** — codex 0.155.1:
  * 이 PC 에서는 재현 안 됨: 앱이 만드는 커맨드라인(`… -s danger-full-access -a never -p agentclihub`) 그대로 새 대화 ·
    이어하기(read-only · on-request 로 기록된 대화 포함)를 띄워 `/status` → 둘 다 `Full Access`. 이 PC 의 `config.toml` 에는
    `[windows] sandbox = "elevated"` 와 주요 폴더 신뢰가 이미 있었다(Codex 앱을 따로 쓰면서 생긴 것)
  * `-p agentclihub` 로 뜬 codex 에서 폴더를 믿는다고 답하면 **`agentclihub.config.toml` 에** `[projects.…]` 가 적히는 것을 실측(config.toml 은 그대로)
  * codex 소스(rust-v0.155.1)로 경로 확인: 신뢰 창은 `trust_level` 이 없으면 `-a`·`-s` 와 무관하게 뜸 · Windows 에서 방금 믿었는데
    샌드박스가 꺼져 있으면 설정 창 · 설정이 끝나면 프리셋 `auto`(on-request · workspace-write)를 세션에 덮어씀 ·
    `-p` 가 있으면 설정 쓰기가 그 프로필 파일로 감
  * 병합(시험 프로그램 27개): 1.5.2 머리글 파일 · codex 가 붙인 신뢰/샌드박스 · `/model` 최상위 키 · 여러 줄 문자열 안의 `[` 줄 ·
    표 안의 같은 이름 키 · 되살리기(없을 때만 · 기존 `[windows]` 표 안에 · 옛 스위치/점 표기면 안 함) · 두 번 해도 같음 —
    결과 전부 파이썬 `tomllib` 로 읽힘. 안내 판정(빈 홈 · 신뢰만 · 프로필에 선택 · 옛 스위치 · 이 PC 기본 홈)
  * 이 PC 의 실제 프로필(시험 중 codex 가 적은 신뢰 포함)에 새 병합 → 쪽지 글자 그대로 · 신뢰 유지 → 실제 codex 가
    그 파일로 **신뢰 창 없이** 떠서 `Full Access`
  * 새 PC 에서 샌드박스 설정 창이 뜨는 장면 자체는 못 띄움 — 이 PC 는 이미 설정돼 있고, 지우려면 사용자 config.toml 이나
    로그인 사본이 필요해서 안 했다

* **Codex 진행 시간 갱신(1.5.4)** — 설치된 codex 0.159.3, 원본 `ConPty.cs`, 원본 HTML/xterm 을
  헤드리스 Edge 에 연결해 확인했다. 임시 Codex 홈 + 로컬 시험용 Responses 서버에서 응답을 지연시켜
  작업 중 상태를 만들었다(실제 계정·모델 호출 없음, 시험 홈의 긴 소켓 경로 때문에 `--no-daemon` 사용).
  * 기존 방식: `Working (2s)` 가 유지되다가 클릭 후 `Working (15s)` 로 바뀜.
    그 사이 ConPTY 출력 바이트도 없었다. 동기화 출력 모드가 잠기거나 xterm 만 멎은 경우는 아니었다.
  * 수정된 HTML: 하단 입력창에 초점을 둔 채 추가 클릭 없이 20초 이상 진행 시간이 계속 증가.
    Codex 내부에서 주기 갱신이 멎는 원인까지 확정한 것은 아니며, 앱 쪽의 호환 처리다.
  * `python -m unittest discover -s tests -v` — 브라우저 회귀 검사 8개 통과(Python Playwright + Edge 필요).
    신호 종류·빈도, 초안/입력 초점 유지, 정상 출력 시 억제, 활성 탭 구분, 로그인/시작 중/종료/계정 전환,
    포커스 보고 해제, 숨김/비활성, 설정·브라우저·고르기 창에서의 억제를 확인.

* **Codex 실행·크기 변경(1.5.5)**:
  * `dotnet run --project tests/HostChecks/HostChecks.csproj -c Release` — 자동 탐지한 실제 Codex 0.159.3 실행,
    오래된 프로세스 PATH, 삭제된 지정 경로, 경로 우선순위, npm 래퍼(시험용 JS), 일반 cmd·ps1 실행,
    한국어 인자·종료 코드, PowerShell 탐지·실행, 새 대화·이어하기의 화면 모드 인자를 확인.
  * `python -m unittest discover -s tests -v` — 11개 통과. 기존 갱신 검사에 기록 덮개의 터미널 크기 유지,
    연속 크기 변경을 한 번만 전송, 기록이 열릴 때 네이티브 브라우저 숨김/복원을 추가했다.
  * 실제 Codex 0.159.3 + 원본 ConPTY·HTML·xterm + 로컬 시험 서버에서 50줄 응답을 표시한 뒤
    창 크기를 네 번 변경했다. 전체 화면 버퍼가 유지되고, 과거 대화가 스크롤백에 중복 쌓이지 않으며,
    마지막 응답이 한 번만 보였다. 기록 열기 뒤에도 터미널 행·열과 표시 내용이 같았고 마우스 휠로 앞부분을 볼 수 있었다.
    실제 계정·모델 API 는 사용하지 않았다.

* **Antigravity CLI 통합(1.6.0)**:
  * `dotnet run --project tests/HostChecks/HostChecks.csproj -c Release` — 총 39개 통과. `agy.cmd` 자동 탐지와
    실제 버전 실행, 오래된 PATH, Git Bash 경로, 새 대화·이어하기 인자, 다른 CLI 설정과 분리,
    모델 이름의 공백·따옴표·역슬래시 보존, 권한·강도 검증, 입력 이력/캐시 병합과 삭제된 기록 제외를 확인.
  * `python -m unittest discover -s tests -v` — 총 14개 통과. Antigravity 선택·AG 배지·입력 전송,
    설정 저장·실행 파일 선택이 Codex/Claude 경로를 건드리지 않는 것, 이력에서 Antigravity 로 이어하기,
    지원하지 않는 계정 이동 메뉴 제외를 확인. 기존 Codex 갱신·기록 덮개·resize 검사도 통과.
  * 실제 `TerminalHost` + ConPTY + 번들 HTML/xterm 을 연결해, 탐지된 `agy.cmd` → PowerShell →
    Antigravity 1.2.14 가 열리는 것, 기존 로그인 인식, 한글 입력, resize 후 초안 유지,
    `term.send` 로 `/help` 를 보내 도움말 화면을 여는 것까지 확인. 실행 중 CLI 자체 업데이트가
    1.0.1 → 1.2.14 로 적용됐다. 시험 작업 폴더에서 확인했으며 모델에 요청을 보내지는 않았다.

* **Codex 자동 탐지 보완(1.6.1)**:
  * 호스트 검사 46개, 화면 검사 14개 통과. PowerShell 명령 탐지, 두 단계 junction 해석,
    업데이트 후 새 파일 선택, 연결 누락 시 버전 기록 확인, Windows 실행 오류 보존을 검증했다.
  * 수동 경로를 비운 실제 `TerminalHost → PowerShell → Codex 0.159.3 → ConPTY → xterm.js` 에서
    별도 테스트 홈과 로컬 응답 서버를 사용해 대화 응답, 창 크기 변경 4회, 기록 버튼을 확인했다.
    버전 폴더 경로가 자동 선택됐고 대화 반복 출력은 없었다. 실제 OpenAI 모델 호출은 하지 않았다.

* **Antigravity 모델 선택(1.6.2)**:
  * 호스트 검사 53개, 화면 검사 16개 통과. 실제 `agy models`의 14개 모델 조회,
    표시 이름/ID 전달, 목록 갱신, 조회 실패·취소, 오래된 응답 무시, 저장 전 설정 보존을 검증했다.
  * 실제 CLI 1.2.14에서 조회된 ID `gemini-3.8-flash-high`를 `--model`로 지정해
    `Gemini 3.8 Flash` 모델 화면이 열리는 것을 확인했다. 모델에 요청을 보내지는 않았다.

* **Antigravity 대화 전환·화면 언어(1.7.0)**:
  * 호스트 검사 71개, 화면 검사 23개 통과. 이 환경에서 Windows junction을 따라가지 못해 기존 연결 탐색 검사 2개는 건너뛰었다.
    설치 버전 파일로 실제 Codex 실행 파일을 찾는 대체 경로는 통과했다.
  * 탭 우클릭에서 Antigravity를 양방향으로 고르는 것, `.db`/`.pb` 세션 탐색, 본문 파싱, 도구 기록 보존,
    인계 후 다시 전환할 때 원래 대화와 새 요청 유지, 인계 세션 복원과 원본 보존을 확인했다.
  * 실제 `agy.cmd` → PowerShell → Antigravity 1.2.14 → ConPTY/xterm에서 시험용 대화를 인계했다.
    모델이 인계 JSON을 읽고 그 안의 프로젝트명 `Amber Otter`를 확인하는 답변을 했으며,
    그 실제 기록을 별도 시험 폴더의 Claude/Codex 기록으로 다시 변환했다.
    `.cmd`에서 개행 인자가 잘리는 문제를 확인해 초기 인계 요청은 한 줄로 전달하고 회귀 검사를 추가했다.
  * 한국어↔English 전환, 저장한 언어와 첫 화면 복원, 잘못된 값의 한국어 기본값 처리,
    설정·팝업·오류 문구 번역, 모델 초안·대화 입력·사용자 이름·터미널 내용 보존을 확인했다.

**아직 확인 안 한 것**

* **실제 구글 로그인을 앱 안에서 끝까지.** 팝업 처리는 실제 WebView2 로 흉내 낸 로그인 창으로 확인했다 — 진짜 Google 계정으로
  claude.ai 에 로그인해 승인하는 것은 사용자 계정이 필요해 못 해 봤다.
* **두 번째 계정으로 실제 로그인·넘기기.** 이 PC 에 두 번째 Claude·ChatGPT 계정이 없어 계정 폴더에서 로그인을 끝까지 하는 것
  (claude.ai · SSO · Console · setup-token 토큰 자동 저장 · 계정 홈의 `codex login`)과 B 계정으로 넘긴 뒤 한 턴 주고받기는 못 해 봤다.
  로그인 화면이 뜨는 것까지, 토큰 읽기는 화면 모양을 흉내 낸 입력으로, B 계정 실행은 틀린 토큰 · 로그인 없는 폴더로 확인한 것이다.
  (기본 계정의 로그인을 계정 폴더에 복사해 시험하지 않았다 — 한쪽이 토큰을 갱신하면 다른 쪽 로그인이 죽는다.)
* **옮겨 쓴 Codex 대화로 실제 한 턴.** 화면이 앞 대화를 그리고 입력을 받을 준비가 된 것까지 봤다. 이 PC 의 Codex 가
  무료 요금제(월 한도 91% 사용)라 모델 호출은 안 했다. Claude 쪽은 실제 한 턴까지 했다.
* **설치본에서 끝까지 누른 것.** 위는 호스트 코드와 화면을 따로 돌린 것이다.
* **설치된 1.4.1 앱의 Codex 탭에서 진짜 브라우저 탭(Jira·GitLab)을 부리는 것.** 위는 새 창구 코드를
  가짜 앱에 엮어 확인한 것이다 — 창구·도우미·쪽지·Codex 샌드박스는 전부 실물이지만, 앱 창과 진짜 탭은 아니다.

* **새 CLI 고르기를 설치본에서 끝까지 누른 것.** 위 검증은 호스트와 화면을 따로 돌린 것이다 —
  고르기 → 폴더 창(제목에 CLI 이름) → 탭이 그 CLI 로 뜨는 한 바퀴를 실제 앱에서는 아직 안 돌렸다.
  (개발 중인 앱이 설치본 탭 안에서 돌고 있어, 둘째 창을 띄우면 같은 settings.json 을 나눠 쓰며
  탭을 되살리다 지금 대화까지 `--continue` 로 이어 버린다.)

* **실제 OpenAI 모델로 Codex 한 턴을 주고받는 것.** Codex TUI 의 송수신은 로컬 응답 서버로
  검증했다. 실제 모델 서비스까지 연결한 검증은 아니다.
* npm 래퍼(`claude.cmd`) → `node cli.js` 우회 경로. 이 기계의 claude 는 네이티브
  `claude.exe` 라 그 경로를 타지 않는다.
* 첨부(📎 · 이미지 붙여넣기)를 실제 대화에서 주고받는 것 — 코드는 있지만 실제로 안 붙여 봤다.
* 화면을 눈으로 본 것이 아니라 **접근성 트리(UI Automation)와 pty 바이트열**로 확인했다.
  글자·상태·프로세스는 전부 맞았지만, 색이나 자간 같은 **보이는 모양**은 사람이 한 번 봐야 한다.

## 공개 상태와 라이선스

자체 코드·문서는 [MIT 라이선스](LICENSE)를 사용합니다. 외부 구성요소의 고지는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)와 [licenses/](licenses/)에 있습니다. MIT는 수정·상업적 이용·재배포를 허용하며 고지를 보존해야 합니다.

**공개 상태:** 2026-10-10 유지관리자의 요청에 따라 소스를 공개했습니다. 현재 구현의 Claude 토큰 백업·저장과 내부 사용량 API 직접 호출에는 약관 검토·정리가 필요한 항목이 남아 있습니다. [공개 검토 기록](docs/PUBLIC_RELEASE_REVIEW.md)의 미해결 항목을 확인하십시오. 공개 결정이나 라이선스 파일 추가만으로 준수 또는 법적 무위험이 확인된 것은 아닙니다.

[기여 안내](CONTRIBUTING.md) · [보안 제보](SECURITY.md) · [라이선스·서비스 조건](LEGAL.md) · [로컬 데이터·개인정보](PRIVACY.md)

## 1.8.1 이름 통일 및 자동 업그레이드

프로젝트 이름은 **AgentCLIHub**입니다. 실행 파일은 `AgentCLIHub.exe`, 설치 파일은
`AgentCLIHub-Setup-1.8.1.0.exe`입니다. 기존 버전을 따로 제거할 필요 없이 설치 파일을 실행합니다.

설치 프로그램이 기존 설치 위치·버전과 설정 형식·파일 잠금을 먼저 확인합니다.
`%LOCALAPPDATA%\LlmTools`의 계정·설정·브라우저 데이터를 `%LOCALAPPDATA%\AgentCLIHub`로 이전하고,
파일 해시와 연결 대상을 확인한 뒤 설정 안의 해당 절대 경로를 변환합니다. 암호화 키와 인증 파일은 변경하지 않습니다.

기존 설치 폴더는 새 설치 경로로 이동합니다. 설치 성공 후 이전 실행 파일·브라우저 도우미·바로가기와
임시 이전 기록을 정리하며, 이전 데이터 폴더는 남기지 않습니다. 설치 취소 시 원래 경로와 설정으로 복원합니다.
새 경로에 별도 데이터가 있거나 파일이 잠겨 있으면 덮어쓰지 않고 이유를 표시합니다.
설치 AppId는 유지하므로 Windows에 중복 제품으로 등록하지 않습니다.

프로젝트에 이미 저장된 첨부·대화 인계 파일과 외부 CLI의 대화 기록은 기존 참조를 보존합니다.
화면·설치 이름과 소스 네임스페이스·프로젝트 폴더는 `AgentCLIHub`로 통일했습니다.
새 첨부·인계 파일은 `.agentclihub-attachments`와 `.agentclihub-handoff`에 저장합니다.
Codex는 `-p agentclihub`와 `agentclihub.config.toml`을 사용하며, 처음 만들 때 기존
`llmtools.config.toml`의 신뢰·샌드박스·모델 설정을 가져옵니다. 기존 파일은 보존합니다.
브라우저 연결 환경변수는 `AGENTCLIHUB_BROWSER_*`를 사용합니다.
옛 이름은 구버전 설치·데이터 감지, 이전 인계 파일 읽기, 암호화 데이터 복호화에 필요한 호환 코드에만 유지합니다.

이전 로직 검증: `dotnet run --project tests/MigrationChecks/MigrationChecks.csproj -c Release`
이름 변경 호환성 검증: `dotnet run --project tests/RenameChecks/RenameChecks.csproj -c Release`

## 1.8.2 관리자 설치 오류 수정

관리자 설치에서 일반 사용자 권한의 이전 도우미가 보호된 임시 폴더에 결과 파일을 만들지 못해
`-532462766`으로 종료되던 문제를 수정했습니다. 설치 프로그램이 결과 파일을 미리 만들고
그 파일에만 쓰기를 허용합니다. 도우미는 결과 파일을 열 수 있는지 먼저 확인한 뒤 데이터 작업을 시작합니다.
실행 중인 앱을 닫고 `AgentCLIHub-Setup-1.8.2.0.exe`로 다시 설치하십시오.

결과 기록 검사: `dotnet run --project tests/InstallerChecks/InstallerChecks.csproj -c Release`.
설치본 빌드는 실제 배포 도우미를 읽기 전용 임시 폴더 조건에서 검사한 뒤 패키징합니다.
이 검사는 사용자의 설치·데이터를 변경하지 않습니다.

## 1.8.4 계정 관리 · 대화 이어하기

- **설정 → 계정 → [이름]**으로 Claude·Codex·Antigravity의 기본 계정 이름도 변경할 수 있습니다. 이름은 저장되며 계정 선택·탭·지난 대화·사용량 표시에 반영됩니다.
- **Antigravity 계정 추가**를 지원합니다. Google 로그인 완료 후 `/exit`로 종료하면 새 대화 및 이어하기에서 선택할 수 있습니다.
- 제품명 변경 후 PowerShell의 CLI 자동 탐지 결과를 읽지 못하던 문제도 수정했습니다.
- 추가 Antigravity 계정은 `%LOCALAPPDATA%\AgentCLIHub\accounts\<번호>\profile` 아래에 로그인·설정·대화를 따로 저장합니다. agy의 원래 원격 로그인용 파일 저장 모드를 사용하므로 URL·코드 입력이 필요할 수 있습니다. 기본 Windows 자격 증명을 공유하거나 복사하지 않습니다. 이 동작은 agy 1.4.2에서 확인했습니다.

- Claude Code 계정을 바꿀 때 원본 대화 파일의 절대 경로로 이어갑니다. 계정별 `projects` 폴더가 분리되거나 연결 폴더의 세션 번호 검색이 실패해도 같은 대화를 찾습니다.
- 지난 대화 목록에 계정별 Claude 기록도 표시합니다. 보조 에이전트 기록은 제외하며, 선택한 기록이 없으면 다른 대화로 대체하지 않습니다.
- **[↻ 이어하기] → 대화 우클릭 → [다른 CLI·계정으로 이어가기…] → 계정 선택**으로 새 탭에서 이어갑니다. 일반 클릭은 기존 계정으로 이어갑니다.
- 다른 CLI의 대화를 Claude로 옮길 때는 선택한 계정의 기록 폴더에 저장합니다. 로그인 파일은 복사하지 않습니다.

`AgentCLIHub-Setup-1.8.4.0.exe`로 업데이트한 뒤 앱을 다시 시작하세요.

## 1.8.3 에이전트 브라우저 탭 정리

에이전트 지침에 데이터 수집·작업 완료 후 직접 연 작업용 탭을 `close`로 닫고,
`tabs`로 정리를 확인하도록 명시했습니다. 기존 사용자 탭과 유지 요청한 탭은 보존합니다.
`open` 응답에도 해당 탭의 `closeCommand`를 제공합니다.
에이전트의 `close`가 네이티브 브라우저만 제거하고 화면의 탭 표시를 남기던 문제도 수정했습니다.
`AgentCLIHub-Setup-1.8.3.0.exe`로 업데이트한 뒤 앱을 다시 시작하면 새 지침이 CLI 실행에 적용됩니다.
