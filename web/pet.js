// Shared format checks are also run against the normalized PNG by the native host.
(() => {
  const counts = Object.freeze([6, 8, 8, 4, 5, 8, 6, 6, 6, 8, 8]);
  const states = Object.freeze(['idle', 'running-right', 'running-left', 'waving', 'jumping', 'failed', 'waiting', 'running', 'review']);
  const labels = ['가만히 있기', '우측 이동', '좌측 이동', '인사', '점프', '실패 반응', '입력 기다리기', '작업 중', '결과 검토'];
  const maxBytes = 20 * 1024 * 1024, width = 1536, height = 2288;
  const translate = text => typeof window !== 'undefined' && window.tr ? window.tr(text) : text;
  const fail = text => { throw new Error(translate(text)); };
  const textAt = (b, n, length) => String.fromCharCode(...b.subarray(n, n + length));

  function inspect(bytes, mime) {
    if (!bytes.length || bytes.length > maxBytes) fail('이미지는 20 MiB 이하여야 합니다.');
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    let w = 0, h = 0;
    if (mime === 'image/png') {
      if (bytes.length < 33 || ![137, 80, 78, 71, 13, 10, 26, 10].every((n, i) => bytes[i] === n) || textAt(bytes, 12, 4) !== 'IHDR')
        fail('PNG / WebP 파일 형식이 올바르지 않습니다.');
      w = view.getUint32(16); h = view.getUint32(20);
      for (let p = 8; p + 12 <= bytes.length;) {
        const size = view.getUint32(p), type = textAt(bytes, p + 4, 4);
        if (size > bytes.length - p - 12) fail('이미지 파일이 손상되었습니다.');
        if (type === 'acTL') fail('애니메이션 파일 대신 정지 스프라이트 시트를 선택하세요.');
        p += size + 12;
      }
    } else if (mime === 'image/webp') {
      if (bytes.length < 20 || textAt(bytes, 0, 4) !== 'RIFF' || textAt(bytes, 8, 4) !== 'WEBP' || view.getUint32(4, true) !== bytes.length - 8)
        fail('PNG / WebP 파일 형식이 올바르지 않습니다.');
      const u24 = p => bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16;
      for (let p = 12; p + 8 <= bytes.length;) {
        const type = textAt(bytes, p, 4), size = view.getUint32(p + 4, true), d = p + 8;
        if (size > bytes.length - d) fail('이미지 파일이 손상되었습니다.');
        if (type === 'ANIM' || type === 'ANMF' || (type === 'VP8X' && size >= 10 && (bytes[d] & 2)))
          fail('애니메이션 파일 대신 정지 스프라이트 시트를 선택하세요.');
        if (type === 'VP8X' && size >= 10) { w = u24(d + 4) + 1; h = u24(d + 7) + 1; }
        if (!w && type === 'VP8L' && size >= 5 && bytes[d] === 0x2f) {
          const bits = view.getUint32(d + 1, true); w = (bits & 0x3fff) + 1; h = ((bits >>> 14) & 0x3fff) + 1;
        }
        if (!w && type === 'VP8 ' && size >= 10 && bytes[d + 3] === 0x9d && bytes[d + 4] === 1 && bytes[d + 5] === 0x2a) {
          w = view.getUint16(d + 6, true) & 0x3fff; h = view.getUint16(d + 8, true) & 0x3fff;
        }
        p = d + size + (size & 1);
      }
    } else fail('PNG / WebP 파일 형식이 올바르지 않습니다.');
    if (w !== width || h !== height) fail('이미지는 1536 × 2288픽셀이어야 합니다.');
    return { width: w, height: h };
  }

  function validatePixels(rgba) {
    if (rgba.length !== width * height * 4) fail('이미지 크기가 올바르지 않습니다.');
    for (let row = 0; row < 11; row++) for (let col = 0; col < 8; col++) {
      let visible = 0;
      for (let y = row * 208; y < (row + 1) * 208; y++) for (let x = col * 192; x < (col + 1) * 192; x++)
        if (rgba[(y * width + x) * 4 + 3]) visible++;
      if (col < counts[row] ? !visible || visible === 192 * 208 : visible !== 0)
        fail(`Frame ${row + 1}:${col + 1} — ` + translate(col < counts[row] ? '캐릭터와 투명 배경이 필요합니다.' : '사용하지 않는 칸은 완전히 투명해야 합니다.'));
    }
  }

  function loadImage(url) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      const timer = setTimeout(() => { img.src = ''; reject(new Error('이미지를 읽지 못했습니다.')); }, 15000);
      img.onload = () => { clearTimeout(timer); resolve(img); };
      img.onerror = () => { clearTimeout(timer); reject(new Error('이미지를 읽지 못했습니다.')); };
      img.src = url;
    });
  }

  async function normalize(bytes, mime) {
    inspect(bytes, mime);
    const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
    try {
      const img = await loadImage(url);
      if (img.naturalWidth !== width || img.naturalHeight !== height) fail('이미지는 1536 × 2288픽셀이어야 합니다.');
      const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height;
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      ctx.drawImage(img, 0, 0); validatePixels(ctx.getImageData(0, 0, width, height).data);
      const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
      if (!blob || blob.size > maxBytes) fail('이미지는 20 MiB 이하여야 합니다.');
      return new Uint8Array(await blob.arrayBuffer());
    } finally { URL.revokeObjectURL(url); }
  }

  const from64 = value => Uint8Array.from(atob(value), c => c.charCodeAt(0));
  const to64 = bytes => {
    const parts = [];
    for (let i = 0; i < bytes.length; i += 16384) parts.push(String.fromCharCode(...bytes.subarray(i, i + 16384)));
    return btoa(parts.join(''));
  };
  const api = { counts, states, inspect, validatePixels, normalize };
  if (typeof module !== 'undefined') module.exports = api;
  if (typeof window === 'undefined') return;
  window.PetFormat = api;
  const send = message => window.chrome?.webview?.postMessage(message);
  const tr = (...args) => window.tr(...args);
  let settings = {}, panel = null, image = null, loaded = '', requested = '', frame = 0, preview = 'idle', busy = false;
  let status = '', active = '', lastActivity = 0;
  let library = null, libraryItems = [], pendingDelete = '';
  const thumbnails = new Map();
  const button = (label, action) => {
    const el = document.createElement('button'); el.type = 'button'; el.className = 'btn'; el.textContent = tr(label);
    el.addEventListener('click', action); return el;
  };
  const report = message => { status = message; updatePanel(); };

  function updatePanel() {
    if (library?.open) {
      library.querySelector('[data-library-status]').textContent = tr(status);
      library.querySelector('[data-library-status]').setAttribute('aria-busy', String(busy));
      for (const b of library.querySelectorAll('[data-pet-mutate]'))
        b.disabled = busy || (b.dataset.petAction === 'select' && b.dataset.petId === settings.SelectedId && settings.Enabled);
    }
    if (!panel?.isConnected) return;
    panel.querySelector('[data-pet-name]').textContent = settings.Name || tr('등록된 펫이 없습니다.');
    panel.querySelector('[data-pet-status]').textContent = tr(status);
    panel.querySelector('[data-pet-status]').setAttribute('aria-busy', String(busy));
    for (const key of ['Enabled', 'Animate', 'FollowCursor']) panel.querySelector(`[data-pet-key="${key}"]`).checked = !!settings[key];
    panel.querySelector('[data-pet-key="Size"]').value = String(settings.Size || 75);
    panel.querySelector('[data-pet-key="Desktop"]').value = settings.Desktop ? 'desktop' : 'app';
    panel.querySelector('[data-pet-import]').disabled = busy;
    panel.querySelector('[data-pet-empty]').hidden = !!image;
    if (settings.FileName && requested !== settings.FileName && loaded !== settings.FileName) {
      requested = settings.FileName; send({ t: 'pet.get' });
    }
    paintPreview();
  }

  function mutate(message, text = '펫을 적용하는 중…') {
    busy = true; report(text); send(message);
  }

  function paintThumbnail(row, item) {
    const area = row.querySelector('.pet-library-thumb'); area.replaceChildren();
    const value = thumbnails.get(item.FileName);
    if (value?.data) {
      const img = document.createElement('img'); img.src = 'data:image/png;base64,' + value.data; img.alt = item.Name;
      area.append(img);
    } else {
      const text = document.createElement('span');
      text.textContent = tr(value?.error ? '미리보기를 읽지 못했습니다.' : '미리보기 불러오는 중…'); area.append(text);
    }
  }

  function paintLibrary() {
    if (!library?.open) return;
    const focus = document.activeElement?.dataset;
    const focusedId = focus?.petId, focusedAction = focus?.petAction;
    const list = library.querySelector('[data-pet-list]'); list.replaceChildren();
    library.querySelector('[data-library-count]').textContent = String(libraryItems.length);
    if (!libraryItems.length) {
      const empty = document.createElement('p'); empty.className = 'pet-library-empty';
      empty.textContent = tr('저장된 펫이 없습니다. 이미지 추가로 첫 펫을 등록하세요.'); list.append(empty);
    }
    for (const item of libraryItems) {
      const row = document.createElement('li'); row.dataset.petId = item.Id;
      const selected = item.Id === settings.SelectedId; row.classList.toggle('selected', selected);
      const thumb = document.createElement('div'); thumb.className = 'pet-library-thumb';
      const info = document.createElement('div'); info.className = 'pet-library-info';
      const name = document.createElement('strong'); name.textContent = item.Name; name.title = item.Name;
      const badge = document.createElement('span'); badge.className = 'pet-library-badge';
      badge.textContent = selected ? tr(settings.Enabled ? '사용 중' : '선택됨 · 숨김') : tr('저장됨');
      info.append(name, badge);
      const actions = document.createElement('div'); actions.className = 'pet-library-actions';
      const action = (label, kind, handler) => {
        const b = button(label, handler); b.dataset.petId = item.Id; b.dataset.petAction = kind; b.dataset.petMutate = '';
        b.setAttribute('aria-label', item.Name + ' — ' + tr(label)); actions.append(b);
      };
      if (pendingDelete === item.Id) {
        const warning = document.createElement('span'); warning.className = 'pet-library-confirm';
        warning.textContent = tr('이 펫을 삭제할까요?'); actions.append(warning);
        action('삭제 확인', 'confirm-delete', () => mutate({ t: 'pet.remove', itemId: item.Id }));
        action('취소', 'cancel-delete', () => { pendingDelete = ''; paintLibrary(); });
      } else {
        action('사용하기', 'select', () => mutate({ t: 'pet.select', itemId: item.Id }));
        action('이미지 교체', 'update', () => mutate({ t: 'pet.pick', itemId: item.Id }, '이미지를 선택하고 검사하는 중…'));
        action('삭제', 'delete', () => { pendingDelete = item.Id; paintLibrary(); });
      }
      row.append(thumb, info, actions); list.append(row); paintThumbnail(row, item);
    }
    updatePanel();
    if (focusedId && focusedAction) {
      const target = [...library.querySelectorAll('button')].find(b => b.dataset.petId === focusedId && b.dataset.petAction === focusedAction);
      if (target && !target.disabled) target.focus();
    }
  }

  function openLibrary() {
    if (library?.open) { library.focus(); return; }
    const dialog = document.createElement('dialog'); library = dialog; dialog.className = 'pet-library';
    dialog.setAttribute('aria-labelledby', 'pet-library-title');
    dialog.innerHTML = '<header><div><h2 id="pet-library-title"></h2><span data-library-description></span></div><span data-library-count></span></header><div class="pet-library-toolbar"></div><ul data-pet-list></ul><div data-library-status role="status"></div>';
    dialog.querySelector('h2').textContent = tr('내 펫 목록');
    dialog.querySelector('[data-library-description]').textContent = tr('이미지를 보관하고 사용할 펫을 선택하세요.');
    const add = button('이미지 추가', () => mutate({ t: 'pet.pick' }, '이미지를 선택하고 검사하는 중…'));
    add.dataset.petAdd = ''; add.dataset.petMutate = '';
    const close = button('닫기', () => dialog.close()); close.dataset.petLibraryClose = '';
    dialog.querySelector('.pet-library-toolbar').append(add, close);
    dialog.addEventListener('close', () => {
      dialog.remove(); if (library === dialog) library = null;
      pendingDelete = ''; send({ t: 'pet.list.close' });
    });
    document.body.append(dialog); dialog.showModal();
    libraryItems = settings.Items || []; pendingDelete = ''; paintLibrary(); add.focus(); send({ t: 'pet.list' });
  }

  function paintPreview() {
    const canvas = panel?.isConnected && panel.querySelector('canvas');
    if (!canvas) return;
    const ctx = canvas.getContext('2d'); ctx.clearRect(0, 0, 192, 208);
    if (!image) return;
    const direction = preview.startsWith('look:') ? Number(preview.slice(5)) : -1;
    const row = direction >= 0 ? 9 + Math.floor(direction / 8) : states.indexOf(preview);
    const col = direction >= 0 ? direction % 8 : settings.Animate === false ? 0 : frame % counts[row];
    ctx.drawImage(image, col * 192, row * 208, 192, 208, 0, 0, 192, 208);
  }
  setInterval(() => { if (!document.hidden && panel?.isConnected) { frame++; paintPreview(); } }, 120);

  window.PetUI = {
    apply(value) {
      settings = { Enabled: false, Size: 75, FollowCursor: true, Animate: true, Desktop: false, ...value };
      if (settings.FileName !== loaded) { image = null; loaded = ''; }
      updatePanel();
      if (library?.open) { pendingDelete = ''; send({ t: 'pet.list' }); }
    },
    render(parent) {
      if (library?.open) library.close();
      parent.querySelector('.grid')?.remove();
      panel = document.createElement('div'); panel.className = 'pet-settings';
      panel.innerHTML = '<div class="pet-preview"><canvas width="192" height="208" aria-label="Pet preview"></canvas><span data-pet-empty></span></div><div class="pet-options"><div data-pet-name></div><div class="pet-buttons"></div><div class="pet-controls"></div><div class="pet-actions"></div><div data-pet-status role="status"></div></div>';
      parent.append(panel);
      panel.querySelector('[data-pet-empty]').textContent = tr('PNG / WebP 이미지를 등록하세요.');
      panel.querySelector('canvas').setAttribute('aria-label', tr('펫 미리보기'));
      const buttons = panel.querySelector('.pet-buttons');
      const pick = button('이미지 등록 / 목록', openLibrary);
      pick.dataset.petImport = '';
      buttons.append(pick);
      const controls = panel.querySelector('.pet-controls');
      const modeLabel = document.createElement('label'); modeLabel.textContent = tr('표시 위치');
      const mode = document.createElement('select'); mode.dataset.petKey = 'Desktop';
      for (const [value, label] of [['app', '프로그램 안'], ['desktop', '바탕화면']]) {
        const option = document.createElement('option'); option.value = value; option.textContent = tr(label); mode.append(option);
      }
      mode.addEventListener('change', () => { settings.Desktop = mode.value === 'desktop'; send({ t: 'pet.save', v: settings }); });
      modeLabel.append(mode); controls.append(modeLabel);
      for (const [key, label] of [['Enabled', '펫 표시'], ['FollowCursor', '마우스 시선 추적'], ['Animate', '애니메이션']]) {
        const l = document.createElement('label'), input = document.createElement('input'); input.type = 'checkbox'; input.dataset.petKey = key;
        input.addEventListener('change', () => { settings[key] = input.checked; send({ t: 'pet.save', v: settings }); });
        l.append(input, document.createTextNode(tr(label))); controls.append(l);
      }
      const sizeLabel = document.createElement('label'); sizeLabel.textContent = tr('표시 크기');
      const size = document.createElement('select'); size.dataset.petKey = 'Size';
      for (const value of [50, 75, 100, 125, 150]) { const option = document.createElement('option'); option.value = String(value); option.textContent = value + '%'; size.append(option); }
      size.addEventListener('change', () => { settings.Size = Number(size.value); send({ t: 'pet.save', v: settings }); });
      sizeLabel.append(size); controls.append(sizeLabel);
      controls.append(button('위치 초기화', () => send({ t: 'pet.save', v: settings, resetPosition: true })));
      const actionLabel = document.createElement('label'); actionLabel.textContent = tr('동작 / 시선 미리보기');
      const actions = document.createElement('select');
      for (const [i, state] of states.entries()) { const o = document.createElement('option'); o.value = state; o.textContent = tr(labels[i]); actions.append(o); }
      for (let i = 0; i < 16; i++) { const o = document.createElement('option'); o.value = 'look:' + i; o.textContent = tr('시선') + ' ' + i * 22.5 + '°'; actions.append(o); }
      actions.value = preview;
      actions.addEventListener('change', () => { preview = actions.value; frame = 0; paintPreview(); if (states.includes(preview)) send({ t: 'pet.play', state: preview }); });
      actionLabel.append(actions); panel.querySelector('.pet-actions').append(actionLabel);
      const note = document.createElement('p'); note.className = 'note';
      note.textContent = tr('1536 × 2288 · 프레임 192 × 208 · 73프레임 · 투명 PNG / WebP · 최대 20 MiB. 설정은 즉시 저장됩니다.'); parent.append(note);
      const usage = document.createElement('p'); usage.className = 'note';
      usage.textContent = tr('클릭: 인사 · 두 번 클릭: 점프 · 드래그: 이동 · 우클릭: 9종 동작. 작업 중 표시는 CLI 입력·출력 활동에 반응하며 실제 작업 완료를 판정하지 않습니다.'); parent.append(usage);
      const desktopNote = document.createElement('p'); desktopNote.className = 'note';
      desktopNote.textContent = tr('바탕화면에서는 창 밖으로 이동할 수 있고 다른 창 위에 표시됩니다. 프로그램을 최소화해도 유지되며 종료하면 함께 닫힙니다.'); parent.append(desktopNote);
      updatePanel();
    },
    async handle(m) {
      if (m.t === 'pet.library') {
        libraryItems = m.items || []; settings.Items = libraryItems; settings.SelectedId = m.selectedId || '';
        const files = new Set(libraryItems.map(item => item.FileName));
        for (const file of thumbnails.keys()) if (!files.has(file)) thumbnails.delete(file);
        paintLibrary();
      } else if (m.t === 'pet.thumbnail') {
        const item = libraryItems.find(item => item.Id === m.itemId && item.FileName === m.file);
        if (!item) return;
        thumbnails.set(m.file, { data: m.data, error: m.error });
        const row = library?.querySelector('[data-pet-list]') && [...library.querySelectorAll('li')].find(row => row.dataset.petId === item.Id);
        if (row) paintThumbnail(row, item);
      } else if (m.t === 'pet.decode') {
        try { const png = await normalize(from64(m.data), m.mime); send({ t: 'pet.commit', id: m.id, data: to64(png) }); }
        catch (e) { busy = false; report(tr(e.message)); send({ t: 'pet.reject', id: m.id, error: tr(e.message) }); }
      } else if (m.t === 'pet.preview') {
        try {
          const next = await loadImage('data:image/png;base64,' + m.data);
          if (settings.FileName !== m.file) return;
          image = next; loaded = m.file; updatePanel();
        } catch (e) { requested = ''; report(tr(e.message)); }
      } else if (m.t === 'pet.settings') {
        busy = false; status = '펫 설정을 저장했습니다.'; this.apply(m.settings);
      } else if (m.t === 'pet.error') { busy = false; requested = settings.FileName || ''; report(m.message); }
      else if (m.t === 'pet.cancel') { busy = false; report(''); }
    },
    active(id) { active = id || ''; this.activity('idle'); },
    activity(state) { lastActivity = performance.now(); send({ t: 'pet.activity', state }); },
    terminal(m) {
      if (m.id !== active) return;
      if (m.t === 'term.error' || (m.t === 'term.exit' && m.code !== 0)) this.activity('failed');
      else if (m.t === 'term.exit') this.activity('review');
      else if (m.t === 'term.ready') this.activity('waiting');
      else if (m.t === 'term.starting' || (m.t === 'term.out' && m.data && performance.now() - lastActivity > 500)) this.activity('running');
    },
  };
})();
