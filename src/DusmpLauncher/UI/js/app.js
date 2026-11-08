(() => {
  'use strict';
  const pending = new Map(), renderKeys = new Map();
  let nextId = 0, table = {}, state = {}, countries = [], languages = [], mods = [];
  let selectedCountry = null, selectedLanguage = null, deviceUrl = '', uiScreen = 'loading';
  let editingSettings = false, selecting = false, authActive = false, externalLoading = false;
  let accounts = [], activeAccountId = null, drawerOpen = false, menuAccountId = null;
  let editions = [], currentEdition = null, switchingEdition = false;
  let authPromise = null, authCancelPromise = null, authGeneration = 0;
  let progressFrame = null, latestProgress = null, validationSequence = 0;
  let settingsJvmPreset = null, settingsMinRamMb = 512;
  let syncBusy = false;
  let installedMods = [], installedModSwitches = [], installedModsLoading = false, installedModsMutating = false;
  let installedModsReturn = 'main', installedModsError = '', installedModsRequest = 0;
  const $ = id => document.getElementById(id);
  // ?? rather than ||: several subtitles are deliberately empty, and an
  // empty string falling through printed the raw key on screen. A key that
  // is genuinely absent still shows itself, which is how gaps get noticed.
  const t = (key, args = {}) => Object.entries(args).reduce((s, [k, v]) => s.replaceAll(`{${k}}`, v), table[key] ?? key);
  const text = (id, value) => { const el = $(id); if (el && el.textContent !== String(value ?? '')) el.textContent = value ?? ''; };

  function send(command, payload = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++nextId);
      // Sign-in and installation commands can remain open while the user works.
      const timer = setTimeout(() => { if (pending.delete(id)) reject(Object.assign(Error(t('error.bridge.timeout')), { key: 'error.bridge.timeout' })); }, ['loginMicrosoft', 'startGame', 'repairInstallation', 'resetInstallation', 'selectEdition', 'importMods', 'syncNow', 'finishOnboarding'].includes(command) ? 960000 : 120000);
      pending.set(id, { resolve, reject, timer });
      window.external.sendMessage(JSON.stringify({ id, command, payload }));
    });
  }
  function setText() {
    document.querySelectorAll('[data-i18n]').forEach(e => e.textContent = t(e.dataset.i18n));
    document.querySelectorAll('[data-i18n-placeholder]').forEach(e => e.placeholder = t(e.dataset.i18nPlaceholder));
    document.querySelectorAll('[data-i18n-aria-label]').forEach(e => e.setAttribute('aria-label', t(e.dataset.i18nAriaLabel)));
    document.querySelectorAll('[data-i18n-alt]').forEach(e => e.alt = t(e.dataset.i18nAlt));
    document.title = t('app.windowTitle');
    if (uiScreen === 'installed-mods') renderInstalledMods();
  }
  function screen(name) {
    if (name === 'main' && state.launcherUpdatePending) name = state.fatalErrorKey ? 'error' : 'loading';
    if (name === uiScreen && document.querySelector('.screen.is-active')) return;
    uiScreen = name;
    if (name === 'onboarding-optional-mods') {
      $('mods-back').hidden = !editingSettings && !state.firstLaunch && state.screen === name;
      text('mods-continue', t(!state.firstLaunch || editingSettings ? 'common.done' : 'onboarding.optionalMods.action'));
    }
    document.querySelectorAll('.screen').forEach(e => e.classList.toggle('is-active', e.dataset.screen === name));
    // Arriving at the account step always offers the choice again. Leaving it
    // with the offline name form open meant coming back showed that form on its
    // own: the Microsoft button was not on screen to be pressed, so the player
    // could no longer change their mind. A sign-in already under way owns the
    // panel and is left alone.
    if (name === 'onboarding-edition') loadEditions();
    if (name === 'onboarding-account' && !authActive) showAccountChoices();
    if (name === 'onboarding-external-launcher') loadExternalLaunchers();
    if (name === 'onboarding-language') renderLanguages($('language-search').value);
    if (name === 'onboarding-country') renderCountries($('country-search').value);
    if (name === 'onboarding-device') renderDevicePreset();
    if (name === 'installed-mods') loadInstalledMods();
  }
  function event(name, payload) {
    if (name === 'screen') renderState(payload);
    if (name === 'downloadProgress') queueProgress(payload);
    if (name === 'stage') renderStage(payload);
    if (name === 'language') {
      table = payload.table || {}; document.documentElement.lang = payload.tag || 'en-US';
      document.documentElement.dir = payload.rtl ? 'rtl' : 'ltr'; setText(); renderKeys.clear();
      renderLanguages($('language-search').value); renderCountries($('country-search').value); renderState(state);
    }
    if (name === 'deviceCode') showDevice(payload);
    if (name === 'notice') toast(payload.key && table[payload.key] ? t(payload.key) : payload.message || t('error.unknown'));
    if (name === 'gameState') text('play-sub', payload.message || '');
  }
  function onceChanged(key, data, render) {
    const serialized = JSON.stringify(data);
    if (renderKeys.get(key) === serialized) return;
    renderKeys.set(key, serialized); render(data);
  }
  function setImage(id, url) { const img = $(id); const safe = safeUrl(url); if (img && img.getAttribute('src') !== safe) { img.onerror = () => { img.onerror = null; img.src = 'app://launcher/assets/icon.png'; }; img.src = safe; } }
  // Display names for the launchers DUSMP can hand sign-in to.
  const externalNames = { modrinth: 'Modrinth App', curseforge: 'CurseForge', prism: 'Prism Launcher', official: 'Minecraft Launcher', multimc: 'MultiMC', polymc: 'PolyMC' };
  function renderState(s) {
    const oldScreen = state.screen, wasReady = state.readyToPlay; state = s || state;
    if (uiScreen === 'onboarding-optional-mods')
      $('mods-back').hidden = !editingSettings && !state.firstLaunch && state.screen === 'onboarding-optional-mods';
    text('error-message', state.fatalErrorKey ? (table[state.fatalErrorKey] ? t(state.fatalErrorKey) : t('error.unknown')) : '');
    text('error-detail', state.fatalErrorDetail || '');
    if (state.launcherUpdatePending && state.screen === 'main') screen('main');
    else if (state.screen === 'error' || (!editingSettings && (oldScreen !== state.screen || uiScreen === 'loading'))) screen(state.screen || 'loading');
    // In external mode DUSMP holds no account at all — the host launcher does —
    // so the chip names that launcher instead of showing an empty player.
    const external = state.playMode === 'external';
    text('main-username', external ? (externalNames[state.externalLauncherId] || t('main.account.external')) : (state.profile?.username || ''));
    text('main-account-kind', external ? t('main.account.external')
      : t(state.profile?.type === 'microsoft' ? 'main.account.microsoft' : 'main.account.offline'));
    setPlayerHead(external ? 'app://launcher/assets/launchers/' + (state.externalLauncherId || 'generic') + '.svg' : null);
    onceChanged('edition', state.edition, () => { currentEdition = state.edition ?? currentEdition; loadEditions(); });
    // The drawer is built from the stored account list, which only changes when
    // somebody signs in, is removed, or is switched to.
    onceChanged('accountsFor', [state.profile?.uuid, state.profile?.username, state.profile?.type, state.playMode], () => loadAccounts());
    text('main-installed-version', state.installedVersion || '—');
    text('main-minecraft-version', state.minecraftVersion || '—');
    text('main-installation-path', state.instancePath || '—');
    if ($('main-installation-path')) $('main-installation-path').title = state.instancePath || '';
    text('settings-launcher-version', state.launcherVersion || '');
    $('settings-fixtures-row').hidden = !state.offlineFixtures;
    text('settings-language-value', languages.find(l => l.tag === state.language)?.englishName || state.language || 'English');
    text('settings-country-value', countries.find(c => c.code === state.country)?.englishName || state.country || '—');
    text('settings-account-value', external ? (externalNames[state.externalLauncherId] || t('main.account.external')) : state.profile?.username || '—');
    text('settings-launcher-value', external ? (externalNames[state.externalLauncherId] || t('main.account.external')) : t('app.title'));
    $('settings-logout-btn').hidden = external;
    $('btn-accounts').hidden = external;
    text('settings-mods-value', (state.optionalMods || mods).filter(x => x.enabled).length);
    renderDevicePreset();
    updateInstalledModControls();
    if (uiScreen === 'installed-mods' && state.readyToPlay === true && wasReady !== true) loadInstalledMods();
    if (state.progress) queueProgress(state.progress);
    if (state.servers) onceChanged('servers', state.servers, renderServers);
    if (state.news) onceChanged('news', state.news, renderNews);
    if (state.optionalMods) onceChanged('mods', state.optionalMods, renderMods);
    $('maintenance-banner').hidden = !state.status?.maintenance;
    text('maintenance-text', localized(state.status?.message));
    const running = ['preparing', 'launching', 'running'].includes(state.gameState);
    $('btn-sync').disabled = syncBusy || running || !state.readyToPlay;
    $('btn-sync').title = t(syncBusy ? 'main.syncing' : 'main.sync');
    $('btn-optional-mods').disabled = running;
    const live = !external && (state.gameState === 'running' || state.gameState === 'launching');
    const play = $('btn-play');
    play.dataset.action = live ? 'stop' : 'play';
    play.classList.toggle('is-stop', live);
    text('btn-play', live ? t('main.stop')
      : running ? t('main.preparing')
      : state.readyToPlay ? (external ? t('main.external.open', { launcher: externalNames[state.externalLauncherId] || t('main.account.external') }) : t('main.play'))
      : progressLabel(state.progress));
    play.title = live ? t('main.stopHint') : '';
    // Stop stays clickable while the game is up; everything else needs a ready install.
    play.disabled = live ? false : (!state.readyToPlay || running || !!state.status?.blockPlay);
    text('main-ready-title', t(state.readyToPlay ? 'main.ready.title' : 'main.installing.title'));
    $('main-ready-description').hidden = !state.readyToPlay;
    if (state.readyToPlay && !running) { text('play-sub', t(external && state.externalLauncherId === 'official' ? 'main.external.handoff' : 'main.progress.ready')); $('play-bar-track').hidden = true; }
  }
  // ---------- editions ----------
  //
  // Two packs, each its own Minecraft version and its own mod list. Nothing is
  // downloaded until one is chosen, so this screen is what starts the install;
  // the pill on the home screen is the same choice, offered again.

  async function loadEditions() {
    try {
      const payload = await send('getEditions');
      editions = payload?.editions || [];
      currentEdition = payload?.current ?? null;
    } catch { editions = []; }
    renderEditions();
  }

  function renderEditions() {
    const grid = $('edition-grid');
    if (grid) {
      grid.replaceChildren();
      for (const edition of editions) grid.append(editionCard(edition));
      $('edition-empty').hidden = editions.length > 0;
    }

    // The home screen only offers the switch when there is something to switch
    // between: one edition is not a choice.
    const pill = $('edition-switch');
    if (!pill) return;
    pill.hidden = editions.length < 2;
    pill.replaceChildren();
    for (const edition of editions) {
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = edition.name || edition.id;
      button.classList.toggle('is-current', edition.id === currentEdition);
      button.disabled = !edition.available || switchingEdition;
      button.title = edition.available ? '' : t('onboarding.edition.unavailable');
      button.onclick = () => chooseEdition(edition.id);
      pill.append(button);
    }
  }

  function editionCard(edition) {
    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'edition-card';
    card.classList.toggle('is-current', edition.id === currentEdition);
    card.disabled = !edition.available || switchingEdition;

    const title = document.createElement('h2');
    title.textContent = edition.name || edition.id;
    card.append(title);

    if (edition.id === currentEdition) {
      const badge = document.createElement('span');
      badge.className = 'edition-current';
      badge.textContent = t('onboarding.edition.installed');
      card.append(badge);
    }

    const blurb = document.createElement('p');
    blurb.textContent = localized(edition.description) || '';
    card.append(blurb);

    const meta = document.createElement('span');
    meta.className = 'edition-meta';
    meta.textContent = edition.available
      ? [edition.version && `v${edition.version}`, edition.minecraft && `Minecraft ${edition.minecraft}`,
         edition.loaderVersion && `${edition.loader || ''} ${edition.loaderVersion}`.trim()].filter(Boolean).join(' · ')
      : t('onboarding.edition.unavailable');
    card.append(meta);

    card.onclick = () => chooseEdition(edition.id);
    return card;
  }

  async function chooseEdition(id) {
    // Switching installs the other pack, which is a long job: the whole choice
    // is disabled while it runs rather than letting a second click start a
    // second install over the first.
    if (switchingEdition || id === currentEdition && uiScreen === 'main') return;
    switchingEdition = true;
    renderEditions();
    try {
      const payload = await send('selectEdition', { id });
      editions = payload?.editions || editions;
      currentEdition = payload?.current ?? id;
      await refreshState();
    } catch (e) { showError(e); }
    finally { switchingEdition = false; renderEditions(); }
  }

  // ---------- account drawer ----------
  //
  // The launcher remembers every account that has signed in. The caret beside
  // the nick points up while the drawer is closed and flips down while it is
  // open, so the arrow always points at where the list will appear. With a
  // single account the chip itself still goes straight to the skin window,
  // because there is nothing to switch between.

  async function loadAccounts() {
    // A launcher that cannot read its own account file still plays fine, so a
    // failure here only costs the drawer.
    try { applyAccounts(await send('listAccounts')); }
    catch { accounts = []; activeAccountId = null; renderAccounts(); }
  }

  function applyAccounts(payload) {
    accounts = payload?.accounts || [];
    activeAccountId = payload?.activeId || null;
    renderAccounts();
  }

  function renderAccounts() {
    const caret = $('btn-accounts'), list = $('account-list');
    if (!caret || !list) return;
    // In external mode the host launcher owns the account, so there is nothing
    // here to switch.
    const usable = state.playMode !== 'external' || !!state.profile?.username;
    caret.hidden = !usable;
    if (!usable) return closeDrawer();
    list.textContent = '';
    for (const account of accounts) list.append(accountRow(account));
    list.hidden = accounts.length === 0;
    const hint = document.querySelector('.drawer-hint');
    if (hint) hint.hidden = accounts.length < 2;
  }

  function accountRow(account) {
    const item = document.createElement('li');
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'account-row';
    row.classList.toggle('is-active', account.id === activeAccountId);
    row.dataset.accountId = account.id;

    // Same 8x8 crop the chip uses, from the skin the account actually has.
    const head = document.createElement('span');
    head.className = 'account-head';
    const skin = account.skinUrl || '';
    if (/^https:\/\//.test(skin)) { head.classList.add('is-skin'); head.style.backgroundImage = `url("${skin}")`; }
    else head.style.backgroundImage = 'url("app://launcher/assets/icon.png")';

    const box = document.createElement('span');
    box.className = 'account-text';
    const name = document.createElement('span');
    name.className = 'account-name';
    name.dir = 'ltr';
    name.textContent = account.username || '';
    const kind = document.createElement('span');
    kind.className = 'account-kind';
    kind.textContent = t(account.type === 'microsoft' ? 'main.account.microsoft' : 'main.account.offline');
    box.append(name, kind);
    row.append(head, box);

    if (account.id === activeAccountId) {
      const badge = document.createElement('span');
      badge.className = 'account-badge';
      badge.textContent = t('accounts.active');
      row.append(badge);
    }
    item.append(row);
    return item;
  }

  function openDrawer() {
    const drawer = $('account-drawer');
    if (!drawer || drawerOpen) return;
    drawerOpen = true;
    drawer.hidden = false;
    $('btn-accounts')?.setAttribute('aria-expanded', 'true');
    loadAccounts();
  }

  function closeDrawer() {
    closeAccountMenu();
    if (!drawerOpen) return;
    drawerOpen = false;
    const drawer = $('account-drawer');
    if (drawer) drawer.hidden = true;
    $('btn-accounts')?.setAttribute('aria-expanded', 'false');
  }

  function openAccountMenu(id, x, y) {
    const menu = $('account-menu');
    if (!menu || !accounts.some(a => a.id === id)) return;
    menuAccountId = id;
    document.querySelectorAll('.account-row').forEach(r => r.classList.toggle('is-target', r.dataset.accountId === id));
    // Selecting the account already in use would do nothing, so that one item
    // is disabled rather than removed: the menu keeps its shape wherever it
    // opens, and the greyed row says why.
    menu.querySelector('[data-account-action="select"]').disabled = id === activeAccountId;
    menu.hidden = false;
    // Measured only once it is visible, then pulled back inside the window when
    // the cursor is near an edge.
    const box = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(8, Math.min(x, window.innerWidth - box.width - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(y, window.innerHeight - box.height - 8))}px`;
  }

  function closeAccountMenu() {
    const menu = $('account-menu');
    if (menu) menu.hidden = true;
    menuAccountId = null;
    document.querySelectorAll('.account-row.is-target').forEach(r => r.classList.remove('is-target'));
  }

  async function accountAction(action, id) {
    if (!accounts.some(a => a.id === id)) return;
    if (action === 'remove') {
      applyAccounts(await send('removeAccount', { id }));
      await refreshState();
      if (accounts.length < 2) closeDrawer();
      return;
    }
    if (action !== 'select' && action !== 'customize') return;
    // Customizing works on whichever account is in use, so switching first is
    // part of the action rather than something the player has to remember.
    if (id !== activeAccountId) applyAccounts(await send('selectAccount', { id }));
    await refreshState();
    if (action === 'customize') await send('openSkinWindow');
    closeDrawer();
  }

  // Draws the chip's head straight from the account's own skin texture.
  //
  // It used to point at a third-party render service, which caches by UUID: a
  // player who had just changed their skin kept seeing the old one, and an
  // account the service had never rendered showed someone else's default. The
  // texture Mojang reports is the truth, and the head is a fixed 8x8 square in
  // it, so cropping is both exact and free.
  function setPlayerHead(overrideUrl) {
    const head = $('main-avatar');
    if (!head) return;
    const skin = overrideUrl || (state.profile?.skinUrl ?? '');
    const usable = /^(https:\/\/|(?:app:\/\/launcher|http:\/\/localhost)\/assets\/)/.test(skin);
    const cropped = usable && !overrideUrl;
    head.classList.toggle('is-skin', cropped);
    head.style.backgroundImage = usable ? `url("${skin}")` : 'url("app://launcher/assets/icon.png")';
    if (!cropped) head.style.removeProperty('--head-scale');
  }

  function localized(value) { return typeof value === 'string' ? value : value?.[state.language] || value?.en || value?.['en-US'] || ''; }
  function stageLabel(stage) { return table[`loading.stage.${String(stage || '').toLowerCase()}`] || t('main.preparing'); }
  function progressLabel(p) { return p?.percent != null && String(p.stage).toLowerCase() === 'downloadingfiles' ? t('main.downloading', { percent: Math.floor(p.percent) }) : stageLabel(p?.stage); }
  function queueProgress(p) {
    if (!p) return;
    latestProgress = p;
    if (progressFrame !== null) return;
    progressFrame = window.requestAnimationFrame(() => { progressFrame = null; renderProgress(latestProgress); });
  }
  function renderProgress(p) {
    const downloading = ['downloadingfiles', 'downloadinglauncher'].includes(String(p.stage).toLowerCase());
    const percent = p.percent == null ? null : Math.max(0, Math.min(100, p.percent));
    text('loading-stage', stageLabel(p.stage));
    $('loading-bar-track').hidden = percent == null;
    $('loading-bar-track').setAttribute('aria-valuenow', percent ?? 0);
    $('loading-bar-fill').style.width = percent == null ? '0%' : `${percent}%`;
    text('loading-percent', percent == null ? '' : `${Math.floor(percent)}%`);
    text('loading-file', p.currentFile || '');
    text('loading-speed', p.bytesPerSecond > 0 ? `${formatBytes(p.bytesPerSecond)}/s` : '');
    text('loading-bytes', downloading ? t('loading.download', { downloaded: formatBytes(p.downloadedBytes || 0), total: formatBytes(p.totalBytes || 0) }) : '');
    $('loading-speed-item').hidden = !downloading || !(p.bytesPerSecond > 0);
    $('loading-bytes-item').hidden = !downloading || !(p.totalBytes > 0);
    text('loading-files', p.filesTotal ? `${p.filesCompleted || 0} / ${p.filesTotal}` : '');
    if (!state.readyToPlay) {
      text('play-sub', downloading && p.totalBytes > 0 ? `${t('main.progress.bytes', { downloaded: formatBytes(p.downloadedBytes || 0), total: formatBytes(p.totalBytes) })}${p.bytesPerSecond > 0 ? ` · ${formatBytes(p.bytesPerSecond)}/s` : ''}` : stageLabel(p.stage));
      $('play-bar-track').hidden = percent == null; $('play-bar-fill').style.width = percent == null ? '0%' : `${percent}%`;
      if ($('btn-play').dataset.action !== 'stop') text('btn-play', progressLabel(p));
    }
  }
  function renderStage(p) { if (p?.stage) text('loading-stage', stageLabel(p.stage)); }
  function formatBytes(n) { if (!n || n < 0) return '0 B'; const u = ['B', 'KB', 'MB', 'GB', 'TB'], i = Math.min(Math.floor(Math.log(n) / Math.log(1024)), 4); return `${(n / 1024 ** i).toFixed(i ? 1 : 0)} ${u[i]}`; }
  function flagImage(url) { return /^(?:app:\/\/launcher|http:\/\/localhost)\/assets\/flags\/[a-z]{2}\.svg$/.test(url || '') ? `<img class="picker-flag" src="${url}" alt="" loading="lazy" width="28" height="21">` : '<span class="picker-flag flag-placeholder" aria-hidden="true"></span>'; }
  function renderLanguages(filter = '') {
    const q = filter.toLocaleLowerCase(); const list = $('language-list'); list.replaceChildren();
    for (const l of languages.filter(x => `${x.englishName} ${x.nativeName} ${x.tag}`.toLocaleLowerCase().includes(q))) {
      const b = document.createElement('button'); b.type = 'button'; b.className = 'picker-item' + (selectedLanguage === l.tag ? ' selected' : '');
      b.innerHTML = `${flagImage(l.flag)}<span class="picker-name">${escapeHtml(l.englishName)}</span><span class="picker-native">${escapeHtml(l.nativeName)}</span>`;
      b.onclick = () => choose(async () => { selectedLanguage = l.tag; await send('selectLanguage', { tag: l.tag }); await refreshState(); if (editingSettings) screen('settings'); }); list.append(b);
    }
    $('language-empty').hidden = list.children.length > 0;
    $('language-back').hidden = !editingSettings;
  }
  function renderCountries(filter = '') {
    const q = filter.toLocaleLowerCase(); const list = $('country-list'); list.replaceChildren();
    for (const c of countries.filter(x => `${x.englishName} ${x.nativeName} ${x.code}`.toLocaleLowerCase().includes(q))) {
      const b = document.createElement('button'); b.type = 'button'; b.className = 'picker-item' + (selectedCountry === c.code ? ' selected' : '');
      b.innerHTML = `${flagImage(c.flag)}<span class="picker-name">${escapeHtml(c.englishName)}</span><span class="picker-native">${escapeHtml(c.code)}</span>`;
      b.onclick = () => choose(async () => { selectedCountry = c.code; await send('selectCountry', { code: c.code }); await refreshState(); if (editingSettings) screen('settings'); }); list.append(b);
    }
    $('country-empty').hidden = list.children.length > 0;
  }
  async function choose(action) { if (selecting) return; selecting = true; document.body.classList.add('is-selecting'); try { await action(); } catch (e) { showError(e); } finally { selecting = false; document.body.classList.remove('is-selecting'); } }
  function renderDevicePreset() {
    const preset = editingSettings ? settingsJvmPreset : state.jvmPreset;
    $('device-back').hidden = !editingSettings && !state.firstLaunch;
    for (const id of ['low8gb', 'default', 'smooth']) {
      const button = $('device-' + id);
      button.classList.toggle('is-selected', preset === (id === 'default' ? 'standard' : id));
      button.setAttribute('aria-pressed', String(preset === (id === 'default' ? 'standard' : id)));
    }
    text('settings-device-value', ['low8gb', 'standard', 'smooth'].includes(preset)
      ? t('onboarding.device.' + (preset === 'standard' ? 'default' : preset) + '.name') : t('settings.devicePreset.unselected'));
  }
  async function selectDevicePreset(preset) {
    return choose(async () => {
      const controls = ['device-low8gb', 'device-default', 'device-smooth', 'device-back'].map($);
      controls.forEach(button => button.disabled = true);
      try {
        fillSettings(await send('selectJvmPreset', { preset }));
        await refreshState();
        screen(editingSettings ? 'settings' : state.screen);
      } finally { controls.forEach(button => button.disabled = false); }
    });
  }
  function installedModEditsAllowed() {
    return !['preparing', 'launching', 'running'].includes(state.gameState) && state.readyToPlay === true;
  }
  function updateInstalledModControls() {
    const busy = !installedModEditsAllowed();
    const disabled = busy || installedModsLoading || installedModsMutating;
    $('installed-mods-upload').disabled = disabled;
    for (const input of installedModSwitches) input.disabled = disabled;
    const message = installedModsLoading ? t('installedMods.loading')
      : installedModsError || (installedModsMutating ? t('installedMods.saving') : busy ? t('error.mods.busy') : '');
    text('installed-mods-status', message);
    $('installed-mods-status').hidden = !message;
  }
  async function loadInstalledMods() {
    const request = ++installedModsRequest;
    installedModsLoading = true; installedModsError = '';
    updateInstalledModControls();
    try {
      const result = await send('getInstalledMods');
      if (request !== installedModsRequest) return;
      installedMods = Array.isArray(result) ? result : [];
    } catch (e) {
      if (request !== installedModsRequest) return;
      installedModsError = e?.key && table[e.key] ? t(e.key) : e?.message || t('error.mods.failed');
    } finally {
      if (request === installedModsRequest) { installedModsLoading = false; renderInstalledMods(); }
    }
  }
  function renderInstalledMods() {
    const list = $('installed-mods-list');
    list.replaceChildren(); installedModSwitches = [];
    const query = String($('installed-mods-search').value || '').toLocaleLowerCase().trim();
    const visible = installedMods.filter(mod => `${mod.name || ''} ${mod.fileName || ''} ${mod.version || ''}`.toLocaleLowerCase().includes(query));
    for (const mod of visible) {
      const row = document.createElement('tr');
      row.className = mod.enabled ? '' : 'installed-mod-disabled';
      const projectCell = document.createElement('td');
      const project = document.createElement('div'); project.className = 'installed-mod-project';
      const icon = document.createElement('img'); icon.className = 'installed-mod-icon'; icon.alt = ''; icon.loading = 'lazy';
      icon.src = safeUrl(mod.icon, 'app://launcher/assets/mod-generic.svg');
      icon.onerror = () => { icon.onerror = null; icon.src = 'app://launcher/assets/mod-generic.svg'; };
      const details = document.createElement('div');
      const name = document.createElement('span'); name.className = 'installed-mod-name'; name.textContent = mod.name || mod.fileName || '';
      const status = document.createElement('span'); status.className = 'installed-mod-state'; status.textContent = t(mod.enabled ? 'installedMods.enabled' : 'installedMods.disabled');
      details.append(name, status); project.append(icon, details); projectCell.append(project);
      const versionCell = document.createElement('td');
      const version = document.createElement('span'); version.className = 'installed-mod-version'; version.textContent = mod.version || '—';
      const file = document.createElement('span'); file.className = 'installed-mod-filename'; file.textContent = mod.fileName || ''; file.dir = 'ltr';
      versionCell.append(version, file);
      const actions = document.createElement('td'); actions.className = 'installed-mods-actions';
      const toggle = document.createElement('label'); toggle.className = 'installed-mod-toggle';
      const input = document.createElement('input'); input.type = 'checkbox'; input.checked = !!mod.enabled;
      input.setAttribute('role', 'switch');
      input.setAttribute('aria-label', t(mod.enabled ? 'installedMods.disable' : 'installedMods.enable', { name: mod.name || mod.fileName || '' }));
      input.addEventListener('change', () => setInstalledModEnabled(mod, input.checked));
      const track = document.createElement('span'); track.className = 'installed-mod-toggle-track'; track.setAttribute('aria-hidden', 'true');
      toggle.append(input, track); actions.append(toggle); installedModSwitches.push(input);
      row.append(projectCell, versionCell, actions); list.append(row);
    }
    text('installed-mods-count', t('installedMods.count', { count: installedMods.length }));
    text('installed-mods-empty', t(query && installedMods.length ? 'installedMods.noResults' : 'installedMods.empty'));
    $('installed-mods-empty').hidden = installedModsLoading || visible.length > 0;
    updateInstalledModControls();
  }
  async function setInstalledModEnabled(mod, enabled) {
    if (installedModsMutating || !installedModEditsAllowed()) { renderInstalledMods(); return; }
    installedModsMutating = true; installedModsError = ''; updateInstalledModControls();
    try {
      const result = await send('setInstalledModEnabled', { fileName: mod.fileName, enabled });
      installedMods = Array.isArray(result) ? result : installedMods;
    } catch (e) { showError(e); }
    finally { installedModsMutating = false; renderInstalledMods(); }
  }
  async function importInstalledMods() {
    if (installedModsMutating || !installedModEditsAllowed()) return;
    installedModsMutating = true; installedModsError = ''; updateInstalledModControls();
    const existing = new Set(installedMods.map(mod => mod.fileName));
    try {
      const result = await send('importMods');
      if (Array.isArray(result)) {
        installedMods = result;
        if (result.some(mod => !existing.has(mod.fileName))) toast(t('installedMods.imported'));
      }
    } catch (e) { showError(e); }
    finally { installedModsMutating = false; renderInstalledMods(); }
  }
  const MOD_ICON_FALLBACK = 'app://launcher/assets/mod-generic.svg';
  function renderMods(list = mods) {
    mods = list || []; $('mods-list').replaceChildren();
    for (const m of mods) {
      const o = m.mod || m;
      // A <label> makes the whole row the hit area: the switch alone is a 42px
      // target in a 60px row, which is what made it feel misaligned rather than
      // any actual offset.
      const row = document.createElement('label'); row.className = 'mod-row ' + (m.enabled ? 'is-enabled' : 'is-disabled');
      const description = localized(o.description);
      row.innerHTML = `<img class="mod-icon" src="${escapeHtml(safeUrl(o.icon, MOD_ICON_FALLBACK))}" alt="" loading="lazy">`
        + `<span class="mod-body">`
        + `<span class="mod-name">${escapeHtml(o.name || o.id)}${m.isNew ? `<span class="mod-new">${escapeHtml(t('common.new'))}</span>` : ''}</span>`
        // An empty description still reserved its margins and pushed the rest
        // of the block off the row's centre line, so it is omitted entirely.
        + (description ? `<span class="mod-desc">${escapeHtml(description)}</span>` : '')
        + (o.category ? `<span class="mod-tag">${escapeHtml(o.category)}</span>` : '')
        + `</span>`;
      row.querySelector('.mod-icon').onerror = e => { e.target.onerror = null; e.target.src = MOD_ICON_FALLBACK; };
      const sw = document.createElement('input'); sw.type = 'checkbox'; sw.className = 'switch'; sw.checked = !!m.enabled; sw.setAttribute('aria-label', t('common.enabled', { name: o.name || o.id }));
      const status = document.createElement('span'); status.className = 'mod-state';
      const paint = () => { row.classList.toggle('is-enabled', sw.checked); row.classList.toggle('is-disabled', !sw.checked); status.textContent = t(sw.checked ? 'installedMods.enabled' : 'installedMods.disabled'); };
      paint();
      sw.onchange = async () => { sw.disabled = true; paint(); try { await send('toggleOptionalMod', { id: o.id, enabled: sw.checked }); renderTotals(); } catch (e) { sw.checked = !sw.checked; paint(); showError(e); } finally { sw.disabled = false; } };
      row.querySelector('.mod-body').append(status);
      row.append(sw); $('mods-list').append(row);
    }
    $('mods-empty').hidden = mods.length > 0; renderTotals();
  }
  // Counts from the checkboxes on screen, so toggling one updates the tally
  // without waiting for the backend to echo a new list back.
  function renderTotals() {
    const boxes = [...$('mods-list').querySelectorAll('.switch')];
    text('mods-total', boxes.length ? `${boxes.filter(x => x.checked).length} / ${boxes.length} ${t('onboarding.optionalMods.selected')}` : '');
  }
  function renderServers(list) {
    $('server-list').replaceChildren();
    for (const s of list) { const row = document.createElement('li'); row.className = 'server-row'; row.innerHTML = `<strong>${escapeHtml(s.name)}</strong><span class="${s.online ? 'status-online' : 'status-offline'}">${escapeHtml(t(s.online ? 'main.servers.online' : 'main.servers.offline'))}</span>`; $('server-list').append(row); }
    $('server-empty').hidden = list.length > 0;
  }
  function renderNews(list) {
    $('news-list').replaceChildren();
    for (const n of list.slice(0, 3)) {
      const row = document.createElement('li');
      row.className = 'news-row';
      // Long items were simply clipped with no way to read the rest; now the
      // whole row toggles open, and only rows that actually overflow say so.
      row.innerHTML = `<strong>${escapeHtml(localized(n.title))}</strong><p>${escapeHtml(localized(n.body))}</p>`
        + (n.autoTranslated ? `<span class="news-auto">${escapeHtml(t('main.news.autoTranslated'))}</span>` : '');
      row.tabIndex = 0;
      row.setAttribute('role', 'button');
      const toggle = () => {
        const open = row.classList.toggle('is-open');
        row.setAttribute('aria-expanded', String(open));
      };
      row.addEventListener('click', toggle);
      row.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
      $('news-list').append(row);
      // Clamping is a CSS effect, so overflow can only be measured after layout.
      requestAnimationFrame(() => {
        const body = row.querySelector('p');
        if (body && body.scrollHeight > body.clientHeight + 1) row.classList.add('is-clipped');
      });
    }
    $('news-empty').hidden = list.length > 0; $('news-section').hidden = list.length === 0;
  }
  async function loadExternalLaunchers() {
    if (externalLoading) return;
    externalLoading = true;
    try {
      const list = await send('getExternalLaunchers'); $('launcher-list').replaceChildren();
      for (const item of list) {
        const card = document.createElement('article'); card.className = 'launcher-card';
        card.innerHTML = `<img class="launcher-mark" src="${escapeHtml(safeUrl(item.iconUrl))}" alt="" width="44" height="44"><div class="launcher-card-body"><h2>${escapeHtml(item.name)}</h2><p>${escapeHtml(t(item.detected ? 'onboarding.external.detected' : 'onboarding.external.notDetected'))}${item.installationKind ? ' · ' + escapeHtml(item.installationKind) : ''}</p><details><summary>${escapeHtml(t('onboarding.external.details'))}</summary><small>${escapeHtml(item.root || '')}</small></details></div><button type="button" data-select-launcher class="btn ${item.detected ? 'btn-primary' : 'btn-ghost'}" ${item.canManage ? '' : 'disabled'}>${escapeHtml(t(!item.canManage ? 'onboarding.external.unavailable' : item.detected ? 'onboarding.external.select' : 'onboarding.external.chooseFolder'))}</button>`;
        card.querySelector('img').onerror = e => { e.target.onerror = null; e.target.src = 'app://launcher/assets/launchers/generic.svg'; };
        const selectLauncher = chooseFolder => choose(async () => {
          const result = await send('selectExternalLauncher', { id: item.id, chooseFolder });
          if (!result?.selected) return;
          await refreshState(); screen(editingSettings ? 'settings' : state.screen);
        });
        card.querySelector('[data-select-launcher]').onclick = () => selectLauncher(['modrinth', 'curseforge'].includes(item.id) || !item.detected);
        if (item.canManage && item.detected) {
          const folder = document.createElement('button'); folder.type = 'button'; folder.className = 'btn btn-ghost btn-small';
          folder.textContent = t('onboarding.external.chooseFolder'); folder.onclick = () => selectLauncher(true);
          card.querySelector('details').append(folder);
        }
        $('launcher-list').append(card);
      }
    } catch (e) { showError(e); }
    finally { externalLoading = false; }
  }
  function escapeHtml(x) { return String(x ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
  // Remote https is allowed on purpose: mod icons come from Modrinth's and
  // CurseForge's CDNs. The fallback is per caller — a mod with no icon must not
  // fall back to the DUSMP logo, which is what made every row look identical.
  // base64 data URLs carry the real icon of a launcher installed on this
  // machine, which the asset scheme cannot serve from disk. Restricted to the
  // two image types we inline; an <img> never executes what a data URL holds.
  function safeUrl(x, fallback = 'app://launcher/assets/icon.png') {
    return /^(https:\/\/|(?:app:\/\/launcher|http:\/\/localhost)\/assets\/|data:image\/(?:png|svg\+xml);base64,)/.test(x || '') ? x : fallback;
  }
  function toast(msg) { const e = document.createElement('div'); e.className = 'toast'; e.textContent = msg; $('toasts').append(e); setTimeout(() => e.remove(), 6000); }
  function showError(e) { if (['error.auth.cancelled', 'error.cancelled'].includes(e?.key)) return; toast(e?.key === 'error.modpack.configConflict' && e?.message ? e.message : e?.key && table[e.key] ? t(e.key) : e?.message || t('error.unknown')); }
  function showAuthError(message, detail) {
    $('auth-error').hidden = false;
    text('auth-error-text', message);
    $('auth-error-details').hidden = !detail;
    text('auth-error-detail', detail || '');
    $('device-panel').hidden = false;
    $('account-cards').hidden = false;
    $('auth-body').hidden = true;
    $('auth-or').hidden = true;
    $('auth-body').hidden = true; $('auth-or').hidden = true;
    $('btn-open-verify').disabled = true;
    $('btn-open-verify').hidden = true;
    $('btn-device-fallback').hidden = true;
    text('device-instructions', '');
  }

  function showAccountChoices() {
    $('offline-panel').hidden = true;
    $('device-panel').hidden = true;
    $('account-cards').hidden = false;
    clearAuthError();
  }

  function clearAuthError() {
    $('auth-error').hidden = true;
    text('auth-error-text', ''); text('auth-error-detail', '');
    $('auth-body').hidden = false;
    $('auth-body').hidden = true; $('auth-or').hidden = true;
  }

  function showDevice(p) {
    if (!authActive || authCancelPromise) return;
    const browser = p.mode === 'browser', opened = p.browserOpened === true;
    clearAuthError();
    $('device-panel').hidden = false; $('offline-panel').hidden = true; $('account-cards').hidden = true;

    deviceUrl = browser ? p.browserUri : p.verificationUriComplete || p.verificationUri;
    text('btn-open-verify', t(opened ? 'onboarding.account.browser.reopen' : 'onboarding.account.device.openUrl'));
    $('btn-open-verify').hidden = false;
    $('btn-open-verify').disabled = !deviceUrl;

    const hasCode = !browser && !!p.userCode;
    const requiresCode = hasCode && !p.verificationUriComplete;
    // "Or" only makes sense once the button above it is actually offered.
    $('auth-or').hidden = !hasCode || $('btn-open-verify').hidden;
    $('auth-body').hidden = !hasCode;
    $('device-code-block').hidden = !hasCode;
    $('device-qr').hidden = !hasCode;
    text('device-code', p.userCode || '');
    text('device-instructions', t(requiresCode ? 'onboarding.account.device.codeRequired'
      : opened ? 'onboarding.account.browser.instructions'
      : 'onboarding.account.device.browserUnavailable'));

    $('btn-device-fallback').hidden = !browser;

    if (hasCode && typeof qrcode === 'function') {
      const qr = qrcode(0, 'M'); qr.addData(deviceUrl); qr.make();
      $('device-qr').innerHTML = qr.createSvgTag({ scalable: true, margin: 2 });
      $('device-qr').setAttribute('aria-label', t('onboarding.account.device.qrAlt'));
    }
  }

  function loginMicrosoft(payload = {}) {
    if (authPromise || authCancelPromise) return authPromise || authCancelPromise;
    const generation = ++authGeneration;
    authActive = true; $('btn-login-microsoft').disabled = true;
    text('btn-login-microsoft', t('onboarding.account.device.waiting'));
    $('device-panel').hidden = false; $('offline-panel').hidden = true; $('account-cards').hidden = true;
    clearAuthError();
    text('device-instructions', t('onboarding.account.device.waiting'));
    $('auth-or').hidden = true; $('auth-body').hidden = true; $('device-code-block').hidden = true; $('device-qr').hidden = true;
    $('btn-open-verify').hidden = true; $('btn-open-verify').disabled = true; $('btn-device-fallback').hidden = true;
    authPromise = (async () => {
      try {
        await send('loginMicrosoft', payload);
        if (generation !== authGeneration) return;
        const nextState = await send('getState');
        if (generation !== authGeneration) return;
        renderState(nextState); if (editingSettings) screen('settings');
      } catch (e) {
        if (generation !== authGeneration) return;
        if (['error.auth.cancelled', 'error.cancelled'].includes(e?.key)) return;
        // A sign-in failure is not a toast: it is the screen's whole subject,
        // and the detail is what makes it diagnosable instead of "try again".
        const message = e?.key && table[e.key] ? t(e.key) : t('error.unknown');
        const detail = e?.message && e.message !== e?.key ? e.message : '';
        showAuthError(message, detail);
      }
      finally {
        if (generation === authGeneration) {
          authActive = false; authPromise = null; $('btn-login-microsoft').disabled = false;
          text('btn-login-microsoft', t('onboarding.account.microsoft.action')); resetAuth();
        }
      }
    })();
    return authPromise;
  }
  function cancelMicrosoftLogin() {
    if (authCancelPromise) return authCancelPromise;
    const pendingLogin = authPromise;
    if (!pendingLogin) { resetAuth(); return Promise.resolve(); }
    ++authGeneration; authActive = false;
    for (const id of ['btn-device-fallback', 'btn-cancel-device', 'account-back']) $(id).disabled = true;
    authCancelPromise = (async () => {
      try { await send('cancelMicrosoftLogin'); }
      finally {
        // Settle the old request before allowing another sign-in to own the panel.
        await pendingLogin;
        authPromise = null; authCancelPromise = null; $('btn-login-microsoft').disabled = false;
        text('btn-login-microsoft', t('onboarding.account.microsoft.action'));
        for (const id of ['btn-device-fallback', 'btn-cancel-device', 'account-back']) $(id).disabled = false;
        resetAuth();
      }
    })();
    return authCancelPromise;
  }
  function resetAuth() {
    // Keep the panel up when it is showing a failure, so the reason and the
    // retry button survive the cleanup that runs after every attempt.
    const failed = !$('auth-error').hidden;
    $('device-panel').hidden = !failed;
    $('account-cards').hidden = false;
    $('device-code-block').hidden = true;
    $('auth-body').hidden = true; $('auth-or').hidden = true;
    $('device-qr').replaceChildren(); text('device-code', ''); deviceUrl = '';
    if (!failed) { $('auth-or').hidden = true; text('device-instructions', ''); }
  }
  async function refreshState() { renderState(await send('getState')); }
  async function setup() {
    const b = await send('ready'); table = b.language?.table || {}; document.documentElement.lang = b.language?.tag || 'en-US'; document.documentElement.dir = b.language?.rtl ? 'rtl' : 'ltr'; setText(); renderState(b.state || {});
    await Promise.all([send('getLanguages').then(x => { languages = x.languages || []; selectedLanguage = x.current; renderLanguages(); }), send('getCountries').then(x => { countries = x.countries || []; selectedCountry = x.current; renderCountries(); })]);
  }
  function bind(id, fn) { $(id)?.addEventListener('click', () => Promise.resolve().then(fn).catch(showError)); }
  // Stepping back through the setup is reported to the host.
  //
  // The host picks the next step from the one it believes is current, so a page
  // that walks backwards on its own leaves the two disagreeing: after stepping
  // back from the country step and signing in with a different kind of account,
  // the host saw a step it had already passed and moved nobody, stranding the
  // setup on a screen with no way on.
  async function goBack(target) {
    if (editingSettings) return screen('settings');
    if (state.screen === 'main') return screen('main');
    // Read the host back rather than assuming it agreed: that keeps the page's
    // copy of the current step true, which is what the next sign-in is judged
    // against. The page still moves even if the host would not — outside the
    // setup it owns its own navigation, and a refusal there is not the player's
    // problem.
    try { await send('goToScreen', { screen: target }); await refreshState(); }
    catch { /* ignored on purpose */ }
    screen(target);
  }
  bind('playmode-dusmp', async () => { await send('setPlayMode', { mode: 'dusmp' }); await refreshState(); screen(state.screen); });
  bind('playmode-external', async () => { if (!editingSettings) { await send('setPlayMode', { mode: 'external' }); await refreshState(); } screen(editingSettings ? 'onboarding-external-launcher' : state.screen); });
  bind('playmode-back', () => goBack('onboarding-device'));
  bind('device-low8gb', () => selectDevicePreset('low8gb'));
  bind('device-default', () => selectDevicePreset('standard'));
  bind('device-smooth', () => selectDevicePreset('smooth'));
  bind('device-back', () => goBack('onboarding-language'));
  bind('external-back', () => goBack('onboarding-play-mode'));
  bind('btn-login-microsoft', () => loginMicrosoft());
  bind('btn-auth-retry', () => { clearAuthError(); return loginMicrosoft(); });
  bind('btn-offline-open', () => { $('offline-panel').hidden = false; $('account-cards').hidden = true; $('device-panel').hidden = true; $('offline-username').focus(); });
  bind('btn-offline-cancel', () => showAccountChoices());
  bind('btn-offline-continue', async () => { await send('loginOffline', { username: $('offline-username').value }); await refreshState(); if (editingSettings) screen('settings'); });
  bind('btn-open-verify', () => send('openExternal', { url: deviceUrl }));
  $('device-link').onclick = e => { e.preventDefault(); send('openExternal', { url: deviceUrl }).catch(showError); };
  bind('btn-copy-code', async () => { if (!navigator.clipboard) return toast(t('common.copyFailed')); try { await navigator.clipboard.writeText($('device-code').textContent); toast(t('onboarding.account.device.copySuccess')); } catch { toast(t('common.copyFailed')); } });
  bind('btn-device-fallback', async () => { if (authCancelPromise) return; await cancelMicrosoftLogin(); await loginMicrosoft({ useDeviceCode: true }); });
  bind('btn-cancel-device', () => cancelMicrosoftLogin());
  bind('mods-continue', async () => { if (editingSettings) return screen('settings'); await send('finishOnboarding'); await refreshState(); });
  bind('btn-optional-mods', () => { editingSettings = false; screen('onboarding-optional-mods'); });
  bind('btn-sync', async () => {
    if (syncBusy) return;
    syncBusy = true; renderState(state);
    try { const result = await send('syncNow'); await refreshState(); if (result?.completed) toast(t('main.syncComplete')); }
    catch (e) { showError(e); }
    finally { syncBusy = false; renderState(state); }
  });
  bind('btn-play', async () => {
    const play = $('btn-play');
    if (play.dataset.action === 'stop') {
      play.disabled = true;
      text('btn-play', t('main.stopping'));
      try { await send('stopGame'); } finally { play.disabled = false; }
      return;
    }
    await send('startGame');
  });
  bind('btn-settings', async () => { fillSettings(await send('getSettings')); editingSettings = true; renderState(state); screen('settings'); });
  // Reachable from the error screen too, so a broken start never traps the
  // player on a screen with no way to change language or account.
  bind('btn-error-settings', async () => { fillSettings(await send('getSettings')); editingSettings = true; renderState(state); screen('settings'); });
  bind('settings-back', () => { editingSettings = false; screen(state.screen || 'main'); });
  // The player chip is the way in, the way Yerium does it.
  // Appearance lives in its own window, the way Prism does it, so the player can
  // keep it open beside the launcher while they try skins.
  // With a second account signed in the chip opens the list instead, because
  // switching is then the more likely intent; the drawer's own rows still lead
  // to the skin window.
  bind('main-player', () => state.playMode === 'external' ? $('btn-settings').click() : (accounts.length > 1 ? toggleDrawerFromChip() : send('openSkinWindow')));
  bind('btn-accounts', () => (drawerOpen ? closeDrawer() : openDrawer()));
  bind('btn-folder', () => send('openInstanceFolder'));
  bind('btn-installed-mods', () => { installedModsReturn = uiScreen === 'settings' ? 'settings' : 'main'; screen('installed-mods'); });
  bind('installed-mods-back', () => screen(installedModsReturn === 'settings' ? 'settings' : state.screen || 'main'));
  bind('installed-mods-upload', importInstalledMods);
  $('installed-mods-search').oninput = renderInstalledMods;
  bind('edition-back', () => goBack('onboarding-country'));
  bind('btn-add-account', () => { closeDrawer(); resetAuth(); $('offline-panel').hidden = true; screen('onboarding-account'); });
  function toggleDrawerFromChip() { drawerOpen ? closeDrawer() : openDrawer(); }
  $('account-list')?.addEventListener('click', e => {
    const row = e.target.closest('.account-row');
    if (row) Promise.resolve().then(() => accountAction('select', row.dataset.accountId)).catch(showError);
  });
  $('account-list')?.addEventListener('contextmenu', e => {
    const row = e.target.closest('.account-row');
    if (!row) return;
    e.preventDefault();
    openAccountMenu(row.dataset.accountId, e.clientX, e.clientY);
  });
  // Right-clicking the chip means the account in use, which saves opening the
  // drawer first when there is only one.
  $('player-chip-wrap')?.addEventListener('contextmenu', e => {
    if (e.target.closest('#account-drawer') || !activeAccountId) return;
    e.preventDefault();
    openAccountMenu(activeAccountId, e.clientX, e.clientY);
  });
  $('account-menu')?.addEventListener('click', e => {
    const item = e.target.closest('[data-account-action]');
    if (!item || item.disabled) return;
    const id = menuAccountId;
    closeAccountMenu();
    Promise.resolve().then(() => accountAction(item.dataset.accountAction, id)).catch(showError);
  });
  // Capture, so a click anywhere else dismisses these before it does anything
  // else. A click inside the menu is left alone: closing it first would clear
  // the account the item is about.
  document.addEventListener('pointerdown', e => {
    if (e.target.closest('#account-menu')) return;
    closeAccountMenu();
    if (!e.target.closest('#account-drawer, #btn-accounts')) closeDrawer();
  }, true);
  document.addEventListener('keydown', e => { if (e.key === 'Escape') closeDrawer(); });
  bind('btn-save-settings', async () => { await send('saveSettings', readSettings()); editingSettings = false; screen('main'); toast(t('common.saved')); });
  bind('settings-language-btn', () => screen('onboarding-language')); bind('settings-country-btn', () => screen('onboarding-country'));
  bind('settings-launcher-btn', () => screen('onboarding-play-mode'));
  bind('settings-device-btn', () => screen('onboarding-device'));
  bind('settings-account-btn', () => { if (state.playMode === 'external') return screen('onboarding-external-launcher'); resetAuth(); $('offline-panel').hidden = true; screen('onboarding-account'); });
  bind('settings-logout-btn', async () => { editingSettings = false; await send('logout'); });
  bind('settings-mods-btn', () => { text('mods-continue', t('common.done')); screen('onboarding-optional-mods'); });
  bind('btn-repair', async () => { editingSettings = false; screen('main'); await send('repairInstallation'); });
  // Destructive, so it asks once in place rather than firing on the first click.
  // A second click within the window confirms; anything else cancels it.
  let resetArmed = null;
  bind('btn-reset', async () => {
    const button = $('btn-reset');
    if (!resetArmed) {
      resetArmed = setTimeout(() => { resetArmed = null; text('btn-reset', t('settings.reset')); button.classList.remove('is-armed'); }, 6000);
      text('btn-reset', t('settings.resetConfirm'));
      button.classList.add('is-armed');
      return;
    }
    clearTimeout(resetArmed); resetArmed = null;
    text('btn-reset', t('settings.reset')); button.classList.remove('is-armed');
    editingSettings = false; screen('main');
    await send('resetInstallation');
    await refreshState();
  });
  bind('btn-open-folder', () => send('openInstanceFolder'));
  bind('btn-retry', async () => { editingSettings = false; await send('retryStartup'); }); bind('btn-error-close', () => send('windowClose'));
  bind('language-back', () => screen('settings')); bind('account-back', async () => { await cancelMicrosoftLogin(); await goBack('onboarding-play-mode'); });
  bind('country-back', () => goBack(state.playMode === 'external' ? 'onboarding-external-launcher' : 'onboarding-account'));
  bind('mods-back', () => goBack('onboarding-country'));
  $('language-search').oninput = e => renderLanguages(e.target.value); $('country-search').oninput = e => renderCountries(e.target.value);
  $('set-concurrency').oninput = e => $('set-concurrency-value').value = e.target.value;
  $('set-ram').oninput = e => text('set-ram-value', `${e.target.value} MB`);
  $('offline-username').oninput = async () => { const sequence = ++validationSequence; try { const r = await send('validateOfflineName', { username: $('offline-username').value }); if (sequence !== validationSequence) return; text('offline-error', r.errorKey ? t(r.errorKey) : ''); $('btn-offline-continue').disabled = !!r.errorKey; } catch (e) { showError(e); } };
  $('offline-username').onkeydown = e => { if (e.key === 'Enter' && !$('btn-offline-continue').disabled) $('btn-offline-continue').click(); };
  function fillSettings(s) {
    settingsJvmPreset = s.jvmPreset ?? null;
    settingsMinRamMb = s.minRamMb ?? 512;
    renderDevicePreset();
    $('set-ram').value = s.maxRamMb || 4096; text('set-ram-value', `${$('set-ram').value} MB`); $('set-java-path').value = s.javaPath || ''; $('set-jvm-args').value = s.extraJvmArgs || '';
    $('set-width').value = s.windowWidth || 854; $('set-height').value = s.windowHeight || 480; $('set-fullscreen').checked = !!s.fullscreen; $('set-close-on-launch').checked = !!s.closeOnLaunch;
    $('set-concurrency').value = s.downloadConcurrency || 6; $('set-concurrency-value').value = $('set-concurrency').value;
  }
  function readSettings() { return { jvmPreset: settingsJvmPreset, maxRamMb: +$('set-ram').value, minRamMb: Math.min(settingsMinRamMb, +$('set-ram').value), javaPath: $('set-java-path').value || null, extraJvmArgs: $('set-jvm-args').value, windowWidth: +$('set-width').value, windowHeight: +$('set-height').value, fullscreen: $('set-fullscreen').checked, closeOnLaunch: $('set-close-on-launch').checked, downloadConcurrency: +$('set-concurrency').value }; }
  window.external.receiveMessage(raw => {
    try { const m = JSON.parse(raw); if (m.kind === 'event') event(m.event, m.payload); else if (m.kind === 'response') { const p = pending.get(String(m.id)); if (p) { pending.delete(String(m.id)); clearTimeout(p.timer); m.ok ? p.resolve(m.result) : p.reject(Object.assign(Error(m.errorDetail || m.errorKey), { key: m.errorKey })); } } }
    catch (e) { console.error(e); }
  });
  document.addEventListener('DOMContentLoaded', () => setup().catch(showError));
})();
