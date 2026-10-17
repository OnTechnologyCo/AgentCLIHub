# 공개 준비와 공개 기록 / Publication preparation and record

Initial review date: **2026-10-10 (Asia/Seoul)**
Initially reviewed source snapshot: **d1033d1ad64134503cfcddf010e1c8c043710cbc**
Pre-publication source/history verified: **30a503e5194fe34f6cb667432b61760ec81d3128**
Repository: **OnTechnologyCo/AgentCLIHub**
Status: **공개 / PUBLIC — published on 2026-10-10 by maintainer decision; unresolved service-term and rights checks remain.**

## 한국어

2026-10-10 유지관리자의 공개 진행 요청에 따라 저장소를 공개로 전환했습니다. 비로그인 GitHub API에서 저장소 접근을 확인했으며, GitHub에서 프로젝트 라이선스를 MIT로 인식하는 것도 확인했습니다. 공개 결정은 아래 B1–B3의 해결이나 공급자 승인·권리 확인이 완료됐다는 뜻은 아닙니다. 해당 기능의 구현은 변경하지 않았습니다.

### 초기 기술 점검

| 항목 | 결과·범위 |
|---|---|
| Git 이력 | 원격의 main·현재 원격 브랜치 전체를 가져와 당시 2개 커밋을 검사. 과거 로컬 작업 폴더·사용자 프로필은 공개 대상에 포함하지 않음 |
| 비밀정보 | SHA-256 검증한 Gitleaks 8.30.1로 전체 원격 이력을 검사. 탐지 3건은 초기 커밋의 `web/vendor/xterm.js` 압축 코드. 공식 배포 코드와 대조해 FourKeyMap·cancellation·UTF-32 처리 구문의 오탐으로 확인. 검사 범위에서 실제 키·토큰을 발견하지 않았다는 결과이며 미탐 가능성을 없애는 보증은 아님 |
| 외부 웹 코드 | `@xterm/xterm 6.0.0`, `@xterm/addon-fit 0.11.0`. 공식 npm tarball SHA-512 무결성 확인 후 줄바꿈을 정규화해 JS·CSS 내용 일치 확인 |
| NuGet 의존성 | net9.0 Windows 구성에 직접 의존성 2개: WebView2 1.0.4078.44, ProtectedData 9.0.0. 실제 패키지 고지 전문 추가. WebView2 아카이브 서명 검사 통과 |
| 자체 프로젝트 라이선스 | 표준 MIT 파일 준비. 외부 구성요소 라이선스와 별도 |
| 사용자 문서 | 양쪽 README에 라이선스·고지·보안·법률·개인정보·본 검토 문서 링크 추가 |
| 기여 안내 | 출처·허가·외부 고지·민감정보 제외·DCO 안내와 PR 양식 추가 |
| 배포 고지 | 프로젝트 빌드·publish 출력에 LICENSE·외부 고지·licenses 포함. 설치본 준비 단계에서 누락 검사 |
| 바이너리 공개 | 이번 변경으로 설치 파일·CLI 바이너리·런타임·사용자 계정을 공개하지 않음 |

### 미해결 검토 항목

**B1. Claude 구독 인증 자료를 앱이 백업·저장·중개하는 기능**

