# Third-party notices / 외부 구성요소 고지

Agent CLI Hub' original project code and documentation use the [MIT License](LICENSE). Separately owned components keep their own licenses and copyright notices; the project license does not replace them.

Agent CLI Hub 자체 코드·문서는 [MIT 라이선스](LICENSE)를 사용합니다. 외부 구성요소에는 해당 저작자의 라이선스와 고지가 적용됩니다. 프로젝트 라이선스가 외부 구성요소의 조건을 대체하지 않습니다.

## Included or referenced components

| Component / 구성요소 | Version / 버전 | License / 라이선스 | Location and full notice / 위치·전문 |
|---|---|---|---|
| xterm.js (`@xterm/xterm`) | 6.0.0 | MIT | `web/vendor/xterm.js`, `web/vendor/xterm.css`; [license](licenses/xterm-6.0.0-MIT.txt) |
| Fit addon (`@xterm/addon-fit`) | 0.11.0 | MIT | `web/vendor/addon-fit.js`; [license](licenses/addon-fit-0.11.0-MIT.txt) |
| Microsoft.Web.WebView2 SDK | 1.0.4078.44 | BSD 3-Clause terms in the signed package | NuGet dependency; [license](licenses/WebView2-1.0.4078.44-LICENSE.txt), [notice](licenses/WebView2-1.0.4078.44-NOTICE.txt) |
| System.Security.Cryptography.ProtectedData | 9.0.0 | MIT | NuGet dependency; [license](licenses/ProtectedData-9.0.0-LICENSE.txt), [third-party notices](licenses/ProtectedData-9.0.0-THIRD-PARTY-NOTICES.txt) |

The vendor versions were identified by comparing the repository files with official npm tarballs whose SHA-512 integrity was verified. The JavaScript and CSS contents match after normalizing line endings. See [the provenance manifest](licenses/component-provenance.json).
The WebView2 NuGet archive passed `dotnet nuget verify --all`, and its supplied license/notice were copied unchanged. Package metadata and archive terms govern the reviewed SDK version; this does not license Microsoft Edge or a separate WebView2 Runtime.

공식 npm 배포 파일의 SHA-512 무결성을 검증한 뒤 저장소의 JS·CSS와 비교했습니다. 줄바꿈을 정규화하면 내용이 일치합니다. WebView2 패키지는 NuGet 서명 검사를 통과했고 원문 고지를 그대로 복사했습니다. 별도 WebView2 Runtime의 이용·배포 조건은 SDK와 구분해야 합니다.

The copyright and historical attribution header already present in `web/vendor/xterm.css` must also remain intact, including its reference to Fabrice Bellard's original design. Do not remove upstream notices when minimizing or copying assets.

## Distribution

Keep `LICENSE`, this document, and `licenses/` with source copies and application distributions. Build/publish configuration includes these files in application output; the installer checks their presence before packaging.

A self-contained .NET build also includes Microsoft .NET runtime components. Preserve the runtime's supplied license and third-party notices in the actual published payload, and verify the resolved runtime version and distribution conditions before creating a release. No runtime or application executable is published by this source preparation change.

소스·프로그램을 배포할 때 `LICENSE`, 본 문서와 `licenses/`를 함께 제공하십시오. 자체 포함 .NET 배포본에는 실제 포함된 런타임의 라이선스·고지도 보존해야 합니다. 이번 변경은 설치 파일이나 런타임 바이너리를 공개하지 않습니다.

Inno Setup is an external build tool. Its compiler is not included in this repository. Its generated installer components remain subject to the [Inno Setup license](https://jrsoftware.org/files/is/license.txt); preserve embedded copyright notices and do not misrepresent their origin.

## External tools, services, and marks

Claude Code, OpenAI Codex, Google Antigravity, and their account services are independent products. Their binaries, model weights, subscriptions, API usage, and trademarks are not licensed by this repository. Users obtain the tools separately and authenticate under their own applicable agreements.

The product names are used to describe compatibility. Agent CLI Hub is an independent project; no affiliation, sponsorship, partnership, or endorsement by Anthropic, OpenAI, Google, Microsoft, or the xterm.js authors is asserted. This project does not sell or grant access to their accounts or quotas.

서비스 이름은 호환성 설명에 사용합니다. 각 서비스의 계정·모델·구독·상표 권리를 이 저장소의 라이선스로 제공하지 않습니다. Agent CLI Hub는 독립 프로젝트이며 공식 제휴·후원·승인을 주장하지 않습니다.

## Upstream references

- [xterm.js](https://github.com/xtermjs/xterm.js)
- [@xterm/xterm 6.0.0 package metadata](https://registry.npmjs.org/@xterm/xterm/6.0.0)
- [@xterm/addon-fit 0.11.0 package metadata](https://registry.npmjs.org/@xterm/addon-fit/0.11.0)
- [WebView2 1.0.4078.44](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4078.44)
- [ProtectedData 9.0.0](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/9.0.0)
- [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
