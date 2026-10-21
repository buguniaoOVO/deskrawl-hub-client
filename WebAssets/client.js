(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const APP_VERSION = '1.0';
  const state = {
    view: 'overview', overview: null, steam: null, config: null,
    gameWindows: {}, timer: 0, busy: new Set(), lastError: '',
    updateSettings: { repositoryUrl: '', enabled: true, ignoredVersion: '', lastCheckedUtc: 0 },
    updateStatus: { kind: 'waiting', message: 'GitHub 地址待配置。' },
    globalFrameCap: { enabled: false, fps: 10 }, frameCapStatuses: {},
    wikiEquipment: { byId: new Map(), byEnglish: new Map(), byChinese: new Map(), count: 0 },
  };
  const viewNames = {
    overview: '账号状态', loot: '掉落记录', settings: '运行设置',
  };
  const api = async (path, options = {}) => {
    let response;
    try { response = await fetch(path, options); }
    catch { throw new Error('本机控制服务暂时没有响应。'); }
    const source = await response.text();
    let result;
    try { result = JSON.parse(source); }
    catch { result = { error: source.slice(0, 240) || `HTTP ${response.status}` }; }
    if (!response.ok) throw new Error(result.error || `请求失败（${response.status}）`);
    return result;
  };
  const post = (path, value) => api(path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value),
  });
  const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  })[c]);
  const icon = (name, cls = '') => `<svg class="${cls}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
  const number = (value) => Number(value || 0).toLocaleString('zh-CN');
  const when = (seconds, includeDate = false) => {
    if (!seconds) return '—';
    const date = new Date(Number(seconds) * 1000);
    return new Intl.DateTimeFormat('zh-CN', includeDate
      ? { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }
      : { hour: '2-digit', minute: '2-digit' }).format(date);
  };
  const ago = (seconds) => {
    const minutes = Math.max(0, Math.floor(Date.now() / 1000 - Number(seconds || 0)) / 60);
    if (minutes < 60) return `${Math.max(1, Math.floor(minutes))} 分钟前`;
    if (minutes < 1440) return `${Math.floor(minutes / 60)} 小时前`;
    return `${Math.floor(minutes / 1440)} 天前`;
  };
  const mapName = (character = {}) => {
    const stay = character.stay || {};
    const label = stay.map_name || stay.map || '';
    const stage = /(\d+)$/.exec(String(stay.map || ''));
    return label && stage && !String(label).includes(stage[1]) ? `${label} · ${stage[1]}` : label || '等待地图数据';
  };
  const sandboxName = (value) => {
    if (typeof value === 'string') return value.trim() || '沙盒外';
    if (value && typeof value === 'object') return String(value.sandbox || value.name || value.label || '').trim() || '沙盒外';
    return '沙盒外';
  };
  const normalizeItemName = (value) => String(value || '').normalize('NFKC')
    .replace(/[’‘`]/g, "'").replace(/[‐‑–—]/g, '-')
    .trim().toLocaleLowerCase().replace(/\s+/g, ' ');
  const wikiItem = (item = {}) => {
    const id = String(item.wikiId || item.item_id || item.id || item.key || '').replace(/^item:/i, '');
    const english = normalizeItemName(item.name_en || item.nameEn);
    const chinese = normalizeItemName(item.name);
    return (id && state.wikiEquipment.byId.get(id))
      || (english && state.wikiEquipment.byEnglish.get(english))
      || (chinese && state.wikiEquipment.byChinese.get(chinese))
      || null;
  };
  const displayItemName = (item = {}) => wikiItem(item)?.name || item.name || item.key || '物品';
  const isBlackMist = (item = {}) => !!item.black_mist
    || String(item.market_kind || '').toLowerCase() === 'black_mist'
    || String(item.kind || '').toLowerCase() === 'black_mist';
  const rarityName = (item = {}) => {
    if (isBlackMist(item)) return '黑雾';
    const wiki = wikiItem(item);
    if (wiki?.qualityName) return wiki.qualityName;
    const rarity = String(item.rarity_id || item.rarity || '').toLowerCase().replace(/[^a-z]/g, '');
    const labels = { common: '普通', uncommon: '优秀', rare: '稀有', legendary: '传奇', divine: '神圣', runeset: '符文套装', blackmist: '黑雾' };
    const known = labels[rarity];
    if (known) return known;
    const raw = String(item.rarity || item.rarity_id || '');
    return /[^\u0000-\u007f]/.test(raw) ? raw : '未分类';
  };
  const rarityClass = (item = {}) => {
    if (isBlackMist(item)) return 'black-mist';
    const rarity = String(wikiItem(item)?.rarity || item.rarity_id || item.rarity || '').toLowerCase().replace(/[^a-z]/g, '');
    return ({ common: 'common', uncommon: 'uncommon', rare: 'rare', legendary: 'legendary', divine: 'divine', runeset: 'runeset', blackmist: 'black-mist' })[rarity] || '';
  };
  const itemKindName = (item = {}) => {
    const kind = String(item.market_kind || item.kind || '').toLowerCase();
    const labels = {
      black_mist: '黑雾装备', divine: '神圣装备', rune_legendary: '传奇符文', rune_set: '符文套装',
      reins: '缰绳', gold: '金币', other: '其他', gear: '装备', equipment: '装备', rune: '符文',
      gem: '宝石', gems: '宝石', consumable: '消耗品', material: '材料', chest: '宝箱',
    };
    if (labels[kind]) return labels[kind];
    const raw = String(item.market_kind || item.kind || '');
    return /[^\u0000-\u007f]/.test(raw) ? raw : '其他';
  };
  const sourceName = (value) => {
    if (!value) return '—';
    const key = String(value || '').toLowerCase();
    const labels = { drop: '打怪掉落', gamble: '血脉赌博', chest: '开宝箱' };
    if (labels[key]) return labels[key];
    const raw = String(value || '');
    return /[^\u0000-\u007f]/.test(raw) ? raw : '其他';
  };
  const githubRepository = (value) => {
    try {
      let address = String(value || '').trim();
      if (!address) return null;
      if (!address.includes('://')) address = `https://github.com/${address.replace(/^\/+/, '')}`;
      const url = new URL(address);
      if (url.protocol !== 'https:' || !['github.com', 'www.github.com'].includes(url.hostname.toLowerCase())) return null;
      const parts = url.pathname.split('/').filter(Boolean);
      if (parts.length < 2) return null;
      const owner = decodeURIComponent(parts[0]);
      const repo = decodeURIComponent(parts[1]).replace(/\.git$/i, '');
      if (!/^[a-z0-9_-]+$/i.test(owner) || !/^[a-z0-9_.-]+$/i.test(repo)) return null;
      return { owner, repo, url: `https://github.com/${owner}/${repo}` };
    } catch { return null; }
  };
  const compareVersions = (left, right) => {
    const parse = (value) => String(value || '').replace(/^[vV]/, '').split(/[.+-]/).slice(0, 4).map((part) => Number.parseInt(part, 10) || 0);
    const a = parse(left), b = parse(right);
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const diff = (a[i] || 0) - (b[i] || 0);
      if (diff) return Math.sign(diff);
    }
    return 0;
  };
  function updateGithubButton() {
    const node = $('#github-button');
    if (!node) return;
    const repo = githubRepository(state.updateSettings.repositoryUrl);
    node.disabled = !repo;
    node.title = repo ? `打开 ${repo.owner}/${repo.repo} GitHub 项目页` : 'GitHub 项目地址待配置';
  }
  async function loadWikiEquipment() {
    try {
      const response = await fetch('/wiki-equipment.json', { cache: 'no-store' });
      if (!response.ok) return;
      const payload = await response.json();
      const items = Array.isArray(payload.items) ? payload.items : [];
      if (!items.length) return;
      const byId = new Map(), byEnglish = new Map(), byChinese = new Map();
      for (const item of items) {
        const id = String(item.id || '').replace(/^item:/i, '');
        const english = normalizeItemName(item.nameEn);
        const chinese = normalizeItemName(item.name);
        if (id) byId.set(id, item);
        if (english) byEnglish.set(english, item);
        if (chinese) byChinese.set(chinese, item);
      }
      state.wikiEquipment = { byId, byEnglish, byChinese, count: items.length, updatedAtUtc: payload.updatedAtUtc || 0 };
      if (state.view === 'overview' && state.overview) renderOverview();
      else if (state.view === 'loot' && state.steam) renderLootRows();
    } catch { /* API-provided names remain available when the Wiki cache is offline. */ }
  }
  const setBusy = (key, value) => value ? state.busy.add(key) : state.busy.delete(key);
  const activeInstances = () => state.overview?.instances || [];

  function notify(message, type = '') {
    const node = document.createElement('div');
    node.className = `toast ${type}`;
    node.textContent = message;
    $('#toast-root').append(node);
    setTimeout(() => node.remove(), 4200);
  }

  function setEngineState(online, message) {
    ['#top-led', '#side-led'].forEach((selector) => {
      const node = $(selector);
      node.className = `state-led${online ? ' online' : ''}`;
    });
    $('#top-status').textContent = message;
    $('#side-state').textContent = online ? '控制服务在线' : '正在连接';
  }

  function navigate(view) {
    if (!viewNames[view]) return;
    state.view = view;
    $$('.nav-item').forEach((button) => button.classList.toggle('active', button.dataset.view === view));
    $('#page-title').textContent = viewNames[view];
    renderCurrent();
    if (view === 'loot') loadSteam();
    if (view === 'settings') loadSettings();
  }

  function intro(kicker, title, description, actions = '') {
    return `<div class="page-intro"><div><div class="eyebrow">${esc(kicker)}</div><h1>${esc(title)}</h1><p>${esc(description)}</p></div>${actions ? `<div class="intro-actions">${actions}</div>` : ''}</div>`;
  }

  function button(action, label, tone = '', extra = '', symbol = '') {
    return `<button class="btn ${tone}" data-act="${action}" ${extra}>${symbol ? icon(symbol) : ''}${esc(label)}</button>`;
  }

  function renderCurrent() {
    if (state.view === 'overview') renderOverview();
    else if (state.view === 'loot') renderLoot();
    else if (state.view === 'settings') renderSettings();
  }

  function renderUpdatePanel() {
    const settings = state.updateSettings || {};
    const status = state.updateStatus || {};
    const repo = githubRepository(settings.repositoryUrl);
    const label = ({ checking: '正在检测', available: '发现新版本', latest: '已是最新', ignored: '已忽略此版本', error: '检测失败' })[status.kind] || '待配置';
    return `<section class="panel update-panel">
      <div class="panel-title"><div><h3>版本检测</h3><p>应用启动时检测，运行期间每 12 小时检测一次。</p></div><span class="state-pill ${status.kind === 'available' ? 'active' : status.kind === 'error' ? 'wait' : ''}">${label}</span></div>
      <div class="form-grid">
        <div class="form-field full"><label for="update-repo-url">GitHub 仓库地址</label><input class="field" id="update-repo-url" type="url" value="${esc(settings.repositoryUrl || '')}" placeholder="项目上传后填写 GitHub 项目页"><div class="update-repo-hint">上传项目并创建 Release 后，填写仓库主页或 Releases 地址即可启用检测。</div></div>
        <div class="form-field full"><label class="check-line"><input id="update-enabled" type="checkbox" ${settings.enabled !== false ? 'checked' : ''}>启动时自动检测，并每 12 小时检查一次</label></div>
      </div>
      <div class="update-status ${status.kind === 'available' ? 'available' : status.kind === 'error' ? 'error' : ''}">
        <span>${esc(status.message || '等待 GitHub 项目地址。')}</span>
        <span>当前版本 <b>v${APP_VERSION}</b>${status.latestVersion ? ` · 最新版本 <b class="update-version">${esc(status.latestVersion)}</b>` : ''}</span>
        ${status.releaseUrl ? `<a class="btn compact" href="${esc(status.releaseUrl)}" target="_blank" rel="noopener">查看发布页</a>` : ''}
      </div>
      <div class="update-actions">
        <button class="btn" data-act="update-check" ${repo ? '' : 'disabled'}>${icon('refresh')}立即检测</button>
        <button class="btn" data-act="update-ignore" ${status.kind === 'available' ? '' : 'hidden'}>不再提醒此版本</button>
        <button class="btn primary" data-act="update-settings-save">保存检测设置</button>
      </div>
    </section>`;
  }

  function renderGlobalFrameCapSettings() {
    const setting = state.globalFrameCap || { enabled: false, fps: 10 };
    return `<section class="panel frame-cap-settings-panel">
      <div class="panel-title"><div><h3>全局锁帧</h3><p>账号状态页的开关会统一应用到当前和后续启动的游戏。</p></div><span class="state-pill ${setting.enabled ? 'active' : ''}">${setting.enabled ? `已开启 · ${Number(setting.fps) || 10} 帧` : '已关闭'}</span></div>
      <div class="form-grid"><div class="form-field"><label for="global-frame-cap-rate">全局帧率上限</label><select class="select" id="global-frame-cap-rate" data-act="global-frame-cap-rate"><option value="10" ${Number(setting.fps) === 30 ? '' : 'selected'}>10 帧</option><option value="30" ${Number(setting.fps) === 30 ? 'selected' : ''}>30 帧</option></select></div><div class="form-field"><span class="form-hint">挡位变更会即时应用；关闭状态下只保存挡位。</span></div></div>
    </section>`;
  }

  function renderOverview() {
    const data = state.overview;
    if (!data) {
      $('#page-root').innerHTML = `<div class="loading-card"><span class="loader"></span><span>正在读取助手状态…</span></div>`;
      return;
    }
    const summary = data.summary || {};
    const instances = data.instances || [];
    const online = instances.filter((item) => item.paired).length;
    const auto = instances.filter((item) => item.paired && item.loop?.running).length;
    const games = instances.filter((item) => item.paired && item.game?.running).length;
    const runningGames = instances.filter((item) => item.game?.running);
    const allWindowsHidden = runningGames.length > 0 && runningGames.every((item) => state.gameWindows[String(item.port)] === 'hidden');
    const capEnabled = state.globalFrameCap.enabled === true;
    $('#page-root').innerHTML = `${intro('ACCOUNT STATUS / LIVE VIEW', '账号状态', '查看账号连接、游戏进程和近期战利品。')}
      <section class="hero-band">
        <div class="hero-orbit"></div>
        <div class="hero-copy"><div class="hero-kicker">LOCAL OPERATIONS / ${esc(new Date().toLocaleDateString('zh-CN'))}</div>
          <h2>${online ? `${number(online)} 个账号正在连接` : '等待助手接入'}</h2>
          <p>${online ? `${number(auto)} 个账号运行全自动，${number(games)} 个游戏进程在线。` : '检查实例目录与本机助手状态，连接后会显示运行详情。'}</p>
        </div>
      </section>
      <section class="metrics">
        ${metric('在线账号', `${number(summary.online ?? online)}<small>/${number(summary.paired ?? online)} 已配对</small>`, '本机助手连接数', 'accent', 'overview')}
        ${metric('全自动运行', `${number(summary.auto_on ?? auto)}<small>个账号</small>`, `游戏进程 ${number(summary.game_on ?? games)} 个`, '', 'ops')}
        ${metric('市场珍品', `${number(summary.steam_found)}<small>件</small>`, '已记录的可交易物品', 'copper', 'loot')}
        ${metric('近期掉落', `${number(summary.drops)}<small>条</small>`, `远古记录 ${number(summary.ancient)} 条`, '', 'box')}
      </section>
      <div class="section-head"><div class="section-head-main"><div class="section-head-titleline"><h2>账号状态</h2><div class="section-head-actions">
        <button class="btn compact" data-act="window-toggle-all" title="${allWindowsHidden ? '显示全部游戏窗口' : '隐藏全部游戏窗口'}" ${runningGames.length ? '' : 'disabled'}>${icon(allWindowsHidden ? 'eye' : 'eye-off')}${allWindowsHidden ? '显示全部窗口' : '隐藏全部窗口'}</button>
        <button class="btn compact ${capEnabled ? 'danger' : 'soft'}" data-act="global-frame-cap-toggle" aria-pressed="${capEnabled}" title="全局锁帧 ${capEnabled ? '已开启' : '已关闭'}">${icon(capEnabled ? 'stop' : 'spark')}${capEnabled ? '关闭全局锁帧' : '开启全局锁帧'}</button>
        </div><span class="count-tag">${instances.length} 个账号</span></div><p>${esc(globalFrameCapSummary())}</p></div></div>
      <section class="account-grid">${instances.length ? instances.map(renderAccount).join('') : empty('box', '没有发现账号', '到运行设置中检查扫描目录与端口范围。')}</section>`;
    $('#side-port').textContent = data.ports?.length ? `${data.ports[0]}—${data.ports[data.ports.length - 1]}` : '—';
    $('#side-version').textContent = data.versions?.hub ? `v${data.versions.hub}` : '本地';
    setEngineState(true, `${number(summary.online ?? online)} 个助手在线`);
  }

  function globalFrameCapSummary() {
    const settings = state.globalFrameCap;
    if (settings.enabled !== true) return '全局锁帧已关闭。';
    const statuses = Object.values(state.frameCapStatuses).filter((status) => Number(status.processId) > 0);
    if (!statuses.length) return `全局锁帧已开启，${Number(settings.fps) || 10} 帧；游戏启动后应用。`;
    const blocked = statuses.filter((status) => status.state === 'restart-required');
    if (blocked.length) return `全局锁帧已开启；${blocked.length} 个游戏需重启后应用。`;
    const failed = statuses.find((status) => !status.applied && status.state !== 'waiting');
    if (failed) return `全局锁帧未能应用：${failed.message || '请检查游戏状态。'}`;
    const applied = statuses.filter((status) => status.applied).length;
    const waiting = statuses.length - applied;
    return `全局锁帧 ${Number(settings.fps) || 10} 帧；${applied}/${statuses.length} 个游戏已确认${waiting ? `，${waiting} 个等待游戏就绪` : ''}。`;
  }

  function metric(label, value, note, tone, symbol) {
    return `<article class="metric ${tone}"><div class="metric-top"><span>${esc(label)}</span>${icon(symbol)}</div><div class="metric-value">${value}</div><div class="metric-note">${esc(note)}</div></article>`;
  }

  function renderAccount(account) {
    const port = Number(account.port || 0);
    const paired = !!account.paired;
    const running = !!account.loop?.running;
    const gameOn = !!account.game?.running;
    const hidden = state.gameWindows[String(port)] === 'hidden';
    const character = account.char || {};
    const paragon = character.paragon || {};
    const skulls = account.skulls || {};
    const inferno = skulls.SkullInferno?.count;
    const keyCount = skulls.TreasureMapKeyInferno?.count;
    const update = account.update || {};
    const market = account.steam?.market || [];
    const stateClass = !paired ? 'wait' : (gameOn ? 'game' : (running ? 'active' : ''));
    const stateText = !paired ? '未配对' : (gameOn ? '游戏在线' : (running ? '全自动运行' : '已连接'));
    const lootText = market.slice(0, 2).map((item) => {
      const cls = rarityClass(item);
      return `<span class="${cls ? `rarity-${cls}` : ''}">${esc(item.ancient ? '远古 ' : '')}${esc(displayItemName(item))}</span>`;
    }).join('<span class="muted"> · </span>') || '暂无掉落记录';
    const launchWait = account.game?.launch_waiting && !gameOn;
    const updateLabel = update.state === 'available' ? `更新 ${esc(update.version || '')}` : (update.state === 'latest' ? '已是最新' : '检查更新');
    const updateAction = update.state === 'available' ? 'update' : 'check-update';
    return `<article class="account-card ${paired ? '' : 'offline'}" data-port="${port}">
      <div class="account-head">
        <div class="account-avatar">${esc(String(port).slice(-3) || '—')}</div>
        <div class="account-title"><div class="account-name-row"><span class="account-name">${esc(account.label || `实例 ${port}`)}</span>
          <button class="rename-button" data-act="rename" data-port="${port}" title="修改备注">${icon('settings')}</button></div>
          <div class="account-meta"><span class="port-pill">PORT ${port || '—'}</span><span>${esc(sandboxName(account.sandbox))}</span>${account.helper?.version ? `<span>助手 ${esc(account.helper.version)}</span>` : ''}</div>
        </div>
        <span class="state-pill ${stateClass}">${esc(launchWait ? '游戏启动中' : stateText)}</span>
      </div>
      ${paired ? `<div class="account-body">
        <div class="data-cell"><div class="data-label">角色等级</div><div class="data-value large">${character.level == null ? '—' : `Lv.${number(character.level)}`}${paragon.level ? ` <small>· 巅峰 ${number(paragon.level)}</small>` : ''}</div></div>
        <div class="data-cell"><div class="data-label">当前地图</div><div class="data-value" title="${esc(mapName(character))}">${esc(mapName(character))}</div></div>
        <div class="data-cell"><div class="data-label">运行状态</div><div class="data-value ${running ? 'good' : ''}">${running ? '自动流程运行中' : '等待操作'}</div></div>
        <div class="data-cell"><div class="data-label">炼狱头骨</div><div class="data-value">${inferno == null ? '—' : number(inferno)}</div></div>
        <div class="data-cell"><div class="data-label">皇家钥匙</div><div class="data-value">${keyCount == null ? '—' : number(keyCount)}</div></div>
      <div class="data-cell"><div class="data-label">掉落记录</div><div class="data-value">${number(account.steam?.found)} 件</div></div>
      </div><div class="recent-loot"><strong>最近记录</strong>${lootText}</div>
      <div class="account-actions">
        ${button(running ? 'auto-off' : 'auto-on', running ? '停止自动' : '开启自动', running ? 'danger compact' : 'soft compact', `data-port="${port}"`, running ? 'stop' : 'play')}
        ${button(gameOn ? 'game-close' : 'game-open', gameOn ? '关闭游戏' : '启动游戏', gameOn ? 'danger compact' : 'compact', `data-port="${port}"`, 'game')}
        ${button(hidden ? 'window-show' : 'window-hide', hidden ? '显示窗口' : '隐藏窗口', 'compact', `data-port="${port}" ${gameOn ? '' : 'disabled'}`, hidden ? 'eye' : 'eye-off')}
        ${button(updateAction, updateLabel, update.state === 'available' ? 'copper compact' : 'compact', `data-port="${port}" data-version="${esc(update.version || '')}"`, 'download')}
        ${button('host', '部署接口', 'compact', `data-port="${port}"`, 'box')}
      </div>` : `<div class="offline-copy">${esc(account.error || account.install?.dir || '助手当前未连接。')} ${account.install?.dir ? '' : '检查沙盒中的助手进程。'}</div>
      <div class="account-actions">${account.install?.dir ? button('launch', '启动助手', 'soft compact', `data-port="${port}"`, 'play') : ''}</div>`}
    </article>`;
  }

  function empty(symbol, title, detail) {
    return `<div class="empty-state">${icon(symbol)}<strong>${esc(title)}</strong><span>${esc(detail)}</span></div>`;
  }

  function renderLoot() {
    const data = state.steam;
    if (!data) {
      $('#page-root').innerHTML = `${intro('RECORDS / GAME DROPS', '掉落记录', '汇总各账号上报的可交易物品与掉落来源。')}<div class="loading-card"><span class="loader"></span><span>正在读取掉落记录…</span></div>`;
      return;
    }
    const totals = data.totals || {};
    const rows = data.rows || [];
    const accounts = data.accounts || [];
    const kindCount = Object.values(totals).reduce((sum, item) => sum + Number(item?.k || 0), 0);
    const totalsHtml = `<section class="metrics">
      ${metric('记录总数', `${number(kindCount)}<small>件</small>`, `${number(data.account_count)} 个账号有记录`, 'accent', 'loot')}
      ${metric('市场黑雾', `${number(totals.black_mist?.k)}<small>件</small>`, `期望 ${(totals.black_mist?.expected || 0).toFixed(2)}`, '', 'spark')}
      ${metric('神圣装备', `${number(totals.divine?.k)}<small>件</small>`, `期望 ${(totals.divine?.expected || 0).toFixed(2)}`, 'copper', 'box')}
      ${metric('数据行', `${number(rows.length)}<small>条</small>`, '来自助手本地记录', '', 'overview')}
    </section>`;
    $('#page-root').innerHTML = `${intro('DROP LOG / MARKET ITEMS', '掉落记录', '搜索账号、物品、地图与首领名称。', button('export-csv', '导出 CSV', '', '', 'download'))}
      ${totalsHtml}<section class="panel"><div class="toolbar"><div class="toolbar-left"><div class="search-wrap">${icon('search')}<input class="field search" id="loot-search" type="search" placeholder="搜索物品、账号或地图"></div>
      <select class="select" id="loot-account"><option value="">全部账号</option>${accounts.map((item) => `<option value="${esc(item.port)}">${esc(item.account || `端口 ${item.port}`)}</option>`).join('')}</select>
      <select class="select" id="loot-period"><option value="all">全部时间</option><option value="7">最近 7 天</option><option value="30">最近 30 天</option></select></div>
      <div class="toolbar-right"><span class="count-tag" id="loot-count">${number(rows.length)} 条</span></div></div>
      <div class="data-table-wrap"><table class="data-table"><thead><tr><th>时间</th><th>账号</th><th>物品</th><th>类别</th><th>品质</th><th>来源</th><th>地图 / 首领</th></tr></thead><tbody id="loot-body"></tbody></table></div></section>`;
    renderLootRows();
    $('#loot-search').addEventListener('input', renderLootRows);
    $('#loot-account').addEventListener('change', renderLootRows);
    $('#loot-period').addEventListener('change', renderLootRows);
  }

  function renderLootRows() {
    if (!state.steam) return;
    const query = ($('#loot-search')?.value || '').trim().toLocaleLowerCase();
    const account = $('#loot-account')?.value || '';
    const period = Number($('#loot-period')?.value || 0);
    const lowerBound = period ? Date.now() / 1000 - period * 86400 : 0;
    const rows = (state.steam.rows || []).filter((row) => {
      if (account && String(row.port) !== account) return false;
      if (lowerBound && Number(row.t || 0) < lowerBound) return false;
      if (query) {
        const haystack = [row.name, row.name_en, row.account, row.char, row.map_name, row.boss].join(' ').toLocaleLowerCase();
        if (!haystack.includes(query)) return false;
      }
      return true;
    }).slice(0, 500);
    $('#loot-count').textContent = `${number(rows.length)} 条`;
    $('#loot-body').innerHTML = rows.length ? rows.map((row) => {
      const cls = rarityClass(row);
      const type = itemKindName(row);
      const flags = [row.ancient ? '远古' : '', isBlackMist(row) && rarityName(row) !== '黑雾' ? '黑雾' : ''].filter(Boolean).join(' · ');
      return `<tr><td class="mono">${esc(when(row.t, true))}</td><td>${esc(row.account || `端口 ${row.port || '—'}`)}</td>
        <td><span class="strong-cell ${cls ? `rarity-${cls}` : ''}">${esc(displayItemName(row))}</span>${row.name_en ? `<div class="muted-cell">${esc(row.name_en)}</div>` : ''}</td>
        <td><span class="rarity-tag">${esc(type)}</span></td><td><span class="rarity-tag ${cls}">${esc(rarityName(row))}${flags ? ` · ${esc(flags)}` : ''}</span></td>
        <td>${esc(sourceName(row.src))}${row.how ? ` · ${esc(row.how === 'auto' ? '自动' : '手动')}` : ''}</td>
        <td>${esc([row.map_name, row.boss].filter(Boolean).join(' / ') || '—')}</td></tr>`;
    }).join('') : `<tr><td colspan="7" class="muted-cell">没有符合条件的记录。</td></tr>`;
  }

  function renderSettings() {
    const config = state.config?.config;
    if (!config) {
      $('#page-root').innerHTML = `${intro('PREFERENCES / LOCAL INSTANCE', '运行设置', '管理实例扫描目录、端口范围与 Sandboxie 路径。')}<div class="loading-card"><span class="loader"></span><span>正在读取本机设置…</span></div>`;
      return;
    }
    const ports = config.ports || {};
    $('#page-root').innerHTML = `${intro('PREFERENCES / LOCAL INSTANCE', '运行设置', '管理实例扫描目录、端口范围与 Sandboxie 路径。', button('rescan', '重新扫描', '', '', 'refresh'))}
      ${renderGlobalFrameCapSettings()}
      ${renderUpdatePanel()}
      <section class="panel"><div class="panel-title"><div><h3>实例发现</h3><p>中控在这些目录查找助手实例与沙盒配置。</p></div><span class="count-tag">发现 ${number(state.config.installs?.length)} 个目录</span></div>
        <div class="form-grid">
          <div class="form-field full"><label for="cfg-roots">扫描目录（每行一个）</label><textarea class="textarea" id="cfg-roots">${esc((config.scan_roots || []).join('\n'))}</textarea></div>
          <div class="form-field"><label>起始控制端口</label><input class="field" id="cfg-port-start" type="number" value="${Number(ports.start || 8766)}"></div>
          <div class="form-field"><label>端口步长</label><input class="field" id="cfg-port-step" type="number" value="${Number(ports.step || 10)}"></div>
          <div class="form-field"><label>扫描端口数</label><input class="field" id="cfg-port-count" type="number" value="${Number(ports.count || 24)}"></div>
          <div class="form-field"><label>连接超时（秒）</label><input class="field" id="cfg-timeout" type="number" min="1" max="60" value="${Number(config.timeout || 4)}"></div>
          <div class="form-field full"><label for="cfg-sandbox">Sandboxie 安装目录</label><input class="field" id="cfg-sandbox" value="${esc(config.sandboxie_dir || '')}" placeholder="自动检测或填写安装目录"></div>
          <div class="form-field full"><label class="check-line"><input id="cfg-launch" type="checkbox" ${config.enable_launch !== false ? 'checked' : ''}>允许控制台启动离线助手</label></div>
        </div>
        <div class="form-actions"><button class="btn" data-act="rescan">扫描实例</button><button class="btn primary" data-act="settings-save">保存设置</button></div>
      </section>
      <div class="section-head"><div><h2>扫描结果</h2><p>配置目录中的助手实例。</p></div></div>
      <section class="panel">${renderInstallList(state.config.installs || [], state.config.sandboxie)}</section>
      <div class="section-head"><div><h2>账号备注</h2><p>备注会覆盖卡片上的端口名称。</p></div></div>
      <section class="panel"><div class="form-field"><label for="cfg-instances">高级配对数据（JSON）</label><textarea class="textarea code" id="cfg-instances">${esc(JSON.stringify(config.instances || {}, null, 2))}</textarea><div class="form-hint">保留端口到账号的现有映射。修改前请确认 JSON 格式正确。</div></div><div class="form-actions"><button class="btn primary" data-act="settings-advanced-save">保存配对数据</button></div></section>`;
  }

  function renderInstallList(installs, sandboxie) {
    const sb = sandboxie ? `<span class="state-pill active">Sandboxie 已找到</span>` : `<span class="state-pill wait">未检测到 Sandboxie</span>`;
    const rows = installs.map((item) => `<tr><td class="mono">${esc(item.control ?? '—')}</td><td>${esc(item.sandbox || '沙盒外')}</td><td>${esc(item.ver || '未知')}</td><td>${esc(item.dir || '—')}</td></tr>`).join('');
    return `<div class="toolbar"><span class="section-meta">${sb}</span><span class="section-meta">${installs.length} 个助手目录</span></div><div class="data-table-wrap"><table class="data-table"><thead><tr><th>控制端口</th><th>沙盒</th><th>版本</th><th>安装目录</th></tr></thead><tbody>${rows || '<tr><td colspan="4" class="muted-cell">尚未发现助手目录。</td></tr>'}</tbody></table></div>`;
  }

  async function loadOverview(force = false) {
    try {
      const data = await api(`/api/overview${force ? '?force=1' : ''}`);
      state.overview = data;
      state.lastError = '';
      setEngineState(true, `${number(data.summary?.online)} 个助手在线`);
      renderCurrent();
      loadGameWindows();
    } catch (error) {
      setEngineState(false, '本机助手无响应');
      if (!state.overview) {
        $('#page-root').innerHTML = `<div class="empty-state">${icon('ops')}<strong>暂时读不到助手状态</strong><span>${esc(error.message)} 过一会儿再试。</span>${button('refresh', '重新读取', 'primary', '', 'refresh')}</div>`;
      }
      if (state.lastError !== error.message) notify(error.message, 'error');
      state.lastError = error.message;
    }
  }

  async function loadSteam() {
    try { state.steam = await api('/api/steam'); renderCurrent(); }
    catch (error) { notify(`掉落记录读取失败：${error.message}`, 'error'); }
  }

  async function loadSettings() {
    try { state.config = await api('/api/config'); renderCurrent(); }
    catch (error) { notify(`运行设置读取失败：${error.message}`, 'error'); }
  }

  async function loadGameWindows() {
    try {
      const result = await api('/api/game-win');
      state.gameWindows = Object.fromEntries((result.accounts || []).map((row) => [String(row.port), row.state]));
      if (state.view === 'overview') renderOverview();
    } catch { /* Game window state is an optional display detail. */ }
  }

  async function toggleAllGameWindows() {
    await loadGameWindows();
    const games = activeInstances().filter((item) => item.game?.running);
    if (!games.length) {
      notify('当前没有运行中的游戏。', 'warn');
      return;
    }
    const allHidden = games.every((item) => state.gameWindows[String(item.port)] === 'hidden');
    const action = allHidden ? 'show' : 'hide';
    let affected = 0;
    for (const game of games) {
      const result = await post('/api/game-win', { action, port: Number(game.port) });
      if (result.ok === false) throw new Error(result.error || `账号 ${game.port} 的窗口操作失败。`);
      affected += Number(result.count || 0);
    }
    await loadGameWindows();
    notify(`${action === 'hide' ? '已隐藏' : '已恢复'} ${number(affected)} 个游戏窗口`);
  }

  async function runAction(action, buttonNode, domEvent = null) {
    const port = buttonNode?.dataset.port ? Number(buttonNode.dataset.port) : 0;
    const busyKey = `${action}:${port || 'single'}`;
    if (state.busy.has(busyKey)) return;
    setBusy(busyKey, true);
    if (buttonNode) buttonNode.disabled = true;
    try {
      if (action === 'exit-client') {
        window.chrome?.webview?.postMessage('exit-client');
        return;
      }
      if (action === 'sponsor') {
        window.open('https://afdian.com/a/Awan0v0', '_blank', 'noopener');
        return;
      }
      if (action === 'github-open') {
        const repo = githubRepository(state.updateSettings.repositoryUrl);
        if (repo) window.open(repo.url, '_blank', 'noopener');
        return;
      }
      if (action === 'update-check') {
        await checkForUpdates({ force: true, manual: true, quiet: false });
        return;
      }
      if (action === 'update-ignore') {
        ignoreCurrentUpdate();
        return;
      }
      if (action === 'update-settings-save') {
        saveUpdateSettings();
        return;
      }
      if (action === 'global-frame-cap-toggle') {
        requestGlobalFrameCap(state.globalFrameCap.enabled !== true, state.globalFrameCap.fps);
        return;
      }
      if (action === 'window-toggle-all') {
        await toggleAllGameWindows();
        return;
      }
      if (action === 'refresh') {
        await loadOverview(true);
        if (state.view === 'loot') await loadSteam();
        else if (state.view === 'settings') await loadSettings();
        return;
      }
      if (action === 'auto-on' || action === 'auto-off') {
        const result = await post('/api/action', {
          port, path: '/control/loop', action: action === 'auto-on' ? 'start' : 'stop',
        });
        notify(result.ok === false ? (result.error || '自动流程操作失败') : `${port} 号实例已提交操作`, result.ok === false ? 'error' : '');
      } else if (action === 'game-open' || action === 'game-close') {
        const isOpen = action === 'game-open';
        if (!isOpen && !confirm(`关闭 ${port} 号实例中的游戏？系统会先停止全自动。`)) return;
        const result = await post('/api/game', { port, action: isOpen ? 'launch' : 'close' });
        notify(result.ok === false ? (result.error || '游戏操作失败') : (result.result?.result || (isOpen ? '游戏启动请求已提交' : '游戏关闭请求已提交')), result.ok === false ? 'error' : '');
      } else if (action === 'window-hide' || action === 'window-show') {
        const next = action === 'window-hide' ? 'hide' : 'show';
        const result = await post('/api/game-win', { action: next, port });
        const count = result.count ?? 0;
        notify(`${next === 'hide' ? '已隐藏' : '已恢复'} ${number(count)} 个窗口`);
      } else if (action === 'launch') {
        const result = await post('/api/launch', { port });
        notify(result.ok ? (result.result || '助手启动请求已提交') : (result.error || '助手启动失败'), result.ok ? '' : 'error');
      } else if (action === 'check-update') {
        await post('/api/update', { action: 'check', port });
        notify(`${port} 号实例的版本检查已提交。`);
      } else if (action === 'update') {
        const version = buttonNode?.dataset.version || '';
        if (!confirm(`将 ${port} 号实例更新到 ${version || '当前可用版本'}？`)) return;
        const result = await post('/api/update', { action: 'apply', port, version });
        notify(result.ok ? '更新已提交。' : (result.error || '更新没有开始'), result.ok ? '' : 'error');
      } else if (action === 'host') {
        const result = await post('/api/host', { port });
        notify(result.ok ? '游戏内助手安装已提交。' : (result.error || '安装没有开始'), result.ok ? '' : 'error');
      } else if (action === 'rescan') {
        await api('/api/rescan');
        notify('实例扫描已完成。');
      } else if (action === 'rename') {
        await renameAccount(port);
        return;
      } else if (action === 'jump') {
        const item = activeInstances().find((row) => Number(row.port) === port);
        const address = item?.jump || `https://x1x.tw/deskrawl/helper/#port=${port}`;
        window.open(address, '_blank', 'noopener');
        return;
      } else if (action === 'export-csv') {
        exportCsv();
        return;
      } else if (action === 'settings-save') {
        await saveSettings(false);
        return;
      } else if (action === 'settings-advanced-save') {
        await saveSettings(true);
        return;
      }
      await refreshAll(false);
    } catch (error) {
      notify(error.message || '操作失败。', 'error');
    } finally {
      setBusy(busyKey, false);
      if (buttonNode?.isConnected) buttonNode.disabled = false;
    }
  }

  async function renameAccount(port) {
    const configResult = await api('/api/config');
    const instances = { ...(configResult.config?.instances || {}) };
    const current = { ...(instances[String(port)] || {}) };
    const account = activeInstances().find((item) => Number(item.port) === port);
    const name = prompt(`为 ${account?.label || `实例 ${port}`} 设置备注`, current.label || '');
    if (name === null) return;
    if (name.trim()) { current.label = name.trim(); instances[String(port)] = current; }
    else { delete current.label; if (Object.keys(current).length) instances[String(port)] = current; else delete instances[String(port)]; }
    const result = await post('/api/config', { instances });
    notify(result.warning || '账号备注已保存。');
    await refreshAll(true);
  }

  async function refreshAll(force) {
    await loadOverview(force);
    if (state.view === 'loot') await loadSteam();
  }

  async function saveSettings(advanced) {
    const result = await api('/api/config');
    let body;
    if (advanced) {
      let instances;
      try { instances = JSON.parse($('#cfg-instances').value || '{}'); }
      catch (error) { throw new Error(`配对数据格式错误：${error.message}`); }
      body = { instances };
    } else {
      body = {
        scan_roots: $('#cfg-roots').value.split(/\r?\n/).map((value) => value.trim()).filter(Boolean),
        ports: { start: Number($('#cfg-port-start').value), step: Number($('#cfg-port-step').value), count: Number($('#cfg-port-count').value) },
        timeout: Number($('#cfg-timeout').value),
        sandboxie_dir: $('#cfg-sandbox').value.trim(),
        enable_launch: $('#cfg-launch').checked,
      };
    }
    const saved = await post('/api/config', body);
    notify(saved.warning || '设置已保存。');
    await loadSettings();
  }

  function persistUpdateSettings() {
    updateGithubButton();
    window.chrome?.webview?.postMessage({ type: 'save-update-settings', settings: state.updateSettings });
  }

  function requestGlobalFrameCap(enabled, fps) {
    const rate = Number(fps) === 30 ? 30 : 10;
    state.globalFrameCap = { enabled: enabled === true, fps: rate };
    if (state.view === 'overview') renderOverview();
    window.chrome?.webview?.postMessage({ type: 'set-global-frame-cap', enabled: enabled === true, fps: rate });
  }

  function mergeGlobalFrameCapMessage(message) {
    if (message.settings && typeof message.settings === 'object') {
      state.globalFrameCap = { ...state.globalFrameCap, ...message.settings };
    }
    for (const status of message.statuses || []) {
      if (status?.port != null) state.frameCapStatuses[String(status.port)] = status;
    }
    if (state.view === 'overview') renderOverview();
    else if (state.view === 'settings' && state.config) renderSettings();
  }

  function onHostMessage(event) {
    const message = event.data || {};
    if (message.type === 'wiki-equipment-updated') {
      loadWikiEquipment();
      return;
    }
    if (message.type === 'frame-cap-settings-loaded'
      || message.type === 'frame-cap-settings-updated'
      || message.type === 'frame-cap-account-statuses') {
      mergeGlobalFrameCapMessage(message);
      if (message.type === 'frame-cap-settings-updated') {
        const failures = Object.values(state.frameCapStatuses).some((status) =>
          Number(status.processId) > 0 && !status.applied && status.state !== 'waiting');
        notify(globalFrameCapSummary(), failures ? 'error' : '');
      }
      return;
    }
    if (message.type === 'global-frame-cap-error') {
      notify(message.message || '全局锁帧设置没有保存。', 'error');
      return;
    }
    if (message.type !== 'update-settings-loaded' && message.type !== 'update-settings-saved') return;
    state.updateSettings = { ...state.updateSettings, ...(message.settings || {}) };
    updateGithubButton();
    if (!githubRepository(state.updateSettings.repositoryUrl)) {
      state.updateStatus = { kind: 'waiting', message: 'GitHub 仓库地址待配置。' };
    }
    if (state.view === 'settings') renderSettings();
    if (message.type === 'update-settings-loaded' && state.updateSettings.enabled && githubRepository(state.updateSettings.repositoryUrl)) {
      setTimeout(() => checkForUpdates({ force: true, quiet: true }), 350);
    }
  }

  function saveUpdateSettings() {
    const rawUrl = $('#update-repo-url')?.value.trim() || '';
    const repo = githubRepository(rawUrl);
    if (rawUrl && !repo) throw new Error('请输入有效的 github.com 仓库地址。');
    state.updateSettings = {
      ...state.updateSettings,
      repositoryUrl: repo?.url || '',
      enabled: $('#update-enabled')?.checked !== false,
    };
    if (!repo) state.updateStatus = { kind: 'waiting', message: 'GitHub 仓库地址待配置。' };
    persistUpdateSettings();
    renderSettings();
    notify('版本检测设置已保存。');
    if (repo && state.updateSettings.enabled) checkForUpdates({ force: true, quiet: true });
  }

  async function checkForUpdates({ force = false, manual = false, quiet = true } = {}) {
    const repo = githubRepository(state.updateSettings.repositoryUrl);
    if (!repo) {
      state.updateStatus = { kind: 'waiting', message: 'GitHub 仓库地址待配置。' };
      if (state.view === 'settings') renderSettings();
      return;
    }
    if (!state.updateSettings.enabled && !manual) return;
    const now = Math.floor(Date.now() / 1000);
    if (!force && now - Number(state.updateSettings.lastCheckedUtc || 0) < 12 * 60 * 60) return;

    state.updateStatus = { kind: 'checking', message: '正在读取 GitHub Releases…' };
    if (state.view === 'settings') renderSettings();
    try {
      const response = await fetch(`https://api.github.com/repos/${encodeURIComponent(repo.owner)}/${encodeURIComponent(repo.repo)}/releases/latest`, {
        headers: { Accept: 'application/vnd.github+json' },
      });
      if (!response.ok) throw new Error(`GitHub API 返回 ${response.status}`);
      const release = await response.json();
      const latestVersion = String(release.tag_name || '').trim();
      if (!latestVersion) throw new Error('Release 没有版本标签。');
      const releaseUrl = release.html_url || `${repo.url}/releases/latest`;
      const lastCheckedUtc = Math.floor(Date.now() / 1000);
      state.updateSettings = { ...state.updateSettings, lastCheckedUtc };
      persistUpdateSettings();

      const hasUpdate = compareVersions(latestVersion, APP_VERSION) > 0;
      const ignored = state.updateSettings.ignoredVersion === latestVersion;
      if (hasUpdate && ignored) {
        state.updateStatus = { kind: 'ignored', message: `已忽略版本 ${latestVersion}。`, latestVersion, releaseUrl };
      } else if (hasUpdate) {
        state.updateStatus = { kind: 'available', message: `发现新版本 ${latestVersion}。`, latestVersion, releaseUrl };
        notify(`桌破中控 v${latestVersion} 已发布，打开运行设置查看。`, 'warn');
      } else {
        state.updateStatus = { kind: 'latest', message: `当前版本 v${APP_VERSION} 已是最新版本。`, latestVersion, releaseUrl };
        if (!quiet) notify('当前已是最新版本。');
      }
    } catch (error) {
      state.updateSettings = { ...state.updateSettings, lastCheckedUtc: Math.floor(Date.now() / 1000) };
      persistUpdateSettings();
      state.updateStatus = { kind: 'error', message: `更新检测失败：${error.message}` };
      if (!quiet) notify(state.updateStatus.message, 'error');
    }
    if (state.view === 'settings') renderSettings();
  }

  function ignoreCurrentUpdate() {
    const version = state.updateStatus.latestVersion;
    if (!version) return;
    state.updateSettings = { ...state.updateSettings, ignoredVersion: version };
    persistUpdateSettings();
    state.updateStatus = { ...state.updateStatus, kind: 'ignored', message: `已忽略版本 ${version}。` };
    renderSettings();
    notify(`已不再提醒版本 ${version}。`);
  }

  function exportCsv() {
    const rows = state.steam?.rows || [];
    if (!rows.length) { notify('当前没有可导出的记录。'); return; }
    const columns = [
      ['时间', (row) => when(row.t, true)], ['账号', (row) => row.account], ['角色', (row) => row.char],
      ['物品', (row) => displayItemName(row)], ['英文名', (row) => row.name_en], ['类别', (row) => itemKindName(row)],
      ['品质', (row) => rarityName(row)], ['远古', (row) => row.ancient ? '是' : ''],
      ['黑雾', (row) => isBlackMist(row) ? '是' : ''], ['来源', (row) => sourceName(row.src)],
      ['地图', (row) => row.map_name], ['首领', (row) => row.boss], ['难度', (row) => row.diff],
      ['端口', (row) => row.port],
    ];
    const quote = (value) => `"${String(value ?? '').replace(/"/g, '""')}"`;
    const csv = [columns.map(([label]) => quote(label)).join(',')]
      .concat(rows.map((row) => columns.map(([, get]) => quote(get(row))).join(','))).join('\r\n');
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob(['\ufeff', csv], { type: 'text/csv;charset=utf-8' }));
    link.download = `桌破掉落记录-${new Date().toISOString().slice(0, 10)}.csv`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 1500);
    notify(`已导出 ${number(rows.length)} 条记录。`);
  }

  async function clockTick() {
    $('#top-time').textContent = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date());
  }

  document.addEventListener('click', async (event) => {
    const nav = event.target.closest('.nav-item');
    if (nav) { navigate(nav.dataset.view); return; }
    const actionButton = event.target.closest('[data-act]');
    if (actionButton) {
      event.preventDefault();
      await runAction(actionButton.dataset.act, actionButton, event);
    }
  });
  document.addEventListener('change', (event) => {
    const rateSelect = event.target.closest('select[data-act="global-frame-cap-rate"]');
    if (!rateSelect) return;
    requestGlobalFrameCap(state.globalFrameCap.enabled === true, Number(rateSelect.value));
  });
  document.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'r') {
      event.preventDefault(); runAction('refresh');
    }
  });
  $('#refresh-button').addEventListener('click', () => runAction('refresh'));
  updateGithubButton();
  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('message', onHostMessage);
    window.chrome.webview.postMessage({ type: 'load-update-settings' });
    window.chrome.webview.postMessage({ type: 'load-frame-cap-settings' });
  }
  loadWikiEquipment();
  $('.footer-version').textContent = `v${APP_VERSION}`;
  $('#top-time').textContent = '--:--:--';
  clockTick();
  setInterval(clockTick, 1000);
  loadOverview(false);
  state.timer = setInterval(() => {
    if (state.view === 'overview') loadOverview(false);
    else if (state.view === 'loot') loadSteam();
  }, 7000);
  setInterval(() => checkForUpdates({ force: true, quiet: true }), 12 * 60 * 60 * 1000);
})();
