(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const APP_VERSION = '1.2';
  const state = {
    view: 'overview', overview: null, steam: null, config: null,
    gameWindows: {}, timer: 0, busy: new Set(), lastError: '',
    workspaces: [], workspaceError: '', overviewLoading: false, workspaceLoading: null, hidden: [], hiddenError: '', hiddenLoading: null,
    lineage: {}, lineageLoading: null, lineageError: '',
    lineageReports: {}, lineageReportLoading: null, lineageReportError: '',
    auction: null, auctionLoading: null,
    lootFilters: { query: '', account: '', period: 'all' },
    auctionSettings: { enabled: false, intervalMinutes: 60, lastRunUtc: 0 },
    updateSettings: { repositoryUrl: '', enabled: true, ignoredVersion: '', lastCheckedUtc: 0 },
    updateStatus: { kind: 'waiting', message: 'GitHub 地址待配置。' },
    globalFrameCap: { enabled: false, fps: 10 }, frameCapStatuses: {},
    wikiEquipment: { byId: new Map(), byEnglish: new Map(), byChinese: new Map(), count: 0 },
  };
  const viewNames = {
    overview: '账号状态', loot: '掉落记录', lineage: '血脉鉴定', auction: '拍卖助手',
    helpers: '助手设置', settings: '运行设置',
  };
  // 每个页面在顶栏只留一行标题和一行说明，正文直接从数据开始。
  const viewMeta = {
    overview: '主机与各沙箱的游戏、助手与运行状态',
    loot: '各账号上报的可交易物品与掉落来源',
    lineage: '赌博、远古与黑雾运气，按助手统计口径折算百分位',
    auction: '巡查空位、从仓库取物并补满上架槽',
    helpers: '按环境管理 X1X 助手页面与配对信息',
    settings: '实例扫描、端口范围、锁帧与客户端更新',
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
  const hostRequests = new Map();
  let hostSequence = 0;
  function hostRequest(action, data = {}) {
    if (!window.chrome?.webview) return Promise.reject(new Error('此功能需要在桌面客户端中使用。'));
    return new Promise((resolve, reject) => {
      const requestId = String(++hostSequence);
      const timeout = setTimeout(() => { hostRequests.delete(requestId); reject(new Error('操作等待超时，请刷新状态后确认。')); }, 90000);
      hostRequests.set(requestId, { resolve, reject, timeout });
      window.chrome.webview.postMessage({ type: 'workspace-request', requestId, action, ...data });
    });
  }
  async function loadWorkspaces() {
    if (state.workspaceLoading) return state.workspaceLoading;
    state.workspaceLoading = hostRequest('list').then((rows) => {
      state.workspaces = Array.isArray(rows) ? rows : [];
      state.workspaceError = '';
    }).catch((error) => { state.workspaceError = error.message; })
      .finally(() => { state.workspaceLoading = null; });
    return state.workspaceLoading;
  }
  async function loadHidden(refresh = false) {
    if (state.hiddenLoading && !refresh) return state.hiddenLoading;
    state.hiddenLoading = hostRequest('hidden-list').then((rows) => {
      state.hidden = Array.isArray(rows) ? rows : [];
      state.hiddenError = '';
    }).catch((error) => { state.hiddenError = error.message; })
      .finally(() => { state.hiddenLoading = null; });
    return state.hiddenLoading;
  }
  function managedInstances() {
    const instances = state.overview?.instances || [];
    if (!state.workspaces.length) return instances;
    return state.workspaces.map((workspace) => {
      const item = instances.find((row) => Number(row.port) === workspace.controlPort
        && String(row.sandbox?.sandbox ?? row.install?.sandbox ?? '') === workspace.sandbox);
      return { ...(item || {}), port: workspace.controlPort, workspace,
        label: item?.label || workspace.name,
        paired: !!(workspace.helperRunning && item?.paired),
        sandbox: { sandbox: workspace.sandbox },
        game: { ...(item?.game || {}), running: workspace.gamePids.length > 0 },
      };
    });
  }
  async function loadLineage() {
    if (state.lineageLoading) return state.lineageLoading;
    state.lineageLoading = hostRequest('lineage-list').then((rows) => {
      state.lineage = Object.fromEntries((Array.isArray(rows) ? rows : []).map((row) => [row.id, row]));
      state.lineageError = '';
    }).catch((error) => { state.lineageError = error.message; })
      .finally(() => {
        state.lineageLoading = null;
        if (state.view === 'overview' && state.overview) renderOverview();
      });
    return state.lineageLoading;
  }
  const lineageLabel = (value) => ({
    '歐皇': '欧皇', '歐洲人': '欧洲人', '歐非混血': '欧非混血', '非洲人': '非洲人', '非洲酋長': '非洲酋长',
  })[String(value || '')] || String(value || '样本不足');
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
    if (typeof value === 'string') return value.trim() || '主机';
    if (value && typeof value === 'object') return String(value.sandbox || value.name || value.label || '').trim() || '主机';
    return '主机';
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
    $('#page-desc').textContent = viewMeta[view] || '';
    renderCurrent();
    if (view === 'loot') loadSteam();
    if (view === 'lineage') loadLineageReports();
    if (view === 'auction') loadAuctionInventory();
    if (view === 'settings') loadSettings();
    if (view === 'helpers') { loadWorkspaces().then(renderHelpers); loadHidden().then(() => { if (state.view === 'helpers') renderHelpers(); }); }
  }

  /* 页面结构件：正文不再重复页面标题，只用「条」和「组」组织内容。 */
  function statStrip(items) {
    return `<section class="stat-strip">${items.map((item) => `<div class="stat${item.tone ? ` ${item.tone}` : ''}">
      <span class="stat-label">${esc(item.label)}</span>
      <span class="stat-value">${item.value}</span>
      ${item.note ? `<span class="stat-note">${esc(item.note)}</span>` : ''}
    </div>`).join('')}</section>`;
  }

  function sectionBar(title, { count = '', actions = '', note = '' } = {}) {
    return `<div class="section-bar">
      <div class="section-bar-main"><h2>${esc(title)}</h2>${count ? `<span class="count-tag">${count}</span>` : ''}</div>
      ${actions ? `<div class="section-bar-actions">${actions}</div>` : ''}
    </div>${note ? `<p class="section-note">${note}</p>` : ''}`;
  }

  function button(action, label, tone = '', extra = '', symbol = '') {
    return `<button class="btn ${tone}" data-act="${action}" ${extra}>${symbol ? icon(symbol) : ''}${esc(label)}</button>`;
  }

  function renderCurrent() {
    if (state.view === 'overview') renderOverview();
    else if (state.view === 'loot') renderLoot();
    else if (state.view === 'lineage') renderLineage();
    else if (state.view === 'auction') renderAuction();
    else if (state.view === 'helpers') renderHelpers();
    else if (state.view === 'settings') renderSettings();
  }

  function updateStatePill() {
    const status = state.updateStatus || {};
    const label = ({ checking: '正在检测', available: '发现新版本', latest: '已是最新', ignored: '已忽略此版本', error: '检测失败' })[status.kind] || '待配置';
    return `<span class="state-pill ${status.kind === 'available' ? 'active' : status.kind === 'error' ? 'wait' : ''}">${label}</span>`;
  }

  function renderUpdatePanel() {
    const settings = state.updateSettings || {};
    const status = state.updateStatus || {};
    const repo = githubRepository(settings.repositoryUrl);
    return `<section class="panel">
      <div class="form-grid">
        <div class="form-field full"><label for="update-repo-url">GitHub 仓库地址</label><input class="field" id="update-repo-url" type="url" value="${esc(settings.repositoryUrl || '')}" placeholder="项目上传后填写 GitHub 项目页"><div class="form-hint">上传项目并创建 Release 后，填写仓库主页或 Releases 地址即可启用检测。</div></div>
        <div class="form-field full"><label class="check-line"><input id="update-enabled" type="checkbox" ${settings.enabled !== false ? 'checked' : ''}>启动时自动检测，并每 12 小时检查一次</label></div>
      </div>
      <div class="update-status ${status.kind === 'available' ? 'available' : status.kind === 'error' ? 'error' : ''}">
        <span>${esc(status.message || '等待 GitHub 项目地址。')}</span>
        <span>当前版本 <b>v${APP_VERSION}</b>${status.latestVersion ? ` · 最新版本 <b class="update-version">${esc(status.latestVersion)}</b>` : ''}</span>
        ${status.releaseUrl ? `<a class="btn compact" href="${esc(status.releaseUrl)}" target="_blank" rel="noopener">查看发布页</a>` : ''}
      </div>
      <div class="form-actions">
        <button class="btn" data-act="update-check" ${repo ? '' : 'disabled'}>${icon('refresh')}立即检测</button>
        <button class="btn" data-act="update-ignore" ${status.kind === 'available' ? '' : 'hidden'}>不再提醒此版本</button>
        <button class="btn primary" data-act="update-settings-save">保存检测设置</button>
      </div>
    </section>`;
  }

  function renderGlobalFrameCapSettings() {
    const setting = state.globalFrameCap || { enabled: false, fps: 10 };
    return `<section class="panel">
      <div class="form-field">
        <label for="global-frame-cap-rate">全局帧率上限</label>
        <div class="field-row">
          <select class="select" id="global-frame-cap-rate" data-act="global-frame-cap-rate"><option value="10" ${Number(setting.fps) === 30 ? '' : 'selected'}>10 帧</option><option value="30" ${Number(setting.fps) === 30 ? 'selected' : ''}>30 帧</option></select>
          <p class="form-hint">挡位变更会即时应用；关闭状态下只保存挡位。账号状态页的开关会统一应用到当前和后续启动的游戏。</p>
        </div>
      </div>
    </section>`;
  }

  function renderOverview() {
    const data = state.overview;
    if (!data) {
      $('#page-root').innerHTML = `<div class="loading-card"><span class="loader"></span><span>正在读取助手状态…</span></div>`;
      return;
    }
    // 7 秒一次的轮询会重绘整页；菜单展开时跳过，免得点到一半被收起来。
    if (document.querySelector('.env-more.open')) return;
    const summary = data.summary || {};
    const instances = managedInstances();
    const online = instances.filter((item) => item.paired).length;
    const auto = instances.filter((item) => item.paired && item.loop?.running).length;
    const games = instances.filter((item) => item.game?.running).length;
    const runningGames = instances.filter((item) => item.paired && item.game?.running);
    const allWindowsHidden = runningGames.length > 0 && runningGames.every((item) => state.gameWindows[String(item.port)] === 'hidden');
    const capEnabled = state.globalFrameCap.enabled === true;
    $('#page-root').innerHTML = `${statStrip([
      { label: '在线账号', value: `${number(summary.online ?? online)}<small>/${number(summary.paired ?? instances.length)}</small>`, note: '助手已连接 / 已配对', tone: 'accent' },
      { label: '全自动运行', value: number(summary.auto_on ?? auto), note: `游戏进程 ${number(games)} 个` },
      { label: '市场珍品', value: `${number(summary.steam_found)}<small>件</small>`, note: '已记录的可交易物品', tone: 'copper' },
      { label: '近期掉落', value: `${number(summary.drops)}<small>条</small>`, note: `远古记录 ${number(summary.ancient)} 条` },
    ])}
    ${state.workspaceError ? `<div class="callout danger">环境读取失败：${esc(state.workspaceError)}</div>` : ''}
    ${sectionBar('环境', {
      count: `${instances.length} 个`,
      note: capEnabled ? esc(globalFrameCapSummary()) : '',
      actions: `<button class="btn compact" data-act="window-toggle-all" title="${allWindowsHidden ? '显示全部游戏窗口' : '隐藏全部游戏窗口'}" ${runningGames.length ? '' : 'disabled'}>${icon(allWindowsHidden ? 'eye' : 'eye-off')}${allWindowsHidden ? '显示全部窗口' : '隐藏全部窗口'}</button>
        <button class="btn compact ${capEnabled ? 'danger' : ''}" data-act="global-frame-cap-toggle" aria-pressed="${capEnabled}" title="全局锁帧 ${capEnabled ? '已开启' : '已关闭'}">${icon(capEnabled ? 'stop' : 'spark')}${capEnabled ? `关闭全局锁帧 · ${Number(state.globalFrameCap.fps) || 10} 帧` : '开启全局锁帧'}</button>`,
    })}
    <section class="env-grid">${instances.length ? instances.map(renderAccount).join('') : empty('box', '没有发现账号', '到运行设置中检查扫描目录与端口范围。')}</section>
    ${renderRecentDrops()}`;
    $('#side-port').textContent = data.ports?.length ? `${data.ports[0]}—${data.ports[data.ports.length - 1]}` : '—';
    $('#side-version').textContent = data.versions?.hub ? `v${data.versions.hub}` : '本地';
    setEngineState(true, `${number(summary.online ?? online)} 个助手在线`);
  }

  /* 账号状态页的「近期掉落」：只是掉落记录页的缩影，不额外造数据。 */
  function renderRecentDrops() {
    const rows = (state.steam?.rows || []).slice(0, 6);
    if (!rows.length) return '';
    const body = rows.map((row) => {
      const cls = rarityClass(row);
      return `<tr>
        <td class="mono">${esc(when(row.t, true))}</td>
        <td>${esc(row.account || `端口 ${row.port || '—'}`)}</td>
        <td><span class="strong-cell ${cls ? `rarity-${cls}` : ''}">${esc(displayItemName(row))}</span></td>
        <td><span class="rarity-tag ${cls}">${esc(rarityName(row))}</span></td>
        <td class="muted-cell">${esc([row.map_name, row.boss].filter(Boolean).join(' / ') || '—')}</td>
      </tr>`;
    }).join('');
    return `${sectionBar('近期掉落', {
      count: `${number(state.steam?.rows?.length || 0)} 条`,
      actions: button('open-loot', '查看全部记录', 'compact', '', 'loot'),
    })}
    <section class="panel table-panel">
      <div class="data-table-wrap"><table class="data-table drops-table"><thead><tr><th>时间</th><th>账号</th><th>物品</th><th>品质</th><th>地图 / 首领</th></tr></thead><tbody>${body}</tbody></table></div>
    </section>`;
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

  /* 卡片的次级操作收进「更多」菜单：常驻的只有自动、游戏、窗口三个动词。 */
  function envMenu(port, workspace, update, paired) {
    const busy = ['checking', 'downloading', 'installing', 'applying', 'updating'].includes(String(update.state || ''));
    const progress = describeProgress(update.progress);
    const updateLabel = busy
      ? (update.state === 'checking' ? '正在检查版本' : `正在安装 ${progress}`.trim())
      : update.state === 'available' ? `更新助手到 ${esc(update.version || '新版本')}`
      : update.state === 'latest' ? '助手已是最新版本'
      : update.state === 'error' ? '重新检查助手版本' : '检查助手更新';
    const updateAct = update.state === 'available' ? 'update' : 'check-update';
    const wsAttr = workspace ? `data-workspace="${esc(workspace.id)}"` : '';
    const running = !!workspace?.helperRunning;
    return `<div class="env-more">
      <button class="btn icon-only" data-menu-toggle aria-haspopup="menu" aria-expanded="false" title="更多操作" aria-label="更多操作">${icon('chevron')}</button>
      <div class="menu" role="menu">
        <button class="menu-item" role="menuitem" data-act="${updateAct}" data-port="${port}" data-version="${esc(update.version || '')}" ${busy ? 'disabled' : ''}>
          ${icon('download')}<span><b>${updateLabel}</b><i>${paired ? '停自动 → 关游戏 → 安装 → 回游戏' : '按当前助手版本检查'}</i></span></button>
        <button class="menu-item" role="menuitem" data-act="host" data-port="${port}">${icon('box')}<span><b>部署游戏内助手</b><i>向该实例提交安装请求</i></span></button>
        <div class="menu-sep" role="separator"></div>
        <button class="menu-item" role="menuitem" data-act="workspace-helper" ${wsAttr} data-port="${port}" ${running ? '' : 'disabled'}>${icon('settings')}<span><b>打开助手页面</b><i>内嵌 X1X 设置页</i></span></button>
        <button class="menu-item" role="menuitem" data-act="workspace-launch-helper" ${wsAttr} data-port="${port}" ${!running && workspace?.helperDirectory ? '' : 'disabled'}>${icon('play')}<span><b>启动助手</b><i>该环境的 X1X 进程</i></span></button>
        <button class="menu-item" role="menuitem" data-act="workspace-browser" ${wsAttr} data-port="${port}" ${running ? '' : 'disabled'}>${icon('external')}<span><b>浏览器打开</b><i>携带该环境的配对信息</i></span></button>
        <button class="menu-item" role="menuitem" data-act="workspace-config" ${wsAttr} data-port="${port}">${icon('box')}<span><b>环境设置</b><i>助手目录、游戏路径与启动方式</i></span></button>
      </div>
    </div>`;
  }

  function renderAccount(account) {
    const port = Number(account.port || 0);
    const paired = !!account.paired;
    const running = !!account.loop?.running;
    const gameOn = !!account.game?.running;
    const hidden = state.gameWindows[String(port)] === 'hidden';
    const workspace = account.workspace;
    const portAttr = `data-port="${port}"`;
    const workspaceAttr = workspace ? `data-workspace="${esc(workspace.id)}"` : '';
    const character = account.char || {};
    const paragon = character.paragon || {};
    const skulls = account.skulls || {};
    const inferno = skulls.SkullInferno?.count;
    const keyCount = skulls.TreasureMapKeyInferno?.count;
    const update = account.update || {};
    const market = account.steam?.market || [];
    const lineage = workspace ? state.lineage[workspace.id] : null;
    const lineageValue = lineage
      ? `${lineageLabel(lineage.label)}${lineage.percentile == null ? '' : ` · ${Number(lineage.percentile).toFixed(1)}%`}`
      : (state.lineageError ? '暂不可用' : '读取中');
    const lineageNote = lineage?.samples?.length ? `样本：${lineage.samples.join(' · ')}`
      : (lineage?.error || (lineage?.character ? '累计样本不足' : '助手血脉鉴定'));
    const stateClass = !paired ? 'wait' : (gameOn ? 'game' : (running ? 'active' : ''));
    const stateText = !paired ? (gameOn ? '游戏在线 · 助手离线' : '助手未连接') : (gameOn ? '游戏在线' : (running ? '全自动运行' : '已连接'));
    const launchWait = account.game?.launch_waiting && !gameOn;
    const lootText = market.slice(0, 2).map((item) => {
      const cls = rarityClass(item);
      return `<span class="${cls ? `rarity-${cls}` : ''}">${esc(item.ancient ? '远古 ' : '')}${esc(displayItemName(item))}</span>`;
    }).join('<span class="sep">·</span>') || '暂无掉落记录';
    const badge = workspace ? (workspace.sandbox || '主机') : (String(port).slice(-2) || '—');
    const level = character.level == null ? '—' : `Lv.${number(character.level)}`;
    return `<article class="env-card ${paired ? 'is-online' : 'is-offline'}" ${portAttr}>
      <header class="env-head">
        <div class="env-badge">${esc(badge)}</div>
        <div class="env-id">
          <div class="env-name-row">
            <h3 class="env-name">${esc(account.label || `实例 ${port}`)}</h3>
            ${workspace ? `<button class="env-icon" data-act="workspace-config" ${workspaceAttr} title="环境设置" aria-label="环境设置">${icon('settings')}</button>` : ''}
            ${workspace ? `<button class="env-icon env-remove" data-act="hide-account" ${workspaceAttr} title="从账号状态页移除（配置保留，可在助手设置恢复）" aria-label="移除环境">${icon('close')}</button>` : ''}
          </div>
          <div class="env-meta">
            <span class="env-port">${port ? `PORT ${port}` : '待启动'}</span>
            <span>${esc(sandboxName(account.sandbox))}</span>
            ${account.helper?.version ? `<span>助手 ${esc(account.helper.version)}</span>` : ''}
          </div>
        </div>
        <span class="state-pill ${stateClass}">${esc(launchWait ? '游戏启动中' : stateText)}</span>
      </header>
      ${paired ? `<div class="env-stats">
        <div class="env-stat"><span class="env-stat-label">角色等级</span><b class="env-stat-value big">${level}${paragon.level ? `<small>巅峰 ${number(paragon.level)}</small>` : ''}</b></div>
        <div class="env-stat"><span class="env-stat-label">当前地图</span><b class="env-stat-value" title="${esc(mapName(character))}">${esc(mapName(character))}</b></div>
        <div class="env-stat"><span class="env-stat-label">炼狱头骨</span><b class="env-stat-value">${inferno == null ? '—' : number(inferno)}</b></div>
        <div class="env-stat"><span class="env-stat-label">皇家钥匙</span><b class="env-stat-value">${keyCount == null ? '—' : number(keyCount)}</b></div>
        <div class="env-stat"><span class="env-stat-label">掉落记录</span><b class="env-stat-value">${number(account.steam?.found)}<small>件</small></b></div>
        <div class="env-stat"><span class="env-stat-label">血脉鉴定</span><b class="env-stat-value ${lineage?.error ? 'wait' : 'violet'}" title="${esc(lineageNote)}">${esc(lineageValue)}</b><button class="link-button" data-act="open-lineage" ${workspaceAttr}>查看鉴定</button></div>
      </div>
      <div class="env-loot"><strong>最近记录</strong>${lootText}</div>
      <div class="env-actions">
        ${button(running ? 'auto-off' : 'auto-on', running ? '停止自动' : '开启自动', running ? 'danger' : 'soft', portAttr, running ? 'stop' : 'play')}
        ${button(gameOn ? 'game-close' : (workspace ? 'workspace-launch-game' : 'game-open'), gameOn ? '关闭游戏' : '启动游戏', gameOn ? '' : 'soft', `${portAttr} ${workspaceAttr}`, 'game')}
        ${button(hidden ? 'window-show' : 'window-hide', hidden ? '显示窗口' : '隐藏窗口', '', `${portAttr} ${gameOn ? '' : 'disabled'}`, hidden ? 'eye' : 'eye-off')}
        ${envMenu(port, workspace, update, paired)}
      </div>` : `<div class="env-notice">${esc(workspace?.notice || account.error || '助手当前未连接；游戏与助手可分别启动。')}</div>
      <div class="env-actions">
        ${workspace ? button('workspace-launch-helper', workspace.helperRunning ? '助手已运行' : '启动助手', workspace.helperRunning ? '' : 'soft', `${workspaceAttr} ${workspace.helperRunning || !workspace.helperDirectory ? 'disabled' : ''}`, 'play') : ''}
        ${button(gameOn ? 'game-close' : (workspace ? 'workspace-launch-game' : 'game-open'), gameOn ? '游戏已运行' : '启动游戏', gameOn ? '' : 'soft', `${portAttr} ${workspaceAttr} ${gameOn ? 'disabled' : ''}`, 'game')}
        ${envMenu(port, workspace, update, paired)}
      </div>`}
    </article>`;
  }

  function empty(symbol, title, detail) {
    return `<div class="empty-state">${icon(symbol)}<strong>${esc(title)}</strong><span>${esc(detail)}</span></div>`;
  }

  function renderLoot() {
    const data = state.steam;
    if (!data) {
      $('#page-root').innerHTML = `<div class="loading-card"><span class="loader"></span><span>正在读取掉落记录…</span></div>`;
      return;
    }
    // 轮询重绘会打断正在输入的筛选条件，交给下一次渲染。
    const focused = document.activeElement;
    if (focused && focused.closest?.('#page-root') && /^(INPUT|SELECT|TEXTAREA)$/.test(focused.tagName)) return;
    const totals = data.totals || {};
    const rows = data.rows || [];
    const accounts = data.accounts || [];
    const filters = state.lootFilters;
    const kindCount = Object.values(totals).reduce((sum, item) => sum + Number(item?.k || 0), 0);
    $('#page-root').innerHTML = `${statStrip([
      { label: '记录总数', value: `${number(kindCount)}<small>件</small>`, note: `${number(data.account_count)} 个账号有记录`, tone: 'accent' },
      { label: '市场黑雾', value: `${number(totals.black_mist?.k)}<small>件</small>`, note: `期望 ${(totals.black_mist?.expected || 0).toFixed(2)}` },
      { label: '神圣装备', value: `${number(totals.divine?.k)}<small>件</small>`, note: `期望 ${(totals.divine?.expected || 0).toFixed(2)}`, tone: 'copper' },
      { label: '数据行', value: `${number(rows.length)}<small>条</small>`, note: '来自助手本地记录' },
    ])}
    <section class="panel table-panel">
      <div class="table-toolbar">
        <div class="table-toolbar-filters">
          <div class="search-wrap">${icon('search')}<input class="field search" id="loot-search" type="search" placeholder="搜索物品、账号或地图" value="${esc(filters.query)}"></div>
          <select class="select" id="loot-account"><option value="">全部账号</option>${accounts.map((item) => `<option value="${esc(item.port)}" ${String(filters.account) === String(item.port) ? 'selected' : ''}>${esc(item.account || `端口 ${item.port}`)}</option>`).join('')}</select>
          <select class="select" id="loot-period"><option value="all" ${filters.period === 'all' ? 'selected' : ''}>全部时间</option><option value="7" ${filters.period === '7' ? 'selected' : ''}>最近 7 天</option><option value="30" ${filters.period === '30' ? 'selected' : ''}>最近 30 天</option></select>
        </div>
        <div class="table-toolbar-actions">
          <span class="count-tag" id="loot-count">${number(rows.length)} 条</span>
          ${button('export-csv', '导出 CSV', 'compact', '', 'download')}
        </div>
      </div>
      <div class="data-table-wrap"><table class="data-table"><thead><tr><th>时间</th><th>账号</th><th>物品</th><th>类别</th><th>品质</th><th>来源</th><th>地图 / 首领</th></tr></thead><tbody id="loot-body"></tbody></table></div>
    </section>`;
    renderLootRows();
    const syncFilters = () => {
      state.lootFilters = {
        query: $('#loot-search')?.value || '',
        account: $('#loot-account')?.value || '',
        period: $('#loot-period')?.value || 'all',
      };
      renderLootRows();
    };
    $('#loot-search').addEventListener('input', syncFilters);
    $('#loot-account').addEventListener('change', syncFilters);
    $('#loot-period').addEventListener('change', syncFilters);
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

  async function loadLineageReports(force = false) {
    if (state.lineageReportLoading) return state.lineageReportLoading;
    state.lineageReportLoading = hostRequest('lineage-report').then((rows) => {
      state.lineageReports = Object.fromEntries((Array.isArray(rows) ? rows : []).map((row) => [row.id, row]));
      state.lineageReportError = '';
    }).catch((error) => { state.lineageReportError = error.message; })
      .finally(() => {
        state.lineageReportLoading = null;
        if (state.view === 'lineage') renderLineage();
      });
    return state.lineageReportLoading;
  }

  const rarityLabel = (value) => ({
    Common: '普通', Uncommon: '优秀', Rare: '稀有', Legendary: '传奇', Divine: '神圣', RuneSet: '符文套装',
  })[String(value || '')] || String(value || '未分类');

  const percent = (value) => value == null || Number.isNaN(Number(value)) ? '—' : `${Number(value).toFixed(1)}%`;

  function renderLineage() {
    const reports = managedInstances().map((account) => {
      const workspace = account.workspace;
      const report = workspace ? state.lineageReports[workspace.id] : null;
      return { account, workspace, report };
    });
    const loaded = reports.filter((row) => row.report?.report).length;
    const verdicts = reports.filter((row) => row.report?.percentile != null);
    const average = verdicts.length
      ? verdicts.reduce((sum, row) => sum + Number(row.report.percentile), 0) / verdicts.length : null;
    const draws = reports.reduce((sum, row) => sum + Number(row.report?.report?.gamble?.all?.draws || 0), 0);
    const ancient = reports.reduce((sum, row) => sum
      + Number(row.report?.report?.drops?.ancient?.k ?? row.report?.report?.families?.ancient?.k ?? 0), 0);
    $('#page-root').innerHTML = `${statStrip([
      { label: '已鉴定账号', value: `${number(verdicts.length)}<small>/${number(reports.length)}</small>`, note: loaded ? '助手已返回鉴定结果' : '正在读取助手记录', tone: 'accent' },
      { label: '平均百分位', value: `${average == null ? '—' : average.toFixed(1)}<small>%</small>`, note: '仅统计已有结论的账号', tone: 'copper' },
      { label: '赌博次数', value: `${number(draws)}<small>次</small>`, note: '各账号累计抽取' },
      { label: '远古掉落', value: `${number(ancient)}<small>件</small>`, note: '有样本的传奇装备' },
    ])}
      ${state.lineageReportError ? `<div class="callout danger">血脉记录读取失败：${esc(state.lineageReportError)}</div>` : ''}
      ${sectionBar('账号鉴定', { count: `${reports.length} 个环境`, actions: button('lineage-refresh', '刷新鉴定', 'compact', '', 'refresh') })}
      <section class="lineage-grid">${reports.map((row) => renderLineageCard(row)).join('')}</section>`;
  }

  function renderLineageCard({ account, workspace, report }) {
    const data = report?.report;
    const name = report?.name || workspace?.name || account.label || `端口 ${account.port}`;
    const character = data?.char || report?.character || '';
    const label = lineageLabel(report?.label || data?.verdict?.label);
    const percentile = report?.percentile ?? data?.verdict?.pct;
    const error = report?.error || (!workspace ? '此账号未配置环境。' : '');
    if (!data) {
      return `<article class="panel lineage-card"><div class="lineage-head"><div><h3>${esc(name)}</h3><div class="lineage-sub">${esc(character || '等待角色数据')}</div></div><span class="state-pill ${error ? 'wait' : ''}">${esc(error || '读取中')}</span></div>
        <p class="lineage-empty">${esc(error || '正在读取该账号的血脉记录…')}</p></article>`;
    }
    const families = data.families || {};
    const familyList = [['gamble', '赌博出货'], ['ancient', '远古装备'], ['black_mist', '黑雾装备']]
      .map(([key, title]) => {
        const family = families[key] || {};
        return `<div class="luck-cell"><div class="luck-label">${title}</div><div class="luck-value">${percent(family.pct)}</div>
          <div class="luck-note">${number(family.k)} / 期望 ${Number(family.expected || 0).toFixed(2)}</div></div>`;
      }).join('');
    const rarities = (data.gamble?.all?.rarities || []).map((row) => `<tr><td><span class="rarity-tag ${String(row.rarity || '').toLowerCase()}">${esc(rarityLabel(row.rarity))}</span></td>
      <td>${number(row.k)}</td><td>${(Number(row.p || 0) * 100).toFixed(2)}%</td><td>${(Number(row.k || 0) * 100 / Math.max(1, Number(data.gamble?.all?.draws || 1))).toFixed(2)}%</td></tr>`).join('');
    const market = data.market_luck?.kinds || {};
    const marketRows = Object.entries(market).map(([key, value]) => `<tr><td>${esc(marketKindLabel(key))}</td><td>${number(value.k)}</td><td>${Number(value.expected || 0).toFixed(2)}</td></tr>`).join('');
    const finds = (data.finds || []).slice(0, 8).map((item) => `<li><span class="${rarityClass(item) ? `rarity-${rarityClass(item)}` : ''}">${esc(displayItemName(item))}</span><span class="muted">${esc(rarityLabel(item.rarity))}${item.ancient ? ' · 远古' : ''}${item.black_mist ? ' · 黑雾' : ''}</span></li>`).join('');
    const drawsList = (data.draws || []).slice(0, 8).map((item) => `<li><span class="${rarityClass(item) ? `rarity-${rarityClass(item)}` : ''}">${esc(displayItemName(item))}</span><span class="muted">${esc(when(item.t, true))}</span></li>`).join('');
    return `<article class="panel lineage-card">
      <div class="lineage-head"><div><h3>${esc(name)}</h3><div class="lineage-sub">${esc(character || '等待角色')} · ${esc(sandboxName(account.sandbox))}</div></div>
        <span class="state-pill ${error ? 'wait' : 'active'}">${esc(label)}${percentile == null ? '' : ` · ${percent(percentile)}`}</span></div>
      <div class="luck-meter"><div class="luck-meter-fill" style="width:${Math.max(2, Math.min(100, Number(percentile) || 0))}%"></div></div>
      <div class="luck-grid">${familyList}</div>
      <div class="lineage-meta"><span>累计赌博 ${number(data.gamble?.all?.draws)} 次</span><span>出货 ${number(families.gamble?.k)} 件</span><span>远古 ${number(families.ancient?.k)} 件</span><span>黑雾 ${number(families.black_mist?.k)} 件</span><span>魔法发现 ${data.mf == null ? '—' : Number(data.mf).toFixed(2)}</span></div>
      <details class="lineage-details"><summary>品质分布与市场运气</summary>
        <div class="lineage-columns"><div><h4>赌博品质</h4><div class="data-table-wrap"><table class="data-table compact"><thead><tr><th>品质</th><th>数量</th><th>概率</th><th>占比</th></tr></thead><tbody>${rarities || '<tr><td colspan="4" class="muted-cell">暂无记录</td></tr>'}</tbody></table></div></div>
        <div><h4>可交易珍品</h4><div class="data-table-wrap"><table class="data-table compact"><thead><tr><th>类别</th><th>实得</th><th>期望</th></tr></thead><tbody>${marketRows || '<tr><td colspan="3" class="muted-cell">暂无记录</td></tr>'}</tbody></table></div></div></div>
      </details>
      <details class="lineage-details"><summary>最近珍品与赌博记录</summary><div class="lineage-columns">
        <div><h4>珍品记录</h4><ul class="lineage-list">${finds || '<li class="muted">暂无珍品</li>'}</ul></div>
        <div><h4>赌博记录</h4><ul class="lineage-list">${drawsList || '<li class="muted">暂无赌博</li>'}</ul></div></div></details>
    </article>`;
  }

  const marketKindLabel = (value) => ({
    black_mist: '黑雾装备', divine: '神圣装备', rune_legendary: '传奇符文', rune_set: '符文套装', reins: '缰绳', gold: '金币',
  })[String(value || '')] || String(value || '其他');

  function loadAuctionInventory(force = false) {
    if (state.auctionLoading) return state.auctionLoading;
    state.auctionLoading = hostRequest('auction-inventory').then((result) => {
      state.auction = result;
      if (state.view === 'auction') renderAuction();
    }).catch((error) => {
      notify(`拍卖候选读取失败：${error.message}`, 'error');
    }).finally(() => { state.auctionLoading = null; });
    return state.auctionLoading;
  }

  const areaLabel = (value) => ({ Town: '城镇中', Combat: '战斗中', Fishing: '钓鱼中' })[String(value || '')] || (value || '位置未知');

  function renderAuction() {
    const data = state.auction;
    if (!data) {
      $('#page-root').innerHTML = `<div class="loading-card"><span class="loader"></span><span>正在检查助手能力与可交易物品…</span></div>`;
      return;
    }
    const accounts = data.accounts || [];
    const candidates = accounts.reduce((sum, row) => sum + (row.items?.length || 0), 0);
    const supported = data.supported === true;
    const notice = supported ? '' : `<section class="callout notice">
      <div class="callout-head">${icon('auction')}<h3>自动上架暂不可用</h3></div>
      <p>当前 X1X 助手与游戏内助手没有提供拍卖行查询、空位读取和上架接口。客户端无法在不模拟点击、不伪造队列的前提下完成「暂停自动流程 → 回城 → 从仓库取出珍品 → 填满上架空位 → 恢复自动」。</p>
      <p>为避免误操作账号或错误上架，1.2 只做能力检测，不提交任何上架请求。接口开放后此处会直接启用间隔巡查、仓库取物、上架与自动恢复，现有设置和运行记录都会保留。下方候选清单按拍卖行可交易品质筛选，方便你先核对手上的珍品。</p>
    </section>`;
    $('#page-root').innerHTML = `${statStrip([
      { label: '可上架候选', value: `${number(candidates)}<small>件</small>`, note: '待游戏内核对可交易标签', tone: 'accent' },
      { label: '在线账号', value: `${number(accounts.filter((row) => !row.error).length)}<small>个</small>`, note: '已读取助手与游戏数据' },
      { label: '自动上架', value: supported ? '可用' : '待接口', note: supported ? '助手已提供拍卖行接口' : '当前助手缺少拍卖行接口', tone: supported ? 'copper' : '' },
      { label: '巡查间隔', value: '60<small>分钟</small>', note: '建议与游戏自动存档错开' },
    ])}
      ${notice}
      ${sectionBar('候选物品', {
        count: `${number(candidates)} 件`,
        actions: button('auction-refresh', '重新扫描', 'compact', '', 'refresh'),
        note: '按品质与物品类型初筛：黑雾装备、神圣装备、符文套装、传奇符文与传奇缰绳；已锁定、已强化、已镶嵌或未铸造的物品不列入。最终能否上架仍以游戏物品提示中的可交易标签为准。',
      })}
      <section class="auction-grid">${accounts.map(renderAuctionAccount).join('') || empty('auction', '没有可用账号', '请先在账号状态启动助手与游戏。')}</section>
      <details class="panel auction-flow"><summary>自动上架流程（接口就绪后启用）</summary><ol class="flow-list">
        <li>到达巡查时间后，读取拍卖行已上架数量与剩余空位。</li>
        <li>空位大于 0 时，向对应助手发送停止全自动请求，并记录此前的运行状态。</li>
        <li>等待角色回到城镇，读取仓库与背包中的可交易珍品。</li>
        <li>按品质与物品等级依次取出，放入拍卖行上架槽并确认。</li>
        <li>上架完成后恢复此前开启的全自动与窗口状态；失败则回滚物品并提示。</li></ol></details>`;
  }

  function renderAuctionAccount(row) {
    if (row.error) {
      return `<article class="panel auction-card"><div class="panel-title"><div><h3>${esc(row.name || `端口 ${row.port}`)}</h3><p>${esc(row.character || '等待角色')}</p></div><span class="state-pill wait">${esc(row.error)}</span></div></article>`;
    }
    const items = row.items || [];
    const rows = items.slice(0, 40).map((item) => `<tr>
      <td><span class="strong-cell ${rarityClass(item) ? `rarity-${rarityClass(item)}` : ''}">${esc(displayItemName(item))}</span></td>
      <td><span class="rarity-tag ${rarityClass(item)}">${esc(rarityLabel(item.rarity))}${item.blackMist ? ' · 黑雾' : item.ancient ? ' · 远古' : ''}</span></td>
      <td>${esc(itemKindName({ kind: item.kind }))}</td><td>${esc(item.where)}${item.page ? ` · 第 ${Number(item.page) + 1} 页` : ''}</td>
      <td class="mono">${Number(item.itemLevel) || '—'}</td></tr>`).join('');
    return `<article class="panel auction-card">
      <div class="panel-title"><div><h3>${esc(row.name || `端口 ${row.port}`)}</h3><p>${esc(row.character || '等待角色')} · ${esc(areaLabel(row.area))}${row.mode === 'Online' ? '' : ' · 离线角色无法交易'}</p></div><span class="state-pill ${items.length && row.mode !== 'Local' ? 'active' : ''}">${number(items.length)} 件候选</span></div>
      ${row.mode && row.mode !== 'Online' ? '<p class="lineage-empty">此角色为离线角色，拍卖行只接受在线角色交易。</p>' : ''}
      ${items.length ? `<div class="data-table-wrap"><table class="data-table compact"><thead><tr><th>物品</th><th>品质</th><th>类别</th><th>所在</th><th>物品等级</th></tr></thead><tbody>${rows}</tbody></table></div>
        ${items.length > 40 ? `<p class="form-hint">仅显示前 40 件，共 ${number(items.length)} 件。</p>` : ''}`
        : (row.mode && row.mode !== 'Online' ? '' : '<p class="lineage-empty">没有符合上架条件的物品。</p>')}
    </article>`;
  }

  function renderEnvPanel() {
    const rows = state.workspaces.map((row) => `<div class="env-row">
      <div class="env-row-main"><strong>${esc(row.name)}</strong><span class="env-path">${esc(row.helperDirectory || '助手目录待选择')}</span></div>
      ${button('workspace-config', '环境设置', 'compact', `data-workspace="${esc(row.id)}"`, 'settings')}
    </div>`).join('');
    return `<section class="panel env-panel">${rows || '<p class="env-empty">正在读取账号环境…</p>'}
      ${state.workspaceError ? `<div class="callout danger">${esc(state.workspaceError)}</div>` : ''}</section>`;
  }

  function renderHiddenPanel() {
    const rows = state.hidden.map((row) => `<div class="env-row">
      <div class="env-row-main"><strong>${esc(row.name)}</strong><span class="env-path">${esc(row.helperDirectory || '未选择助手目录')}</span></div>
      <div class="env-row-actions">
        <span class="tag ${row.paired ? '' : 'muted'}">${row.paired ? '已配对' : '未配对'}</span>
        ${button('unhide-account', '加回账号状态', 'soft compact', `data-workspace="${esc(row.id)}"`, 'eye')}
      </div>
    </div>`).join('');
    return `<section class="panel env-panel">
      <div class="env-add">
        <input class="field" id="env-new-sandbox" placeholder="Sandboxie 沙盘名，例如 5；留空表示主机">
        ${button('add-account', '手动添加环境', 'compact', '', 'box')}
      </div>
      <div class="env-list">${rows || '<p class="env-empty">没有隐藏的环境。账号卡片右上角的 × 可以把扫描到但不需要的沙箱移出列表。</p>'}</div>
      ${state.hiddenError ? `<div class="callout danger">隐藏环境读取失败：${esc(state.hiddenError)}</div>` : ''}
    </section>`;
  }

  function renderHelpers() {
    if (state.view !== 'helpers') return;
    $('#page-root').innerHTML = `${sectionBar('助手环境', {
      count: `${state.workspaces.length} 个`,
      note: '内嵌页面由 X1X 官网提供，需要联网；「浏览器打开」会重新携带该助手的配对信息。',
    })}
      ${state.workspaceError ? `<div class="callout danger">${esc(state.workspaceError)}</div>` : ''}
      <section class="helper-grid">${state.workspaces.map((row) => `<article class="panel helper-card">
        <div class="helper-head">
          <div class="helper-id"><h3>${esc(row.name)}</h3><p class="workspace-path">${esc(row.helperDirectory || '请先选择助手目录')}</p></div>
          <span class="state-pill ${row.helperRunning ? 'active' : 'wait'}">${row.helperRunning ? '助手运行中' : '助手未启动'}</span>
        </div>
        ${row.notice ? `<div class="callout">${esc(row.notice)}</div>` : ''}
        <div class="helper-actions">
          ${button('workspace-helper', '打开助手设置', 'primary', `data-workspace="${esc(row.id)}" ${row.helperRunning ? '' : 'disabled'}`, 'settings')}
          ${button('workspace-launch-helper', '启动助手', '', `data-workspace="${esc(row.id)}" ${row.helperRunning || !row.helperDirectory ? 'disabled' : ''}`, 'play')}
          ${button('workspace-launch-game', row.gamePids.length ? '游戏已运行' : '启动游戏', '', `data-workspace="${esc(row.id)}" ${row.gamePids.length ? 'disabled' : ''}`, 'game')}
          ${button('workspace-config', '环境设置', '', `data-workspace="${esc(row.id)}"`, 'box')}
        </div>
      </article>`).join('') || empty('box', '正在读取环境', '读取主机与 Sandboxie 沙箱列表。')}</section>
      ${sectionBar('已隐藏的环境', {
        count: state.hidden.length ? `${state.hidden.length} 个` : '',
        note: '隐藏只影响账号状态页的显示，助手目录与配对信息都会保留。',
      })}
      ${renderHiddenPanel()}`;
  }

  function showWorkspaceConfig(id) {
    const row = state.workspaces.find((item) => item.id === id);
    if (!row) throw new Error('环境信息已变化，请刷新后重试。');
    $('#workspace-dialog')?.remove();
    const dialog = document.createElement('dialog');
    dialog.id = 'workspace-dialog';
    dialog.className = 'workspace-dialog';
    dialog.dataset.workspace = id;
    dialog.innerHTML = `<form method="dialog"><div class="panel-title"><div><h3>${esc(row.name)} · 环境设置</h3><p>请选择该环境实际使用的助手副本。沙箱内的目录会转换为该沙箱中的路径。</p></div><button class="btn compact" value="cancel" aria-label="关闭">${icon('close')}</button></div>
      <div class="form-field"><label for="workspace-helper-dir">X1X 助手目录</label><div class="path-picker"><input class="field" id="workspace-helper-dir" value="${esc(row.helperDirectory)}" placeholder="选择包含 runtime 和 core 的助手目录"><button class="btn" type="button" data-act="workspace-browse-helper" data-workspace="${esc(id)}">选择目录</button></div></div>
      <div class="form-field"><label for="workspace-game-exe">游戏路径</label><div class="path-picker"><input class="field" id="workspace-game-exe" value="${esc(row.gameExecutable)}" placeholder="Deskrawl.exe 完整路径"><button class="btn" type="button" data-act="workspace-browse-game" data-workspace="${esc(id)}">选择游戏</button></div></div>
      <div class="form-field"><label for="workspace-game-mode">游戏启动方式</label><select class="select" id="workspace-game-mode"><option value="steam" ${row.gameLaunchMode === 'direct' ? '' : 'selected'}>通过此环境的 Steam 启动</option><option value="direct" ${row.gameLaunchMode === 'direct' ? 'selected' : ''}>直接启动 Deskrawl.exe</option></select><p class="form-hint">Steam 方式使用该环境内的登录状态；沙箱模式只向当前沙箱提交启动请求。</p></div>
      <div class="form-actions"><button class="btn" value="cancel">取消</button><button class="btn primary" type="button" data-act="workspace-save" data-workspace="${esc(id)}">保存环境</button></div></form>`;
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
    dialog.querySelector('.field')?.focus();
  }

  async function runWorkspaceAction(action, id) {
    if (action === 'workspace-config') { showWorkspaceConfig(id); return; }
    if (action === 'workspace-browse-helper' || action === 'workspace-browse-game') {
      const helper = action === 'workspace-browse-helper';
      const result = await hostRequest(helper ? 'browse-helper' : 'browse-game', { id });
      if (result.path) $(helper ? '#workspace-helper-dir' : '#workspace-game-exe').value = result.path;
      return;
    }
    if (action === 'workspace-save') {
      state.workspaces = await hostRequest('save', { id,
        helperDirectory: $('#workspace-helper-dir').value,
        gameExecutable: $('#workspace-game-exe').value,
        gameLaunchMode: $('#workspace-game-mode').value,
      });
      $('#workspace-dialog')?.close();
      notify('环境设置已保存。');
      renderCurrent();
      return;
    }
    const actions = { 'workspace-launch-helper': 'launch-helper', 'workspace-launch-game': 'launch-game',
      'workspace-helper': 'open-helper', 'workspace-browser': 'open-browser' };
    const hostAction = actions[action];
    if (!hostAction) return;
    const result = await hostRequest(hostAction, { id });
    if (result?.message) notify(result.message);
    if (hostAction === 'launch-helper' || hostAction === 'launch-game') {
      await loadWorkspaces(); renderCurrent();
      setTimeout(() => loadOverview(false), 3000);
    }
  }

  function renderSettings() {
    const config = state.config?.config;
    if (!config) {
      $('#page-root').innerHTML = `<div class="loading-card"><span class="loader"></span><span>正在读取本机设置…</span></div>`;
      return;
    }
    const ports = config.ports || {};
    const cap = state.globalFrameCap || {};
    const capOn = cap.enabled === true;
    $('#page-root').innerHTML = `<div class="settings-wrap">
      ${sectionBar('账号环境', {
        count: `${state.workspaces.length} 个`,
        note: '每个环境保存独立的助手目录、游戏路径和启动方式。助手更新时按原目录和配对信息查找新版本。',
        actions: button('rescan', '重新扫描', 'compact', '', 'refresh'),
      })}
      ${renderEnvPanel()}
      ${sectionBar('运行控制', {
        actions: `<span class="state-pill ${capOn ? 'active' : ''}">${capOn ? `已开启 · ${Number(cap.fps) || 10} 帧` : '已关闭'}</span>`,
      })}
      ${renderGlobalFrameCapSettings()}
      ${sectionBar('客户端版本', {
        actions: updateStatePill(),
        note: '应用启动时检测一次，运行期间每 12 小时检测一次。',
      })}
      ${renderUpdatePanel()}
      <details class="panel advanced-panel"><summary><span>高级连接设置</span><i>实例发现、扫描结果与配对数据</i></summary>
        <div class="form-block">
          <div class="form-block-head"><h4>实例发现</h4><span class="count-tag">发现 ${number(state.config.installs?.length)} 个目录</span></div>
          <p class="form-hint">中控在这些目录查找助手实例与沙盒配置。</p>
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
        </div>
        <div class="form-block">
          <div class="form-block-head"><h4>扫描结果</h4></div>
          ${renderInstallList(state.config.installs || [], state.config.sandboxie)}
        </div>
        <div class="form-block">
          <div class="form-block-head"><h4>配对数据</h4></div>
          <p class="form-hint">保留端口到账号的现有映射，备注会覆盖卡片上的端口名称。修改前请确认 JSON 格式正确。</p>
          <div class="form-field"><textarea class="textarea code" id="cfg-instances" aria-label="高级配对数据">${esc(JSON.stringify(config.instances || {}, null, 2))}</textarea></div>
          <div class="form-actions"><button class="btn primary" data-act="settings-advanced-save">保存配对数据</button></div>
        </div>
      </details>
    </div>`;
  }

  function renderInstallList(installs, sandboxie) {
    const sb = sandboxie ? `<span class="state-pill active">Sandboxie 已找到</span>` : `<span class="state-pill wait">未检测到 Sandboxie</span>`;
    const rows = installs.map((item) => `<tr><td class="mono">${esc(item.control ?? '—')}</td><td>${esc(item.sandbox || '沙盒外')}</td><td>${esc(item.ver || '未知')}</td><td>${esc(item.dir || '—')}</td></tr>`).join('');
    return `<div class="table-toolbar"><div class="table-toolbar-filters">${sb}</div><div class="table-toolbar-actions"><span class="count-tag">${installs.length} 个助手目录</span></div></div><div class="data-table-wrap"><table class="data-table"><thead><tr><th>控制端口</th><th>沙盒</th><th>版本</th><th>安装目录</th></tr></thead><tbody>${rows || '<tr><td colspan="4" class="muted-cell">尚未发现助手目录。</td></tr>'}</tbody></table></div>`;
  }

  async function loadOverview(force = false) {
    if (state.overviewLoading) return;
    state.overviewLoading = true;
    const workspaceLoad = loadWorkspaces();
    try {
      const steamTask = (state.steam && !force) ? Promise.resolve() : fetchSteam();
      const data = await api(`/api/overview${force ? '?force=1' : ''}`);
      await Promise.all([workspaceLoad, steamTask]);
      state.overview = data;
      state.lastError = '';
      setEngineState(true, `${number(data.summary?.online)} 个助手在线`);
      renderCurrent();
      loadLineage();
      loadGameWindows();
    } catch (error) {
      setEngineState(false, '本机助手无响应');
      await workspaceLoad;
      if (state.workspaces.length) {
        state.overview = state.overview || { instances: [], summary: {} };
        renderCurrent();
        loadLineage();
      } else if (!state.overview) {
        $('#page-root').innerHTML = `<div class="empty-state">${icon('ops')}<strong>暂时读不到助手状态</strong><span>${esc(error.message)} 过一会儿再试。</span>${button('refresh', '重新读取', 'primary', '', 'refresh')}</div>`;
      }
      if (state.lastError !== error.message) notify(error.message, 'error');
      state.lastError = error.message;
    } finally { state.overviewLoading = false; }
  }

  function fetchSteam() {
    return api('/api/steam').then((data) => { state.steam = data; }).catch(() => {});
  }

  async function loadSteam() {
    try { state.steam = await api('/api/steam'); renderCurrent(); }
    catch (error) { notify(`掉落记录读取失败：${error.message}`, 'error'); }
  }

  async function loadSettings() {
    try {
      state.config = await api('/api/config');
      renderCurrent();
    } catch (error) { notify(`运行设置读取失败：${error.message}`, 'error'); }
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
    const workspaceId = buttonNode?.dataset.workspace || '';
    const busyKey = `${action}:${workspaceId || port || 'single'}`;
    if (state.busy.has(busyKey)) return;
    setBusy(busyKey, true);
    if (buttonNode) buttonNode.disabled = true;
    try {
      if (action === 'hide-account') {
        const row = managedInstances().find((item) => item.workspace?.id === workspaceId);
        const name = row?.label || '该环境';
        if (!confirm(`从账号状态页移除「${name}」？\n配置与配对信息会保留，可在「助手设置 → 环境管理」里加回来。`)) return;
        state.workspaces = await hostRequest('hide-account', { id: workspaceId });
        notify(`已移除「${name}」，可在助手设置恢复。`);
        await loadHidden(true); renderCurrent();
        return;
      }
      if (action === 'unhide-account') {
        state.workspaces = await hostRequest('unhide-account', { id: workspaceId });
        notify('环境已恢复到账号状态页。');
        await loadHidden(true); renderCurrent();
        return;
      }
      if (action === 'add-account') {
        const value = ($('#env-new-sandbox')?.value || '').trim();
        state.workspaces = await hostRequest('add-account', { sandbox: value });
        notify(value ? `已添加沙盘「${value}」。` : '已添加主机环境。');
        await loadHidden(true); renderCurrent();
        return;
      }
      if (action.startsWith('workspace-')) {
        await runWorkspaceAction(action, workspaceId);
        return;
      }
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
        else if (state.view === 'lineage') await loadLineageReports();
        else if (state.view === 'auction') await loadAuctionInventory();
        else if (state.view === 'settings') await loadSettings();
        return;
      }
      if (action === 'lineage-refresh') {
        await loadLineageReports(true);
        notify('血脉鉴定已刷新。');
        return;
      }
      if (action === 'open-lineage') {
        navigate('lineage');
        return;
      }
      if (action === 'open-loot') {
        navigate('loot');
        return;
      }
      if (action === 'auction-refresh') {
        await loadAuctionInventory(true);
        notify(state.auction?.supported ? '拍卖候选已刷新。' : '已刷新候选；当前助手不支持自动上架。');
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
        await checkHelperUpdate(port, buttonNode);
      } else if (action === 'update') {
        await runHelperUpdate(port, buttonNode?.dataset.version || '');
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

  async function readInstance(port) {
    const data = await api('/api/overview');
    state.overview = data;
    return (data.instances || []).find((item) => Number(item.port) === Number(port));
  }

  async function waitUntil(check, timeoutMs, message, intervalMs = 1500) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (await check()) return true;
      await new Promise((resolve) => setTimeout(resolve, intervalMs));
    }
    throw new Error(message);
  }

  function describeProgress(progress) {
    if (progress == null) return '';
    if (typeof progress === 'number') {
      return progress <= 100 ? `${Math.round(progress)}%` : `${(progress / 1048576).toFixed(1)} MB`;
    }
    if (typeof progress === 'object') {
      const done = Number(progress.done ?? progress.received ?? progress.current ?? 0);
      const total = Number(progress.total ?? progress.size ?? 0);
      if (total > 0) return `${Math.round((done * 100) / total)}%（${(done / 1048576).toFixed(1)}/${(total / 1048576).toFixed(1)} MB）`;
      if (done > 0) return `${(done / 1048576).toFixed(1)} MB`;
      if (progress.text) return String(progress.text);
    }
    return String(progress);
  }

  async function checkHelperUpdate(port, buttonNode) {
    if (buttonNode) buttonNode.disabled = true;
    try {
      await post('/api/update', { action: 'check', port });
      if (state.view === 'overview') renderOverview();
      await waitUntil(async () => {
        const state = (await readInstance(port))?.update?.state;
        return state && state !== 'checking' && state !== 'waiting';
      }, 120000, '版本检查超时，请稍后重试。');
      const update = (await readInstance(port))?.update || {};
      if (update.error) throw new Error(update.error);
      notify(update.state === 'available'
        ? `可以更新到 ${update.version}，点「更新」开始安装。`
        : `助手已是 ${update.version || '最新版本'}。`, update.state === 'available' ? 'warn' : '');
      if (state.view === 'overview') renderOverview();
    } finally {
      if (buttonNode?.isConnected) buttonNode.disabled = false;
    }
  }

  const UPDATE_STEPS = [
    ['stop', '停止全自动流程'],
    ['close', '关闭游戏'],
    ['install', '下载并安装助手'],
    ['launch', '重新启动游戏'],
  ];

  function openUpdateDialog(name, version) {
    const dialog = document.createElement('dialog');
    dialog.className = 'workspace-dialog update-dialog';
    dialog.innerHTML = `<div class="panel-title"><div><h3>更新助手 · ${esc(name)}</h3><p>目标版本 ${esc(version || '最新版本')}。安装期间不要手动操作该环境的游戏窗口。</p></div></div>
      <ol class="update-steps">${UPDATE_STEPS.map(([key, label]) => `<li class="update-step" data-step="${key}" data-state="wait"><span class="step-dot"></span><div><strong>${esc(label)}</strong><span class="step-note">等待开始</span></div></li>`).join('')}</ol>
      <div class="update-result" hidden></div>
      <div class="form-actions"><button class="btn primary" type="button" id="update-dialog-close" disabled>关闭</button></div>`;
    dialog.addEventListener('close', () => dialog.remove());
    document.body.appendChild(dialog);
    dialog.showModal();
    const step = (key, stateName, note) => {
      const node = dialog.querySelector(`.update-step[data-step="${key}"]`);
      if (!node) return;
      node.dataset.state = stateName;
      const noteNode = node.querySelector('.step-note');
      if (noteNode) noteNode.textContent = note || '';
    };
    const finish = (ok, message) => {
      dialog.querySelectorAll('.update-step[data-state="run"]').forEach((node) => { node.dataset.state = ok ? 'done' : 'error'; });
      const result = dialog.querySelector('.update-result');
      result.hidden = false;
      result.className = `update-result ${ok ? 'ok' : 'fail'}`;
      result.textContent = message;
      const closeButton = dialog.querySelector('#update-dialog-close');
      closeButton.disabled = false;
      closeButton.addEventListener('click', () => dialog.close());
    };
    return { dialog, step, finish };
  }

  async function runHelperUpdate(port, version) {
    const account = managedInstances().find((row) => Number(row.port) === Number(port));
    if (!account) throw new Error('实例信息已变化，请刷新后重试。');
    const name = account.label || `端口 ${port}`;
    if (!confirm(`更新「${name}」的助手到 ${version || '最新版本'}？\n\n会自动停止全自动、关闭游戏、安装助手，装完后再把游戏启动回来。`)) return;
    const ui = openUpdateDialog(name, version);
    const hadGame = account.game?.running === true;
    try {
      if (account.loop?.running) {
        ui.step('stop', 'run', '正在提交停止请求…');
        await post('/api/action', { port, path: '/control/loop', action: 'stop' });
        await waitUntil(async () => !(await readInstance(port))?.loop?.running, 45000, '自动流程没有停下来，请手动停止后重试。');
        ui.step('stop', 'done', '已停止。');
      } else ui.step('stop', 'skip', '未在运行，跳过。');

      if (hadGame) {
        ui.step('close', 'run', '正在关闭游戏…');
        await post('/api/game', { port, action: 'close' });
        await waitUntil(async () => !(await readInstance(port))?.game?.running, 90000, '游戏进程没有在 90 秒内退出，请手动关闭后重试。');
        ui.step('close', 'done', '游戏已退出。');
      } else ui.step('close', 'skip', '游戏未运行，跳过。');

      ui.step('install', 'run', '正在提交安装请求…');
      const apply = await post('/api/update', { action: 'apply', port, version });
      if (apply.ok === false) throw new Error(apply.error || '安装请求被拒绝。');
      const deadline = Date.now() + 20 * 60 * 1000;
      let update = {};
      while (Date.now() < deadline) {
        update = (await readInstance(port))?.update || {};
        if (update.error || update.blocked) throw new Error(update.error || update.blocked);
        const progress = describeProgress(update.progress);
        if (update.state === 'latest' && !progress) break;
        ui.step('install', 'run', progress ? `正在安装…${progress}` : `状态：${update.label || update.state || '安装中'}`);
        await new Promise((resolve) => setTimeout(resolve, 2000));
      }
      if (update.state !== 'latest') throw new Error('安装等待超时，到助手设置里核对一下版本。');
      ui.step('install', 'done', `已安装 ${update.version || version || '最新版本'}。`);

      if (hadGame) {
        ui.step('launch', 'run', '正在重新启动游戏…');
        await post('/api/game', { port, action: 'launch' });
        ui.step('launch', 'done', '启动请求已提交。');
      } else ui.step('launch', 'skip', '游戏此前未运行，保持关闭。');

      ui.finish(true, `「${name}」的助手已更新完成。`);
      notify(`${name} 助手已更新。`);
      await loadOverview(true);
    } catch (error) {
      ui.finish(false, `更新中断：${error.message || '未知错误'}`);
      notify(`更新中断：${error.message}`, 'error');
    }
  }

  async function refreshAll(force) {
    await loadOverview(force);
    if (state.view === 'loot') await loadSteam();
    else if (state.view === 'lineage') await loadLineageReports();
    else if (state.view === 'auction') await loadAuctionInventory();
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
    else if (state.view === 'helpers') renderHelpers();
    else if (state.view === 'settings' && state.config) renderSettings();
  }

  function onHostMessage(event) {
    const message = event.data || {};
    if (message.type === 'workspace-result') {
      const pending = hostRequests.get(message.requestId);
      if (pending) {
        clearTimeout(pending.timeout); hostRequests.delete(message.requestId);
        if (message.ok) pending.resolve(message.result);
        else pending.reject(new Error(message.error || '账号环境操作失败。'));
      }
      return;
    }
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

  /* 卡片上的「更多」菜单：常驻三个动词，其余操作收进一层，位置固定在按钮上沿。 */
  function closeMenus(except = null) {
    $$('.env-more.open').forEach((node) => {
      if (node === except) return;
      node.classList.remove('open');
      node.querySelector('[data-menu-toggle]')?.setAttribute('aria-expanded', 'false');
    });
  }

  function toggleMenu(node) {
    if (!node) return;
    const open = !node.classList.contains('open');
    closeMenus(node);
    node.classList.toggle('open', open);
    node.querySelector('[data-menu-toggle]')?.setAttribute('aria-expanded', String(open));
  }

  document.addEventListener('click', async (event) => {
    const nav = event.target.closest('.nav-item');
    if (nav) { navigate(nav.dataset.view); return; }
    const menuToggle = event.target.closest('[data-menu-toggle]');
    if (menuToggle) { event.preventDefault(); toggleMenu(menuToggle.closest('.env-more')); return; }
    if (!event.target.closest('.env-more')) closeMenus();
    const actionButton = event.target.closest('[data-act]');
    if (actionButton) {
      event.preventDefault();
      if (actionButton.closest('.menu')) closeMenus();
      await runAction(actionButton.dataset.act, actionButton, event);
    }
  });
  document.addEventListener('change', (event) => {
    const rateSelect = event.target.closest('select[data-act="global-frame-cap-rate"]');
    if (!rateSelect) return;
    requestGlobalFrameCap(state.globalFrameCap.enabled === true, Number(rateSelect.value));
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') { closeMenus(); return; }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'r') {
      event.preventDefault(); runAction('refresh');
      return;
    }
    if (event.ctrlKey || event.metaKey || event.altKey) return;
    const target = event.target;
    if (target instanceof HTMLElement && target.closest('input, textarea, select, [contenteditable="true"]')) return;
    // 侧栏的 01–06 是可用快捷键，不是装饰。
    const index = Number(event.key) - 1;
    const views = Object.keys(viewNames);
    if (index >= 0 && index < views.length) {
      event.preventDefault();
      navigate(views[index]);
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
    else if (state.view === 'lineage') loadLineageReports();
    else if (state.view === 'auction') loadAuctionInventory();
    else if (state.view === 'helpers') loadWorkspaces().then(renderHelpers);
  }, 7000);
  setInterval(() => checkForUpdates({ force: true, quiet: true }), 12 * 60 * 60 * 1000);
})();
