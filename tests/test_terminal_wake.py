"""Browser regression checks: python -m unittest discover -s tests -v.

Requires Python Playwright and Microsoft Edge. Uses the shipped HTML/xterm bundle
with a fake WebView2 message bridge; no CLI, account or model API is contacted.
"""
from pathlib import Path
import unittest

from playwright.sync_api import sync_playwright


class TerminalWakeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.chromium.launch(channel="msedge", headless=True)

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()

    def setUp(self):
        self.page = self.browser.new_page()
        self.errors = []
        self.page.on("pageerror", lambda error: self.errors.append(str(error)))
        self.page.clock.install()
        self.page.add_init_script("""
            window.sent = [];
            window.chrome = {webview: {
                postMessage: m => sent.push({...m, at: performance.now()}),
                addEventListener: (_, handler) => window.receive = handler
            }};
        """)
        self.page.goto((Path(__file__).resolve().parents[1] / "web/index.html").as_uri())

    def tearDown(self):
        self.page.close()
        self.assertEqual(self.errors, [])

    def message(self, **message):
        self.page.evaluate("m => receive({data: m})", message)

    def open_terminal(self, id="cx", backend="codex_cli", ready=True, login=False, focus=True):
        self.message(t="tab.opened", id=id, backend=backend, cli="Test CLI",
                     name=id, cwd="C:/test", login=login)
        if ready:
            self.message(t="term.ready", id=id)
        if focus:
            self.message(t="term.out", id=id, data="\x1b[?1004h")
        self.page.clock.run_for(100)

    def clear_messages(self):
        self.page.evaluate("sent.length = 0")

    def inputs(self):
        return self.page.evaluate("sent.filter(m => m.t === 'term.in')")

    def assert_quiet(self):
        self.clear_messages()
        self.page.clock.run_for(1600)
        self.assertEqual(self.inputs(), [])

    def test_idle_codex_wakes_without_typing_or_changing_draft(self):
        self.open_terminal()
        self.page.locator("#input").fill("작성 중인 초안")
        self.page.clock.run_for(100)
        self.clear_messages()
        self.page.clock.run_for(2100)
        signals = self.inputs()
        self.assertGreaterEqual(len(signals), 3)
        self.assertTrue(all(m["id"] == "cx" and m["data"] == "\x1b[I" for m in signals))
        self.assertTrue(all(b["at"] - a["at"] >= 500 for a, b in zip(signals, signals[1:])))
        self.assertEqual(self.page.locator("#input").input_value(), "작성 중인 초안")
        self.assertTrue(self.page.locator("#input").evaluate("e => e === document.activeElement"))
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'term.send')"))

    def test_normal_output_suppresses_wakes_then_idle_recovers(self):
        self.open_terminal()
        self.clear_messages()
        for i in range(15):
            self.message(t="term.out", id="cx", data=f"\rWorking {i}s")
            self.page.clock.run_for(100)
        self.assertEqual(self.inputs(), [])
        self.assertIn("Working 14s", self.page.locator(".pane.on .xterm-rows").inner_text())
        self.page.clock.run_for(1100)
        self.assertGreaterEqual(len(self.inputs()), 1)

    def test_only_active_codex_is_woken(self):
        self.open_terminal("cx")
        self.open_terminal("cx2")
        self.clear_messages()
        self.page.clock.run_for(1600)
        self.assertTrue(self.inputs())
        self.assertTrue(all(m["id"] == "cx2" for m in self.inputs()))
        self.open_terminal("cc", backend="claude_code")
        self.assert_quiet()

    def test_starting_login_and_unrequested_focus_are_excluded(self):
        for options in ({"ready": False}, {"login": True}, {"focus": False}):
            with self.subTest(options=options):
                self.open_terminal(**options)
                self.assert_quiet()
                self.message(t="tab.closed", id="cx")
                self.page.clock.run_for(100)

    def test_focus_reporting_can_be_disabled_by_cli(self):
        self.open_terminal()
        self.message(t="term.out", id="cx", data="\x1b[?1004l")
        self.page.clock.run_for(100)
        self.assert_quiet()

    def test_hidden_and_unfocused_documents_are_excluded(self):
        self.open_terminal()
        self.page.evaluate("Object.defineProperty(document, 'hidden', {configurable: true, value: true})")
        self.assert_quiet()
        self.page.evaluate("delete document.hidden; document.hasFocus = () => false")
        self.assert_quiet()

    def test_settings_browser_and_dialogs_are_excluded(self):
        self.open_terminal()
        self.page.locator("#tab-conf").click()
        self.assert_quiet()
        self.page.evaluate("showTab('cx')")
        self.page.clock.run_for(100)
        for selector in ("#hist", "#pick", "#swap"):
            with self.subTest(dialog=selector):
                self.page.locator(selector).evaluate("e => e.classList.add('on')")
                self.assert_quiet()
                self.page.locator(selector).evaluate("e => e.classList.remove('on')")
        self.message(t="tab.opened", id="web", kind="browser", name="Browser", url="about:blank")
        self.page.clock.run_for(100)
        self.assert_quiet()

    def test_agent_browser_close_removes_tab_and_keeps_user_browser_and_draft(self):
        self.open_terminal()
        self.page.locator("#input").fill("보존할 초안")
        self.message(t="tab.opened", id="user-web", kind="browser", name="User tab", url="https://example.test/user")
        self.message(t="tab.opened", id="agent-web", kind="browser", name="Agent tab", url="https://example.test/task")
        self.message(t="tab.closed", id="agent-web")
        self.page.clock.run_for(100)
        self.assertEqual(self.page.locator(".tab[data-id='agent-web']").count(), 0)
        self.assertEqual(self.page.evaluate("[...TABS.keys()]"), ["cx", "user-web"])
        self.assertEqual(self.page.evaluate("active"), "user-web")
        self.assertEqual(self.page.locator("#input").input_value(), "보존할 초안")
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'browser.layout').at(-1).id"), "user-web")

    def test_agent_background_browser_close_preserves_active_cli(self):
        self.open_terminal()
        self.message(t="tab.opened", id="agent-web", kind="browser", name="Agent tab", url="https://example.test/task")
        self.page.evaluate("showTab('cx')")
        self.page.locator("#input").fill("계속 작성")
        self.clear_messages()
        self.message(t="tab.closed", id="agent-web")
        self.page.clock.run_for(100)
        self.assertEqual(self.page.evaluate("active"), "cx")
        self.assertEqual(self.page.locator(".bview").count(), 0)
        self.assertEqual(self.page.locator("#input").input_value(), "계속 작성")
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'term.start' || m.t === 'term.restart')"))
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'browser.layout').at(-1).id"), "")

    def test_last_browser_close_and_duplicate_notification_leave_no_ghost_tab(self):
        self.message(t="tab.opened", id="agent-web", kind="browser", name="Agent tab", url="https://example.test/task")
        self.message(t="tab.closed", id="agent-web")
        self.page.clock.run_for(100)
        self.assertEqual(self.page.evaluate("TABS.size"), 0)
        self.assertIsNone(self.page.evaluate("active"))
        self.assertEqual(self.page.locator(".bview").count(), 0)
        self.assertTrue(self.page.locator("#empty").is_visible())
        self.clear_messages()
        self.message(t="tab.closed", id="agent-web")
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'tab.close')"))

    def test_exit_error_close_and_account_switch_stop_old_wakes(self):
        for event in ("term.exit", "term.error", "tab.switched", "tab.closed"):
            with self.subTest(event=event):
                self.open_terminal()
                self.message(t=event, id="cx", cli="Test CLI", backend="codex_cli")
                self.page.clock.run_for(100)
                self.assert_quiet()
                self.message(t="tab.closed", id="cx")

    def test_log_panel_does_not_resize_or_restart_terminal(self):
        self.open_terminal()
        self.message(t="term.out", id="cx", data="Conversation stays here\r\n")
        self.page.clock.run_for(400)
        before = self.page.evaluate("({cols:tab('cx').term.cols, rows:tab('cx').term.rows, height:tab('cx').pane.clientHeight})")
        self.clear_messages()
        for _ in range(4):
            self.page.locator("details.diag summary").click()
            self.page.clock.run_for(400)
        after = self.page.evaluate("({cols:tab('cx').term.cols, rows:tab('cx').term.rows, height:tab('cx').pane.clientHeight})")
        self.assertEqual(before, after)
        self.assertEqual(self.page.evaluate("sent.filter(m => ['term.resize','term.start','term.send'].includes(m.t))"), [])
        self.assertIn("Conversation stays here", self.page.locator(".pane.on .xterm-rows").inner_text())

    def test_window_resize_sends_only_settled_dimensions(self):
        self.open_terminal()
        self.page.clock.run_for(400)
        self.clear_messages()
        for width in (1200, 1100, 1000):
            self.page.set_viewport_size({"width": width, "height": 800})
            self.page.clock.run_for(60)
        self.page.clock.run_for(400)
        resizes = self.page.evaluate("sent.filter(m => m.t === 'term.resize')")
        self.assertEqual(len(resizes), 1)
        actual = self.page.evaluate("({cols:tab('cx').term.cols, rows:tab('cx').term.rows})")
        self.assertEqual({k: resizes[0][k] for k in actual}, actual)
        self.assertEqual(resizes[0]["id"], "cx")

    def test_log_overlay_hides_native_browser_until_closed(self):
        self.message(t="tab.opened", id="web", kind="browser", name="Browser", url="about:blank")
        self.page.clock.run_for(400)
        self.clear_messages()
        self.page.locator("details.diag summary").click()
        self.page.clock.run_for(100)
        self.page.wait_for_function("sent.some(m => m.t === 'browser.layout')")
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'browser.layout').at(-1).id"), "")
        self.clear_messages()
        self.page.locator("details.diag summary").click()
        self.page.clock.run_for(100)
        self.page.wait_for_function("sent.some(m => m.t === 'browser.layout')")
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'browser.layout').at(-1).id"), "web")

    def antigravity_config(self, model="", language="ko"):
        self.message(t="conf", file="test-settings.json", version="1.6.0", settings={
            "AntigravityCliPath": "", "CodexCliPath": "keep-codex", "ClaudeCliPath": "keep-claude",
            "AntigravityModel": model, "AntigravityEffort": "", "AntigravityPermission": "", "Bookmarks": [], "UiLanguage": language},
            catalog={"antigravity_cli": {
                "display": "Antigravity", "memoryFile": "AGENTS.md / GEMINI.md", "models": [],
                "modelsComplete": False, "modelKey": "AntigravityModel", "effortKey": "AntigravityEffort",
                "pathKey": "AntigravityCliPath", "efforts": ["low", "medium", "high", "max"],
                "supportsAccounts": True, "supportsTransfer": True,
                "perm": [{"key": "AntigravityPermission", "label": "권한", "choices": ["sandbox", "dangerously-skip-permissions"]}]}},
            accounts=[{"backend": "antigravity_cli", "id": "", "name": "기본", "loggedIn": False}],
            detect={"antigravity_cli": {"found": True, "path": "C:/agy/agy.exe", "source": "PATH"}})

    def test_language_changes_immediately_without_restarting_cli_or_losing_drafts(self):
        self.open_terminal()
        self.message(t="term.out", id="cx", data="이 대화 내용은 그대로 유지\r\n")
        self.page.locator("#input").fill("작성 중인 한글 요청")
        self.page.locator("#tab-conf").click()
        self.antigravity_config("custom-model")
        self.page.locator("#ag-model-custom").fill("unsaved-model")
        self.page.clock.run_for(400)
        self.clear_messages()
        self.page.locator("#ui-language").select_option("en")
        self.assertEqual(self.page.locator("html").get_attribute("lang"), "en")
        self.assertEqual(self.page.locator("#newchat").inner_text(), "＋ New chat")
        self.assertEqual(self.page.locator("#tab-conf").inner_text(), "⚙ Settings")
        self.assertEqual(self.page.locator("#ag-model-custom").input_value(), "unsaved-model")
        self.assertEqual(self.page.locator("#input").input_value(), "작성 중인 한글 요청")
        self.assertIn("Permissions", self.page.locator("#conf").inner_text())
        self.assertIn("No shortcuts", self.page.locator("#conf").inner_text())
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'conf.language').at(-1).language"), "en")
        self.assertEqual(self.page.evaluate("sent.filter(m => ['term.start','term.in','term.send'].includes(m.t))"), [])
        self.page.get_by_role("button", name="Save", exact=True).click()
        saved = self.page.evaluate("sent.filter(m => m.t === 'conf.save').at(-1).v")
        self.assertEqual(saved["UiLanguage"], "en")
        self.assertEqual(saved["AntigravityModel"], "unsaved-model")
        self.page.locator("#ui-language").select_option("ko")
        self.assertEqual(self.page.locator("#newchat").inner_text(), "＋ 새 대화")
        self.page.evaluate("showTab('cx')")
        self.page.clock.run_for(100)
        self.assertIn("이 대화 내용은 그대로 유지", self.page.locator(".pane.on .xterm-rows").inner_text())

    def test_default_account_can_be_renamed_without_login_or_delete_controls(self):
        self.page.locator("#tab-conf").click()
        self.antigravity_config()
        row = self.page.locator('.an[data-backend="antigravity_cli"][data-account-id=""]')
        ops = row.locator("xpath=following-sibling::div[2]")
        self.assertEqual(ops.get_by_role("button").all_text_contents(), ["이름"])
        ops.get_by_role("button", name="이름", exact=True).click()
        row.locator("input").fill("회사 Google")
        row.locator("input").press("Enter")
        rename = self.page.evaluate("sent.filter(m => m.t === 'account.rename').at(-1)")
        self.assertEqual({k: rename[k] for k in ("id", "backend", "name")},
                         {"id": "", "backend": "antigravity_cli", "name": "회사 Google"})
        self.page.locator('[data-conf-draft="new-account:antigravity_cli"]').fill("개인 Google")
        self.page.get_by_role("button", name="＋ Antigravity 계정 추가", exact=True).click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'account.add' && m.backend === 'antigravity_cli' && m.name === '개인 Google')"))

    def test_custom_default_name_is_preserved_in_picker_transfer_and_tab(self):
        self.message(t="newchat", last="codex_cli", items=[{
            "backend": "codex_cli", "display": "Codex", "found": True, "usable": True,
            "account": "", "accountName": "회사 GPT", "opts": []}])
        self.assertIn("회사 GPT", self.page.locator("#plist").inner_text())
        self.page.locator("#plist button.prow").click()
        self.open_terminal()
        self.message(t="tab.account", id="cx", account="", accountName="회사 GPT")
        self.assertIn("회사 GPT", self.page.locator("#tabs").inner_text())
        self.message(t="switch", id="cx", loading=False,
                     **{"from": {"display": "Codex", "account": "", "accountName": "회사 GPT"}},
                     session={"title": "fixture", "turns": 1},
                     items=[{"backend": "antigravity_cli", "display": "Antigravity", "account": "", "accountName": "회사 Google", "usable": True, "how": "context"}])
        self.assertIn("회사 GPT", self.page.locator("#sfrom").inner_text())
        self.assertIn("회사 Google", self.page.locator("#slist").inner_text())

    def test_language_restores_from_settings_and_cached_first_paint(self):
        self.antigravity_config(language="en")
        self.assertEqual(self.page.locator("#resume").inner_text(), "↻ Resume")
        self.page.reload()
        self.assertEqual(self.page.locator("#newchat").inner_text(), "＋ New chat")
        self.antigravity_config(language="ko")
        self.assertEqual(self.page.locator("#newchat").inner_text(), "＋ 새 대화")
        self.antigravity_config(language="invalid")
        self.assertEqual(self.page.locator("html").get_attribute("lang"), "ko")

    def test_english_context_menu_transfer_and_host_errors_preserve_user_text(self):
        self.antigravity_config(language="en")
        self.open_terminal("ag", backend="antigravity_cli")
        self.message(t="tab.renamed", id="ag", alias="설정")
        self.assertIn("설정", self.page.locator("#tabs").inner_text())
        self.page.locator("#tabs .tab").click(button="right")
        self.page.locator("#menu button").filter(has_text="Continue with another CLI/account").click()
        self.message(t="switch", id="ag", loading=False,
                     **{"from": {"display": "Antigravity", "account": "", "accountName": "기본"}},
                     session={"title": "로그인", "turns": 2, "when": "10-01 12:00", "id": "s"},
                     items=[{"backend": "codex_cli", "display": "Codex", "account": "", "accountName": "기본", "usable": True, "how": "copy"}])
        self.assertIn("Default", self.page.locator("#sfrom").inner_text())
        self.assertIn("로그인", self.page.locator("#ssess").inner_text())
        self.assertIn("2 user messages", self.page.locator("#ssess").inner_text())
        self.message(t="switch.session", id="ag", session={"error": "Antigravity 텍스트 기록이 아직 없습니다. 응답이 끝난 뒤 다시 시도해 주세요."})
        self.assertIn("not available yet", self.page.locator("#ssess").inner_text())
        self.assertTrue(self.page.locator("#slist button.prow").is_disabled())

    def test_english_settings_have_no_untranslated_app_labels(self):
        self.page.locator("#tab-conf").click()
        self.antigravity_config(language="en")
        text = self.page.locator("#conf").inner_text().replace("한국어", "")
        import re
        self.assertIsNone(re.search(r"[가-힣]", text), text)
        self.message(t="conf.models.loading", request=1, path="")
        self.message(t="conf.models", request=1, error="모델 목록을 읽지 못했습니다(종료 코드 7). CLI 로그인과 네트워크를 확인해 주세요.")
        self.assertIn("exit code 7", self.page.locator("#ag-model-status").inner_text())

    def test_antigravity_picker_and_terminal_use_own_identity(self):
        self.message(t="newchat", last="antigravity_cli", items=[{
            "backend": "antigravity_cli", "display": "Antigravity", "found": True, "usable": True,
            "account": "", "accountName": "기본", "opts": []}])
        self.page.clock.run_for(100)
        row = self.page.locator("#plist button[data-backend='antigravity_cli']")
        self.assertEqual(row.locator(".cli").inner_text(), "AG")
        row.click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'tab.new' && m.backend === 'antigravity_cli')"))
        self.open_terminal("ag", backend="antigravity_cli")
        self.assertEqual(self.page.locator(".tab.on .cli").inner_text(), "AG")
        self.assertTrue(self.page.evaluate("tabMenu('ag').some(m => m && m.label === '다른 CLI·계정으로 이어가기…')"))
        self.assert_quiet()  # Codex FocusIn 호환 신호를 Antigravity 에 보내지 않는다.
        self.page.locator("#input").fill("한글 메시지")
        self.page.evaluate("send()")
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'term.send' && m.id === 'ag' && m.text === '한글 메시지')"))

    def test_antigravity_settings_save_and_file_picker_are_isolated(self):
        self.page.locator("#tab-conf").click()
        self.antigravity_config()
        self.page.clock.run_for(100)
        card = self.page.locator("#conf .card").filter(has=self.page.locator(".h", has_text="Antigravity"))
        self.page.locator("#ag-model").select_option("__custom_model__")
        self.page.locator("#ag-model-custom").fill("Gemini test model")
        card.locator("select").nth(1).select_option("high")
        card.locator("select").nth(2).select_option("sandbox")
        self.assertIn("Antigravity 계정 추가", self.page.locator("#conf").inner_text())
        self.assertEqual(self.page.locator("#usage").inner_text(), "")
        self.message(t="cli.picked", which="antigravity_cli", path="D:/CLI 한글/agy.exe")
        self.page.get_by_role("button", name="저장", exact=True).click()
        saved = self.page.evaluate("sent.filter(m => m.t === 'conf.save').at(-1).v")
        self.assertEqual(saved["AntigravityCliPath"], "D:/CLI 한글/agy.exe")
        self.assertEqual(saved["AntigravityModel"], "Gemini test model")
        self.assertEqual(saved["AntigravityEffort"], "high")
        self.assertEqual(saved["AntigravityPermission"], "sandbox")
        self.assertEqual(saved["CodexCliPath"], "keep-codex")
        self.assertEqual(saved["ClaudeCliPath"], "keep-claude")

    def test_antigravity_model_dropdown_saves_id_and_preserves_drafts_on_refresh(self):
        self.page.locator("#tab-conf").click()
        self.antigravity_config()
        self.message(t="conf.models.loading", request=1, path="")
        self.assertTrue(self.page.locator("#ag-model-refresh").is_disabled())
        self.message(t="conf.models", request=1, models=["gemini-test", "claude-test"],
                     modelLabels={"gemini-test": "Gemini Test", "claude-test": "Claude Test"})
        select = self.page.get_by_role("combobox", name="Antigravity 모델", exact=True)
        select.select_option(label="Gemini Test")
        self.assertFalse(self.page.locator("#ag-model-custom").is_visible())
        card = self.page.locator("#conf .card").filter(has=self.page.locator(".h", has_text="Antigravity"))
        card.locator("select").nth(1).select_option("high")
        self.page.locator("#ag-model-refresh").click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'conf.models.refresh' && m.path === '')"))
        self.message(t="conf.models.loading", request=2, path="")
        self.message(t="conf.models", request=2, models=["gemini-test", "new-test"],
                     modelLabels={"gemini-test": "Gemini Test", "new-test": "New Model"})
        self.assertEqual(select.input_value(), "gemini-test")
        self.assertIn("New Model", select.inner_text())
        self.page.get_by_role("button", name="저장", exact=True).click()
        saved = self.page.evaluate("sent.filter(m => m.t === 'conf.save').at(-1).v")
        self.assertEqual(saved["AntigravityModel"], "gemini-test")
        self.assertEqual(saved["AntigravityEffort"], "high")
        self.assertEqual(saved["CodexCliPath"], "keep-codex")

    def test_antigravity_model_errors_and_stale_responses_preserve_custom_value(self):
        self.page.locator("#tab-conf").click()
        self.antigravity_config(model="saved-custom")
        self.assertEqual(self.page.locator("#ag-model-custom").input_value(), "saved-custom")
        self.message(t="conf.models.loading", request=1, path="")
        self.message(t="conf.models.loading", request=2, path="")
        self.message(t="conf.models", request=1, models=["stale"], modelLabels={"stale": "Old response"})
        self.assertNotIn("Old response", self.page.locator("#ag-model").inner_text())
        self.message(t="conf.models", request=2, error="로그인을 확인해 주세요.")
        self.assertIn("로그인", self.page.locator("#ag-model-status").inner_text())
        self.assertFalse(self.page.locator("#ag-model-refresh").is_disabled())
        self.page.get_by_role("button", name="저장", exact=True).click()
        self.assertEqual(self.page.evaluate("sent.filter(m => m.t === 'conf.save').at(-1).v.AntigravityModel"), "saved-custom")

    def test_antigravity_transfer_is_available_in_tab_context_menu(self):
        self.open_terminal("ag", backend="antigravity_cli")
        self.page.locator(".tab[data-id='ag']").click(button="right")
        self.page.locator("#menu button").filter(has_text="다른 CLI·계정으로 이어가기…").click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'switch.list' && m.id === 'ag')"))
        self.message(t="switch", id="ag", **{"from": {"backend": "antigravity_cli", "display": "Antigravity"}},
                     loading=True, items=[{"backend": "codex_cli", "display": "Codex", "account": "", "usable": True, "how": "copy"}])
        row = self.page.locator("#slist button[data-backend='codex_cli']")
        self.assertTrue(row.is_disabled())
        self.message(t="switch.session", id="ag", session={"id": "s1", "title": "옮길 요청", "turns": 2, "when": "10-01 21:00"})
        self.assertFalse(row.is_disabled())
        row.click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'tab.switch' && m.id === 'ag' && m.backend === 'codex_cli')"))

    def test_transfer_to_antigravity_keeps_tab_and_explains_handoff(self):
        self.open_terminal("cx")
        self.message(t="switch", id="cx", **{"from": {"backend": "codex_cli", "display": "Codex"}}, loading=False,
                     session={"id": "s1", "title": "앞 대화", "turns": 2, "when": "10-01 21:00"},
                     items=[{"backend": "antigravity_cli", "display": "Antigravity", "account": "", "usable": True, "how": "context"}])
        row = self.page.locator("#slist button[data-backend='antigravity_cli']")
        self.assertIn("인계 파일", row.inner_text())
        self.assertEqual(row.locator(".cli").inner_text(), "AG")
        row.click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'tab.switch' && m.id === 'cx' && m.backend === 'antigravity_cli')"))
        self.message(t="tab.switched", id="cx", backend="antigravity_cli", cli="Antigravity", account="", fresh=True)
        self.assertEqual(self.page.evaluate("TABS.size"), 1)
        self.assertEqual(self.page.locator(".tab.on .cli").inner_text(), "AG")
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'term.start' && m.id === 'cx' && m.fresh)"))

    def test_unreadable_transfer_source_keeps_original_tab(self):
        self.open_terminal("ag", backend="antigravity_cli")
        self.message(t="switch", id="ag", **{"from": {"backend": "antigravity_cli", "display": "Antigravity"}},
                     loading=True, items=[{"backend": "codex_cli", "display": "Codex", "usable": True, "how": "copy"}])
        self.message(t="switch.session", id="ag", session={"error": "대화 본문을 아직 읽을 수 없습니다."})
        self.assertTrue(self.page.locator("#slist button").is_disabled())
        self.assertIn("읽을 수 없습니다", self.page.locator("#ssess").inner_text())
        self.assertEqual(self.page.evaluate("tab('ag').backend"), "antigravity_cli")
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'tab.switch')"))

    def test_antigravity_history_resumes_with_its_backend(self):
        self.message(t="history", items=[{"backend": "antigravity_cli", "cli": "Antigravity",
            "id": "ag-session", "cwd": "C:/workspace", "folder": "workspace", "title": "이전 대화",
            "when": "2026-10-01 20:00", "account": ""}])
        self.page.locator("#hist").evaluate("e => e.classList.add('on')")
        self.assertIn("Antigravity", self.page.locator("#hlist").inner_text())
        self.page.locator("#hlist .hrow").click()
        self.assertTrue(self.page.evaluate("sent.some(m => m.t === 'tab.resume' && m.backend === 'antigravity_cli' && m.sessionId === 'ag-session')"))


    def history_fixture(self):
        self.page.locator("#resume").click()
        self.message(t="history", items=[
            {"backend": "claude_code", "cli": "Claude Code", "id": "first", "cwd": "C:/first",
             "folder": "first", "title": "First", "when": "2026-10-19", "account": "account-a"},
            {"backend": "claude_code", "cli": "Claude Code", "id": "second", "cwd": "C:/second",
             "folder": "second", "title": "Second", "when": "2026-10-19", "account": "account-b"}])

    def history_choices(self):
        self.message(t="switch", id="history-test", history=True, loading=True,
                     **{"from": {"backend": "claude_code", "display": "Claude Code", "account": "account-b"}},
                     items=[{"backend": "claude_code", "display": "Claude Code", "account": "account-a",
                             "accountName": "A", "usable": True, "how": "same"}])

    def test_filtered_history_right_click_keeps_exact_source(self):
        self.history_fixture()
        self.page.locator("#hq").fill("Second")
        self.clear_messages()
        self.page.locator("#hlist .hrow .ti").click(button="right")
        self.page.locator("#menu button").first.click()
        request = self.page.evaluate("sent.find(m => m.t === 'history.switch.list')")
        self.assertEqual((request["sessionId"], request["account"], request["cwd"]),
                         ("second", "account-b", "C:/second"))
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'tab.resume')"))

    def test_history_account_transfer_needs_no_source_terminal(self):
        self.history_fixture()
        self.history_choices()
        self.assertTrue(self.page.locator("#slist button").is_disabled())
        self.message(t="switch.session", id="history-test", session={"id": "second", "title": "Second", "turns": 2})
        self.page.locator("#slist button").click()
        self.assertTrue(self.page.locator("#slist button").is_disabled())
        request = self.page.evaluate("sent.find(m => m.t === 'history.switch')")
        self.assertEqual((request["backend"], request["account"]), ("claude_code", "account-a"))
        self.message(t="history.switched", id="history-test")
        self.assertFalse(self.page.locator("#swap").is_visible())
        self.assertFalse(self.page.locator("#hist").is_visible())
        self.assertFalse(self.page.evaluate("sent.some(m => m.t === 'tab.switch')"))

    def test_history_transfer_failure_preserves_list_and_existing_tab(self):
        self.open_terminal("existing")
        self.page.locator("#input").fill("draft")
        self.history_fixture()
        self.history_choices()
        self.message(t="switch.session", id="history-test", session={"id": "second", "title": "Second", "turns": 2})
        self.page.locator("#slist button").click()
        self.message(t="history.switch.failed", id="history-test", error="Source file unavailable")
        self.assertIn("Source file unavailable", self.page.locator("#ssess").inner_text())
        self.assertTrue(self.page.locator("#hist").is_visible())
        self.assertFalse(self.page.locator("#slist button").is_disabled())
        self.assertEqual(self.page.locator("#input").input_value(), "draft")
        self.assertTrue(self.page.evaluate("TABS.has('existing')"))

    def test_history_left_click_still_resumes_original_account(self):
        self.history_fixture()
        self.clear_messages()
        self.page.locator("#hlist .hrow").nth(1).click()
        request = self.page.evaluate("sent.find(m => m.t === 'tab.resume')")
        self.assertEqual((request["sessionId"], request["account"]), ("second", "account-b"))

    def test_late_history_choices_do_not_reopen_dismissed_history(self):
        self.history_fixture()
        self.page.locator("#hclose").click()
        self.history_choices()
        self.assertFalse(self.page.locator("#swap").is_visible())


if __name__ == "__main__":
    unittest.main()
