# Licensing and external-service terms / 라이선스와 외부 서비스 조건

## 한국어

### 프로젝트 라이선스

자체 프로젝트 코드와 문서는 [MIT 라이선스](LICENSE)를 사용합니다. 사용·수정·상업적 이용·재배포가 허용되며 저작권·라이선스 고지를 유지해야 합니다. 외부 구성요소에는 [해당 라이선스](THIRD_PARTY_NOTICES.md)가 적용됩니다.

MIT의 무보증·책임 제한 문구가 모든 국가의 강행법규를 배제하거나, 타인의 저작권·상표권·특허권·계약상 권리를 없애는 것은 아닙니다. 원래 허가받지 않은 자료에 라이선스를 붙여 공개 권한을 만들 수 없습니다.

### 권리 확인

유지관리자는 자체 코드·문서·`src/LlmTools/app.ico`를 공개하고 라이선스할 권한을 확인할 책임이 있습니다. 고용·외주·고객 계약이나 타인의 기여가 관련되면 해당 권리자의 허가·계약을 확인하십시오. 현재 Git 작성자 정보만으로 이런 계약의 유무를 확정할 수는 없습니다.

### 외부 도구와 인증

LLM Tools는 독립 프로젝트입니다. Anthropic, OpenAI, Google, Microsoft와의 제휴·승인을 주장하지 않습니다. 호환성 설명에 사용한 제품 이름의 상표권은 각각의 권리자에게 있습니다.

공식 CLI는 사용자가 별도로 설치하고, 사용자가 권한을 가진 계정으로 공급자의 인증 절차를 완료해야 합니다. 이 저장소는 구독·계정·사용량을 제공하거나 외부 CLI·모델의 권리를 MIT로 부여하지 않습니다.

공급자의 서비스 약관은 소스의 라이선스와 별개입니다. 특히 현재 구현의 Claude 토큰 백업·저장과 내부 API 사용량 조회에는 정리 또는 공급자 허용 여부의 확인이 필요한 미해결 항목이 남아 있습니다. 자세한 근거와 파일은 [공개 검토 기록](docs/PUBLIC_RELEASE_REVIEW.md)을 참조하십시오. 본 문서나 책임 제한 문구만으로 해당 기능의 준수 문제가 해소되지는 않습니다.

참고:
- [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Anthropic 이용약관](https://www.anthropic.com/legal)
- [OpenAI 이용약관·정책](https://openai.com/policies/)
- [Google 이용약관](https://policies.google.com/terms)
- [GitHub의 저장소 라이선스 안내](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)

### 검토의 범위

이번 준비는 저장소 파일·Git 이력의 비밀정보 검사, 식별된 외부 의존성의 라이선스 고지, 공개 문서 작성에 관한 기술적 점검입니다. 모든 코드의 독립 창작·특허·계약·상표·개인정보·모든 국가의 법률에 관한 법률 의견이나 무위험 보증이 아닙니다. 상업 배포나 계약상 권리가 불명확한 경우에는 관련 계약과 실제 서비스 이용 형태를 법률 전문가와 확인하십시오.

## English

Original project code and documentation use [MIT](LICENSE); preserve its copyright and license notices. Third-party components retain their [own terms](THIRD_PARTY_NOTICES.md). An “as is” disclaimer does not override mandatory law or grant rights in someone else's copyright, trademarks, patents, or contracts.

The maintainer is responsible for confirming authority to publish and license the original source, documents, and `src/LlmTools/app.ico`, including any employer/client agreements or third-party contributions. Git authorship alone does not establish these rights.

LLM Tools is independent, without asserted endorsement by Anthropic, OpenAI, Google, or Microsoft. Product names describe compatibility; their marks remain with their owners. Users install official CLIs separately and complete provider-owned authentication with their own authorized accounts. This project grants no subscription, model, or external-service rights.

Provider terms are separate from MIT. The existing Claude credential backup/storage and internal usage API integrations remain unresolved review items requiring remediation or a verified permitted basis. [The review](docs/PUBLIC_RELEASE_REVIEW.md) identifies the code and sources. Documentation alone does not resolve those implementation concerns.

This preparation covers repository secret checks, notices for identified dependencies, and release documentation. It is not a legal opinion or a guarantee about authorship, patents, contracts, trademarks, privacy, or every jurisdiction. Obtain appropriate professional review where commercial distribution or rights remain uncertain.