- `src/LlmTools/ClaudeAuthGuard.cs`는 `.credentials.json`을 읽어 DPAPI 사본을 저장하고 복원합니다.
- `src/LlmTools/Accounts.cs`, `TerminalHost.cs` 및 `MainWindow.xaml.cs`에는 setup-token을 읽어 앱 저장소에 보관하고 CLI 실행에 주입하는 경로가 있습니다.
- [Anthropic의 공식 안내](https://code.claude.com/docs/en/legal-and-compliance)는 제3자 개발자의 Claude 계정 자격증명·세션 토큰 수집·저장·중개를 제한하며, 공급자의 자체 인증 흐름과 수정되지 않은 공식 CLI 사용을 구분합니다.
- **필요 조치:** 앱의 토큰 수집·백업·복원·저장·주입 경로를 제거하고 공식 CLI 인증 흐름으로 정리하거나, 해당 이용 형태에 적용 가능한 공급자의 허가를 확인해야 합니다. 단순히 “사용자 책임” 문구를 추가하는 것으로 해결했다고 판단하지 않습니다.
- 일반 터미널에서 공식 CLI를 실행하는 것 자체를 금지됐다고 판단한 것은 아닙니다. 발견한 우려는 앱이 별도로 인증 자료를 처리하는 경로입니다.

**B2. 문서화되지 않은 사용량 API를 앱이 직접 호출**

- `src/LlmTools/UsageProbe.cs`의 `ClaudeAsync`는 CLI OAuth 토큰으로 `api.anthropic.com/api/oauth/usage`를 직접 호출합니다.
- `CodexAccountAsync`는 기록이 없을 때 Codex 로그인 자료로 `chatgpt.com/backend-api/wham/usage`를 직접 호출합니다.
- 코드 주석에서도 공개 API가 아니라고 설명합니다. 호출 성공이나 읽기 전용이라는 사실은 이용 허가의 증거가 아닙니다.
- **필요 조치:** 직접 호출을 제거하고 공식 CLI가 제공하는 사용량 표시·지원된 방법으로 안내하거나, 해당 경로의 허용 여부를 확인하십시오. Codex 로컬 기록에서 이미 저장된 사용량을 읽는 경로는 직접 네트워크 호출과 구분해 검토할 수 있습니다.
- OpenAI 경로의 약관 위반을 확정한 것은 아닙니다. 제3자 앱에서 사용할 수 있다는 공식 근거를 이번 검토로 확인하지 못했습니다.

**B3. 자체 소스와 아이콘의 공개 권리 확인**

- 최초 검토한 두 커밋의 작성자는 OnTechnologyCo이고, 해당 이력은 초기 import와 README 번역 변경입니다.
- 이 정보만으로 회사·고객 계약, 외주 계약, 타인 소스의 사용 허가를 확인할 수 없습니다.
- `src/LlmTools/app.ico`는 저장소의 유일한 이미지 자산입니다. 저장소에는 원 제작 출처·사용 허가 증빙이 없습니다.
- **필요 조치:** 소유자가 자체 코드·문서·아이콘의 공개 및 MIT 제공 권한을 확인하십시오. 아이콘 출처가 불명확하면 권리가 확인된 자산으로 교체하십시오. 필요하면 법률 전문가와 계약을 확인하십시오.

### 검사 한계

이 문서는 기술적 점검의 기록이며 법률 의견, 특허 검색, 모든 코드의 독립 창작 증명 또는 “법적 문제가 절대로 없다”는 보증이 아닙니다. 원격에 없는 사용자 홈·실계정·운영 환경을 읽어 검사하지 않았습니다. 공급자 약관과 라이브러리 버전이 바뀌면 검토도 갱신해야 합니다.

현재 공개 저장소에는 README뿐 아니라 소스와 Git 이력도 포함됩니다. B1–B3는 미해결 검토 항목으로 남아 있으며, 문서와 고지를 추가한 사실이 해당 기능의 허가 또는 자체 자료의 권리를 증명하지는 않습니다.

## English

The repository was made **public on 2026-10-10 at the maintainer's request**. Unauthenticated GitHub API access and GitHub's MIT license detection were verified. This publication decision does not establish that B1–B3 are resolved, provider permission was granted, or original rights were certified. The relevant implementation was not changed.

The initial source review covered two remote commits. The final pre-publication history check covered four commits through `30a503e5194fe34f6cb667432b61760ec81d3128`. Gitleaks 8.30.1, downloaded with verified checksums, reported three candidates in the initial xterm.js bundle. They are minified map/cancellation/UTF-32 code, confirmed against the official npm distribution. No actual credentials were identified in that scope; this is not a guarantee against missed secrets.

Vendor assets were identified as `@xterm/xterm 6.0.0` and `@xterm/addon-fit 0.11.0` using verified npm SHA-512 integrity and content comparison after line-ending normalization. Full license files were added for those components and both declared NuGet dependencies. The WebView2 NuGet archive passed signature verification.

Three unresolved review items remain:

1. **Claude credential handling:** `ClaudeAuthGuard` backs up/restores Claude credentials, and account/terminal/window code captures, stores, and injects setup tokens. [Anthropic's official guidance](https://code.claude.com/docs/en/legal-and-compliance) restricts third-party credential/session-token handling. Remove these paths in favor of provider-owned authentication, or establish an applicable permitted basis. This finding does not assert that hosting an unmodified official CLI in a normal terminal is prohibited.
2. **Internal usage APIs:** `UsageProbe.ClaudeAsync` and `CodexAccountAsync` directly use local OAuth material with undocumented usage endpoints. Successful read-only access is not evidence of permission. Replace them with supported/official CLI mechanisms, or verify permission. No definitive legal conclusion about the OpenAI endpoint is claimed.
3. **Original rights:** Git authorship does not prove employer/client permissions or asset provenance. The maintainer must confirm authority to publish/license the original code, documents, and `app.ico`. Replace an icon of uncertain provenance or obtain appropriate permission.

This is a technical preparation record, not legal advice, a patent search, proof of independent creation, or a guarantee of legal safety. Local live accounts and profiles outside the repository were not inspected. The repository is public by maintainer decision, and these checks remain unresolved. Publication and license notices do not establish provider permission or rights in the original materials.

## 준비 파일 검증 / Preparation validation

- Release build: **passed, 0 warnings / 0 errors**; no application launched.
- Copied license/notice files: **9 files matched their source hashes** in the build output.
- Project XML and installer PowerShell syntax: **passed**.
- New documentation relative links and retained vendor file hashes: **passed**.
- Both referenced NuGet archives: **signature verification passed**.
- Final pre-publication history re-scan (four commits through `30a503e5194fe34f6cb667432b61760ec81d3128`): **no unresolved findings** after recording the reviewed initial-vendor false positives. No live credential was included in this report.
- Repository visibility: **public**, changed on 2026-10-10 at the maintainer's request; unauthenticated repository access **passed**.
- An installed application change, complete runtime behavioral test, installer release, and legal rights certification were **not performed**.
