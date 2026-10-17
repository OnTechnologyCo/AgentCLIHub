# Local data and privacy / 로컬 데이터와 개인정보

## 한국어

이 문서는 저장소에서 확인한 현재 구현을 설명합니다. 법률상 개인정보 처리방침을 대체하지 않으며, 사용자나 배포자가 운영하는 서버·조직의 실제 데이터 처리를 보증하지 않습니다.

- 앱 설정·탭 상태·브라우저 프로필은 Windows 사용자의 `%LOCALAPPDATA%\AgentCLIHub` 아래에 저장됩니다.
- CLI 대화 기록·로그인 상태는 각 CLI가 사용하는 홈·계정 폴더에 남습니다. 계정 전환·대화 이관은 해당 파일을 참조하거나 새 기록을 작성할 수 있습니다.
- 캡처·붙여 넣은 이미지는 작업 폴더의 `.llmtools-attachments/`에 저장됩니다. Antigravity 이관 자료는 `.llmtools-handoff/`를 사용할 수 있습니다.
- 입력·첨부를 CLI에 보내면 해당 CLI·모델 공급자의 약관·데이터 처리 조건이 적용됩니다. 조직·고객 자료를 전송할 권한은 사용자가 별도로 확인해야 합니다.
- 브라우저 탭의 로그인 유지·비밀번호 저장 기능은 민감한 인증 자료를 저장할 수 있습니다. 같은 Windows 계정에서 복호화 가능한 저장 방식이 절대적인 보호를 보장하지는 않습니다.
- 현재 구현에는 Claude 인증 백업·setup-token 저장과 Claude/Codex 내부 사용량 API 직접 조회가 있습니다. 미해결 검토 항목에 해당하며, 공개됐다는 사실을 준수가 확인됐다는 주장으로 해석해서는 안 됩니다. [검토 기록](docs/PUBLIC_RELEASE_REVIEW.md)을 확인하십시오.

지원 요청에는 익명화한 오류·버전·재현 단계만 제공하십시오. 인증 파일, 브라우저 프로필, 토큰 저장소, 대화 기록, 캡처 원본을 공개 저장소나 이슈에 올리지 마십시오.

## English

This describes the inspected implementation, not a substitute for an operator-specific statutory privacy policy or a guarantee about third-party/organizational data processing.

App settings, tab state, and browser profiles live under the Windows user's `%LOCALAPPDATA%\AgentCLIHub`. CLI history/authentication remain in CLI homes and account folders; account changes and transcript transfers can read or write those records. Clipboard/capture images use `.llmtools-attachments/` in the working folder; Antigravity transfer material can use `.llmtools-handoff/`.

Sending input or attachments to a CLI invokes its provider's terms and data practices. Verify permission to send employer/client materials. Browser login/password retention can store sensitive credentials; Windows-user-bound encryption is not an absolute security guarantee.

The current implementation also contains Claude credential backup/setup-token storage and direct Claude/Codex internal usage API calls. These remain unresolved review items; publication does not establish compliant integrations. See the [review](docs/PUBLIC_RELEASE_REVIEW.md).

For support, share only sanitized errors, versions, and reproduction steps. Never publish authentication files, browser profiles, token vaults, transcripts, or original screenshots.
