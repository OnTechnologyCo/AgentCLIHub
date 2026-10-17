# Contributing / 기여 안내

## 한국어

버그 제보와 개선 제안을 환영합니다. 공개 준비 상태와 남은 조건은 [공개 검토 기록](docs/PUBLIC_RELEASE_REVIEW.md)을 먼저 확인하십시오.

- 코드·문서 기여는 [MIT 라이선스](LICENSE)로 제공할 수 있는 내용만 제출하십시오. 본인이 저작권자이거나, 필요한 허가를 받았거나, 기존 라이선스가 허용하는 기여여야 합니다.
- 회사·고객의 소스, 유료 제품의 코드, 출처가 불명확한 코드·아이콘·폰트는 허가 없이 포함하지 마십시오.
- 외부 코드나 자산을 추가하면 출처·버전·라이선스를 명시하고, 원문 고지를 `licenses/`와 [외부 구성요소 고지](THIRD_PARTY_NOTICES.md)에 추가하십시오. 프로젝트 MIT 표시로 외부 조건을 덮지 마십시오.
- 실제 API 키·OAuth 토큰·쿠키·비밀번호·인증 파일·대화 기록·개인정보를 커밋하거나 이슈에 붙이지 마십시오. 보안 문제는 [SECURITY.md](SECURITY.md)에 따라 비공개로 보고하십시오.
- 공급자의 공식 로그인 절차와 권한 검사를 유지하십시오. 사용자 인증을 우회하거나 구독 사용량을 재판매하는 코드는 제출하지 마십시오.
- 변경의 목적, 사용자 동작, 확인한 결과를 PR에 설명하십시오. 동작을 바꿨다면 한국어·영어 README도 함께 갱신하십시오.
- `git commit -s`로 기여의 출처를 확인하는 Signed-off-by를 남기십시오. [Developer Certificate of Origin 1.1](https://developercertificate.org/)에 동의할 수 있을 때만 사용하십시오. Git 커밋의 이름·이메일은 공개될 수 있으므로 GitHub noreply 주소도 사용할 수 있습니다.

기본 빌드: Windows에서 `dotnet build AgentCLIHub.sln`.
호스트 검사: `dotnet run --project tests/HostChecks/HostChecks.csproj`.
통합·UI 검사에는 필요한 도구와 격리 환경을 마련하고, 실제 사용자 계정이나 기존 프로필을 사용하지 마십시오. 수행하지 않은 검사는 수행했다고 적지 마십시오.

프로그램의 설치·실행이나 서비스의 계정 사용은 코드 기여와 별개의 이용약관·권한 확인이 필요합니다.

## English

Bug reports and improvement proposals are welcome. Read the [public-release review](docs/PUBLIC_RELEASE_REVIEW.md) for the current preparation status.

Submit only work you own or are authorized to contribute under the [MIT License](LICENSE). Employer/client code, paid-product code, and assets with unknown provenance require appropriate permission. When adding third-party materials, preserve their licenses and identify source, version, and notices in `licenses/` and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Never commit real keys, OAuth tokens, cookies, passwords, authentication files, transcripts, or personal data. Follow [SECURITY.md](SECURITY.md) for private vulnerability reporting. Preserve provider-owned login flows and permission checks; do not bypass authentication or resell subscription usage.

Explain the purpose, resulting behavior, and actual validation in your PR. Update both README languages for behavior changes. Use `git commit -s` only if you can certify your contribution under [DCO 1.1](https://developercertificate.org/). Commit identity may become public; a GitHub noreply address is acceptable.

Build on Windows with `dotnet build AgentCLIHub.sln`. Run host checks with `dotnet run --project tests/HostChecks/HostChecks.csproj`. Integration/UI checks need their required tools and isolated test profiles. Never use another person's live account or claim checks you did not run. Distribution, service access, and account use remain subject to their separate applicable terms.
