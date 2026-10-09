// Translate application labels only. Never walk terminal output, input values or conversation text.
(() => {
  const dictionary = window.LLM_UI_EN || {};
  let language = 'ko';
  try { language = localStorage.getItem('llmtools.uiLanguage') === 'en' ? 'en' : 'ko'; } catch {}
  const format = (text, values) => text.replace(/\{(\d+)\}/g, (all, i) => i < values.length ? String(values[i] ?? '') : all);
  const patterns = Object.entries(dictionary).filter(([key]) => /\{\d+\}/.test(key)).map(([key, value]) => {
    const indexes = [];
    const pattern = key.split(/(\{\d+\})/).map(part => {
      if (/^\{\d+\}$/.test(part)) { indexes.push(Number(part.slice(1, -1))); return '([\\s\\S]*?)'; }
      return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }).join('');
    return { regex: new RegExp('^' + pattern + '$'), indexes, value };
  });
  window.tr = (source, ...values) => {
    const tagged = Array.isArray(source) && Object.hasOwn(source, 'raw');
    const key = tagged ? source.map((part, i) => part + (i < values.length ? '{' + i + '}' : '')).join('') : String(source ?? '');
    if (language !== 'en') return tagged || values.length ? format(key, values) : key;
    if (Object.hasOwn(dictionary, key)) return format(dictionary[key], values);
    // Host-provided status text uses the same templates, but arrives already formatted.
    if (!tagged && !values.length) for (const p of patterns) {
      const match = p.regex.exec(key);
      if (!match) continue;
      const args = []; p.indexes.forEach((index, i) => args[index] = match[i + 1]);
      return format(p.value, args);
    }
    return tagged || values.length ? format(key, values) : key;
  };
  const staticText = [], staticAttrs = [];
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node; (node = walker.nextNode());) {
    if (!node.parentElement?.closest('script, style') && /[가-힣]/.test(node.data)) staticText.push([node, node.data]);
  }
  document.body.querySelectorAll('[title], [placeholder], [aria-label]').forEach(el => {
    for (const name of ['title', 'placeholder', 'aria-label']) {
      const value = el.getAttribute(name);
      if (value && /[가-힣]/.test(value)) staticAttrs.push([el, name, value]);
    }
  });
  const paint = () => {
    document.documentElement.lang = language;
    for (const [node, source] of staticText) node.data = source.replace(/\S[\s\S]*\S|\S/, text => tr(text));
    for (const [el, name, source] of staticAttrs) el.setAttribute(name, tr(source));
  };
  window.UiLanguage = {
    get current() { return language; },
    set(value) {
      const next = value === 'en' ? 'en' : 'ko';
      const changed = next !== language; language = next;
      try { localStorage.setItem('llmtools.uiLanguage', language); } catch {}
      paint();
      return changed;
    },
  };
  paint();
})();
