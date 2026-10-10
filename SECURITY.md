# Security / 보안 안내

## 한국어

보안 제보는 **공개 이슈에 재현용 토큰·비밀번호·쿠키·사용자 대화·개인정보를 포함하지 않고** 전달하십시오.

1. 저장소에서 Security → Report a vulnerability가 활성화되어 있으면 비공개 제보 기능을 사용하십시오.
2. 기능이 보이지 않으면 민감한 세부 내용을 제외한 채 관리자에게 비공개 제보 경로를 요청하십시오. 공개 이슈에는 “비공개 보안 제보 경로 요청” 정도만 남기십시오.
3. 제공할 정보는 영향 버전, 문제 종류, 비밀정보를 제거한 재현 단계입니다. 인증 정보나 개인정보의 원본을 보내지 마십시오.

실제 비밀정보를 커밋했다면 이슈에 값을 다시 붙이지 마십시오. 먼저 해당 공급자에서 폐기·갱신하고, 관리자가 Git 이력·포크·배포본의 영향을 검토하도록 하십시오. 마지막 파일을 지우는 것만으로 과거 커밋에서 사라지지 않습니다.

현재 개발 계열은 1.7.x입니다. 보안 대응은 가능한 범위에서 진행하며, 처리 시간·모든 과거 버전의 지원·취약점 부재를 보증하지 않습니다. 공개 준비 중 확인한 항목은 [검토 기록](docs/PUBLIC_RELEASE_REVIEW.md)에 있습니다.

LLM Tools는 로컬 CLI·브라우저·파일에 접근합니다. 본인에게 권한이 있는 계정·프로필만 사용하고, 공급자의 정상 인증 절차를 따르십시오. 앱의 라이선스가 외부 서비스의 사용 권한을 제공하지 않습니다.

## English

Do not put live tokens, passwords, cookies, transcripts, or personal information into public issues.

Use Security → Report a vulnerability when private reporting is enabled. If unavailable, ask the maintainer for a private channel without posting sensitive details; a public request should contain only the channel request. Report affected versions, impact, and sanitized reproduction steps, never live credentials.

If a secret was committed, revoke/rotate it through its provider before handling Git history and distribution copies. Deleting the latest file does not remove earlier commits or other people's copies.

The current development series is 1.7.x. Support is best effort with no guaranteed response time, coverage of all historical versions, or absence of vulnerabilities. See the [release review](docs/PUBLIC_RELEASE_REVIEW.md).

Use only local accounts, profiles, and files you are authorized to access. Follow provider-owned authentication flows. The repository license does not grant permission to access external accounts or services.
