// Browser checks of MatCloud against a running stack: the setup assistant (or the sign-in), the API of a device, the live list, every page at
// desktop width and at 390 px, light and dark, offline. Screenshots go to SHOTS (default: tests/e2e/shots); LOOK at them.
//
//   cd tests/e2e && npm install && node run.js http://localhost:4950
//
// The test accounts (local development only, see README.md): ADMIN_USER / ADMIN_PASSWORD, USER_NAME / USER_PASSWORD.
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const base = (process.argv[2] || 'http://localhost:4950').replace(/\/$/, '');
const shots = process.env.SHOTS || path.join(__dirname, 'shots');
const admin = { user: process.env.ADMIN_USER || 'admin', password: process.env.ADMIN_PASSWORD || 'MatCloud-Admin-2026', name: 'Matthias Admin' };
const person = { user: process.env.USER_NAME || 'anna', password: process.env.USER_PASSWORD || 'Anna-Test-2026', name: 'Anna Beispiel' };

let failures = 0;
function check(condition, message) {
  if (condition) { console.log('  ok   ' + message); } else { failures++; console.log('  FAIL ' + message); }
}

async function api(method, route, token, body, raw) {
  const headers = { 'User-Agent': 'ThixClip/e2e (windows)', 'X-MatCloud-Protocol': '1' };
  if (token) headers.Authorization = 'Bearer ' + token;
  let payload;
  if (raw) { headers['Content-Type'] = 'image/png'; payload = raw; } else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  const response = await fetch(base + route, { method, headers, body: payload });
  const type = response.headers.get('content-type') || '';
  const data = type.includes('json') ? await response.json() : type.includes('image') ? Buffer.from(await response.arrayBuffer()) : await response.text();
  return { status: response.status, data, headers: response.headers };
}

const crypto = require('crypto');
const sha = (input) => crypto.createHash('sha256').update(input).digest('hex').toUpperCase();
function textClip(text, minutesAgo, extra) {
  const at = new Date(Date.now() - minutesAgo * 60000).toISOString();
  const kind = /^https?:\/\/\S+$/.test(text.trim()) ? 'Link' : 'Text';
  return Object.assign({ syncId: crypto.randomUUID(), kind, text, contentHash: sha(Buffer.from(text, 'utf8')), createdAt: at, copiedAt: at, updatedAt: at }, extra || {});
}
function imageClip(png, minutesAgo, ocr) {
  const at = new Date(Date.now() - minutesAgo * 60000).toISOString();
  return { syncId: crypto.randomUUID(), kind: 'Image', ocrText: ocr || null, contentHash: sha(png), createdAt: at, copiedAt: at, updatedAt: at };
}

// A protected note of ThixNote (NoteEncryption): "QNSEC1" | iterations (int32 LE) | salt (16) | nonce (12) | tag (16) | ciphertext;
// PBKDF2-SHA256 (600000 rounds, 32 bytes), AES-256-GCM with the magic as additional data. The browser must read and write exactly this.
const notePassword = "Kennwort-2026";
function thixNoteEncrypt(text, password) {
  const magic = Buffer.from("QNSEC1"), salt = crypto.randomBytes(16), nonce = crypto.randomBytes(12), iterations = 600000;
  const key = crypto.pbkdf2Sync(password, salt, iterations, 32, "sha256");
  const cipher = crypto.createCipheriv("aes-256-gcm", key, nonce);
  cipher.setAAD(magic);
  const body = Buffer.concat([cipher.update(text, "utf8"), cipher.final()]);
  const count = Buffer.alloc(4); count.writeInt32LE(iterations);
  return Buffer.concat([magic, count, salt, nonce, cipher.getAuthTag(), body]);
}
function thixNoteDecrypt(bytes, password) {
  const magic = bytes.subarray(0, 6), iterations = bytes.readInt32LE(6), salt = bytes.subarray(10, 26), nonce = bytes.subarray(26, 38), tag = bytes.subarray(38, 54);
  const key = crypto.pbkdf2Sync(password, salt, iterations, 32, "sha256");
  const decipher = crypto.createDecipheriv("aes-256-gcm", key, nonce);
  decipher.setAAD(magic); decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(bytes.subarray(54)), decipher.final()]).toString("utf8");
}

async function shot(page, name) {
  await page.waitForTimeout(250);
  await page.screenshot({ path: path.join(shots, name + '.png'), fullPage: true });
}

/** The end of a page: the shell scrolls inside its main area, so that one is scrolled down first. */
async function shotEnd(page, name) {
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('*')) {
      const style = getComputedStyle(el);
      if (/(auto|scroll)/.test(style.overflowY) && el.scrollHeight > el.clientHeight + 4) { el.scrollTop = el.scrollHeight; }
    }
    window.scrollTo(0, document.body.scrollHeight);
  });
  await shot(page, name);
}

async function signIn(page, who) {
  await page.goto(base + '/Account/Login');
  await page.fill('#UserName', who.user);
  await page.fill('#Password', who.password);
  await Promise.all([page.waitForNavigation(), page.click('#login-form button[type=submit]')]);
}

(async () => {
  fs.mkdirSync(shots, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 860 }, locale: 'de-DE' });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });

  try {
    console.log('Setup / sign-in');
    await page.goto(base + '/');
    if (page.url().includes('/Account/Setup')) {
      await shot(page, '01-setup');
      await page.fill('#UserName', admin.user);
      await page.fill('#DisplayName', admin.name);
      await page.fill('#Password', admin.password);
      await page.fill('#Repeat', admin.password);
      await Promise.all([page.waitForNavigation(), page.click('#setup-form button[type=submit]')]);
      check(page.url() === base + '/', 'the setup assistant creates the administrator and opens the overview');
      await shot(page, '02-overview-empty');
    } else {
      await signIn(page, admin);
    }
    check(!page.url().includes('/Account/'), 'signed in as administrator');

    console.log('A second account (administration)');
    await page.goto(base + '/Admin/Users');
    if (!(await page.content()).includes('>' + person.name + '<')) {
      await page.goto(base + '/Admin/Users/Edit');
      await page.fill('#UserName', person.user);
      await page.fill('#DisplayName', person.name);
      await page.fill('#Password', person.password);
      await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
      check(page.url().endsWith('/Admin/Users'), 'a user is created by the administrator');
    }

    console.log('A device (API)');
    const login = await api('POST', '/api/v1/devices/login', null, { userName: admin.user, password: admin.password, deviceName: 'MATTHIAS-PC', platform: 'windows', appVersion: '0.1.7-20261114', app: 'clip' });
    check(login.status === 200 && login.data.token, 'device login answers a token');
    check(login.headers.get('x-matcloud-protocol') === '1', 'the protocol header is sent');
    const token = login.data.token;
    const mac = await api('POST', '/api/v1/devices/login', null, { userName: admin.user, password: admin.password, deviceName: 'MacBook Pro', platform: 'macos', appVersion: '0.1.7-20261114', app: 'clip' });

    const known = await api('GET', '/api/v1/clip/changes?since=0', token);
    if (known.data.clips.length < 5) {
      const todo = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'MatCloud', 'wwwroot', 'img', 'icon-512.png'));
      const chart = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'MatCloud', 'wwwroot', 'img', 'apple-touch-icon.png'));
      const clips = [
        textClip('Release-Plan Q2: Features, Tests, Rollout', 60 * 24 + 300),
        textClip('Design-Feedback:\nClean, einfach, sehr stimmig. 👍', 60 * 24 + 60, { isFavorite: true }),
        imageClip(chart, 60 * 24 + 20, null),
        imageClip(todo, 30, 'ThixClip\nZwischenablage für Windows und Mac'),
        textClip('https://www.example.com/produkt', 20),
        textClip('Produktmeeting morgen um 10 Uhr\nim Konferenzraum.', 5, { isPinned: true }),
      ];
      const push = await api('POST', '/api/v1/clip/push', token, { clips });
      check(push.status === 200 && push.data.results.length === clips.length, 'a push creates the clips');
      check(push.data.results[2].needsImage && push.data.results[3].needsImage, 'image clips ask for their PNG');
      const up1 = await api('PUT', '/api/v1/clip/' + clips[2].syncId + '/image', token, undefined, chart);
      const up2 = await api('PUT', '/api/v1/clip/' + clips[3].syncId + '/image', token, undefined, todo);
      check(up1.status === 200 && up2.status === 200, 'the PNGs are uploaded');
      const thumb = await api('GET', '/api/v1/clip/' + clips[3].syncId + '/thumbnail', mac.data.token);
      check(thumb.status === 200 && thumb.data.length > 100, 'another device downloads the thumbnail');

      // the same text from the Mac merges into the existing clip
      const merged = await api('POST', '/api/v1/clip/push', mac.data.token, { clips: [textClip('https://www.example.com/produkt', 1)] });
      check(merged.data.results[0].mergedInto === clips[4].syncId, 'the same content from another device is merged (mergedInto)');
    }

    console.log('History (browser)');
    await page.goto(base + '/Clip');
    await page.waitForSelector('.mc-clips');
    await shot(page, '03-history');
    const rows = await page.$$eval('.mc-clip', (r) => r.length);
    check(rows >= 5, 'the history shows the synced clips (' + rows + ')');
    check((await page.$$eval('.mat-itemlist__divider', (d) => d.map((x) => x.textContent))).some((t) => t.includes('Heute')), 'the clips are grouped by day');

    // live: a push of a device appears without a reload
    const liveText = 'Live vom Mac ' + new Date().toISOString();
    await api('POST', '/api/v1/clip/push', mac.data.token, { clips: [textClip(liveText, 0)] });
    const appeared = await page.waitForFunction((t) => document.body.innerText.includes(t), liveText, { timeout: 15000 }).then(() => true).catch(() => false);
    check(appeared, 'the list updates itself when another device pushes');

    // search (including the text recognised in a picture)
    await page.fill('#clips-q', 'zwischenablage');
    await Promise.all([page.waitForNavigation(), page.press('#clips-q', 'Enter')]);
    check((await page.$$eval('.mc-clip', (r) => r.length)) === 1, 'the search finds the OCR text of a picture');
    await shot(page, '04-history-search');
    await page.goto(base + '/Clip?kind=image');
    await shot(page, '05-history-images');

    console.log('One clip');
    await page.goto(base + '/Clip');
    const imageHref = await page.$eval('.mc-clip .mc-thumb', (t) => t.closest('a').getAttribute('href'));
    await page.goto(base + imageHref);
    await shot(page, '06-clip-image');
    await shotEnd(page, '06b-clip-image-end');
    check(await page.$('[data-copy-image]') !== null, 'an image clip has "In Zwischenablage kopieren"');
    const textHref = await page.$$eval('.mc-clip a.mat-item__main', (a) => a.map((x) => x.getAttribute('href')));
    await page.goto(base + '/Clip');
    const firstText = await page.$$eval('.mc-clip', (rows) => rows.filter((r) => !r.querySelector('.mc-thumb')).map((r) => r.querySelector('a').getAttribute('href'))[0]);
    await page.goto(base + firstText);
    await shot(page, '07-clip-text');
    check(await page.$('textarea#Text') !== null && await page.$('#Image') === null, 'a text clip has a text field and no upload');

    console.log('New entry');
    await page.goto(base + '/Clip/Edit');
    await shot(page, '08-new');
    check(await page.isVisible('#Text') && !(await page.isVisible('.mat-upload')), 'a new entry shows the text field for "Text"');
    await page.check('#Kind-1');
    await page.waitForTimeout(150);
    check(!(await page.isVisible('#Text')), 'the text field goes for "Bild"');
    await page.check('#Kind-0');
    const browserText = 'Aus dem Browser ' + Date.now();
    await page.fill('#Text', browserText);
    await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
    check(page.url().includes('/Clip/Edit?id='), 'a text from the browser is created');
    const changes = await api('GET', '/api/v1/clip/changes?since=0&limit=200', token);
    const created = changes.data.clips.find((c) => c.text === browserText);
    check(created && created.source === 'Web', 'the device sees the clip from the browser (source "Web")');

    // delete in the browser -> tombstone for the device
    const before = changes.data.latest;
    page.once('dialog', (d) => d.accept());
    await page.click('button.mat-btn--danger[formaction]');
    await page.waitForSelector('dialog[open]');
    await shot(page, '09-delete-confirm');
    await Promise.all([page.waitForNavigation(), page.click('dialog[open] .mat-btn--danger, dialog[open] .mat-btn--primary')]);
    const after = await api('GET', '/api/v1/clip/changes?since=' + before, token);
    const tomb = after.data.clips.find((c) => c.syncId === created.syncId);
    check(tomb && tomb.isDeleted === true && !tomb.text, 'a delete in the browser reaches the device as a tombstone');

    console.log('ThixNote: the sign-in in the browser and the general sync');
    const link = await api('POST', '/api/v1/devices/link', null, { deviceName: 'MATTHIAS-PC', platform: 'windows', appVersion: '1.1.9', app: 'note' });
    check(link.status === 200 && /^[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(link.data.code), 'a device starts the sign-in in the browser and gets a code');
    const pending = await api('POST', '/api/v1/devices/link/poll', null, { pollToken: link.data.pollToken });
    check(pending.data.state === 'Pending', 'before the confirmation the device waits');
    await page.goto(link.data.verificationUrl.replace(/^https?:\/\/[^/]+/, base));
    await shot(page, '30-connect');
    check((await page.content()).includes('MATTHIAS-PC') && (await page.content()).includes('ThixNote'), 'the page Connect names the device and the app');
    await Promise.all([page.waitForNavigation(), page.click('button:has-text("Verbinden")')]);
    await shot(page, '31-connected');
    const approved = await api('POST', '/api/v1/devices/link/poll', null, { pollToken: link.data.pollToken });
    check(approved.data.state === 'Approved' && approved.data.login && approved.data.login.token, 'after the confirmation the device gets its token');
    const noteToken = approved.data.login.token;
    const again = await api('POST', '/api/v1/devices/link/poll', null, { pollToken: link.data.pollToken });
    check(again.data.state === 'Expired', 'the token is given only once');
    // the notes as ThixNote pushes them: {path, content} with the SHA-256 of the text, a protected one as its encrypted bytes, the folders as items of their own
    const noteHash = (b) => crypto.createHash('sha256').update(b).digest('hex');
    const noteItem = (p, content, minutes) => ({ collection: 'notes', key: crypto.randomUUID(), data: JSON.stringify({ path: p, content }),
      contentHash: noteHash(Buffer.from(content, 'utf8')), updatedAt: new Date(Date.now() - minutes * 60000).toISOString() });
    const secretBytes = thixNoteEncrypt('WLAN: MatCloud-Gast\nKennwort: Sonnenblume42', notePassword);
    const notes = [
      noteItem('Einkauf.md', '# Einkauf\n\n- Milch\n- Brot\n- [ ] Kaffee', 30),
      noteItem('Projekte/MatCloud.md', '# MatCloud\n\nDie Cloud der **Thix-Apps**:\n\n| App | Art |\n|---|---|\n| ThixClip | Web-App und Sync |\n| ThixNote | Web-App und Sync |\n\n> Alles auf allen Geräten.', 10),
      noteItem('Projekte/Ideen.txt', 'Dunkles Logo testen\nPWA-Kürzel für Notizen', 5),
      { collection: 'notes', key: crypto.randomUUID(), data: JSON.stringify({ path: 'Privat/Zugangsdaten WLAN.txts', bytes: secretBytes.toString('base64') }),
        contentHash: noteHash(secretBytes), updatedAt: new Date(Date.now() - 60000).toISOString() },
      { collection: 'folders', key: 'Projekte', data: '{}', updatedAt: new Date().toISOString() },
      { collection: 'folders', key: 'Privat', data: '{}', updatedAt: new Date().toISOString() },
    ];
    const pushNotes = await api('POST', '/api/v1/sync/push', noteToken, { items: notes });
    check(pushNotes.status === 200 && pushNotes.data.results.every((r) => r.applied), 'ThixNote pushes its notes into the general sync');
    const noteChanges = await api('GET', '/api/v1/sync/changes?since=0', noteToken);
    check(noteChanges.status === 200 && noteChanges.data.items.length >= notes.length, 'and reads them back');
    check((await api('GET', '/api/v1/clip/changes?since=0', noteToken)).status === 403, 'a device of ThixNote cannot read the clipboard');
    check((await api('GET', '/api/v1/sync/changes?since=0', token)).status === 403, 'ThixClip has its own routes, no general sync');

    console.log('ThixNote in the browser');
    await page.goto(base + '/Note');
    await page.waitForSelector('.mc-notes');
    await shot(page, '40-notes');
    check((await page.$$eval('.mc-note', (r) => r.length)) >= 4, 'the notes of ThixNote are listed');
    await page.goto(base + '/Note?folder=Projekte');
    check((await page.$$eval('.mc-note', (r) => r.length)) === 2, 'a folder shows its notes');
    const projectKey = notes[1].key;
    await page.goto(base + '/Note/View?key=' + projectKey);
    await shot(page, '41-note-markdown');
    check(await page.$('.mc-markdown table') !== null && await page.$('.mc-markdown h1') !== null, 'Markdown is drawn as a page (headings, tables)');
    await page.goto(base + '/Note/Edit?key=' + projectKey);
    await shot(page, '42-note-edit');
    await page.fill('#Text', '# MatCloud\n\nGeändert im Browser.');
    await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
    const afterEdit = (await api('GET', '/api/v1/sync/changes?since=0', noteToken)).data.items.find((i) => i.key === projectKey);
    check(afterEdit && JSON.parse(afterEdit.data).content === '# MatCloud\n\nGeändert im Browser.' && afterEdit.source === 'Web'
      && afterEdit.contentHash === noteHash(Buffer.from('# MatCloud\n\nGeändert im Browser.', 'utf8')), 'ThixNote gets the change of the browser in its own format');
    await page.goto(base + '/Note/Edit?folder=Projekte');
    await page.fill('#Title', 'Aus dem Browser');
    await page.fill('#Text', 'Eine neue Notiz');
    await Promise.all([page.waitForNavigation(), page.click('form.mat-form button[type=submit].mat-btn--primary')]);
    const createdNote = (await api('GET', '/api/v1/sync/changes?since=0', noteToken)).data.items.find((i) => i.data && JSON.parse(i.data).path === 'Projekte/Aus dem Browser.md');
    check(!!createdNote, 'a note written in the browser reaches ThixNote');

    // a protected note: opened and saved only in the browser
    const secretKey = notes[3].key;
    await page.goto(base + '/Note/View?key=' + secretKey);
    await shot(page, '43-note-locked');
    await page.fill('#note-password', 'falsch');
    await page.click('[data-note-unlock] button[type=submit]');
    await page.waitForFunction(() => { const e = document.querySelector('[data-note-error]'); return e && !e.hidden; }, null, { timeout: 30000 });
    check(true, 'a wrong password is refused in the browser');
    await page.fill('#note-password', notePassword);
    await page.click('[data-note-unlock] button[type=submit]');
    await page.waitForSelector('[data-note-editor]:not([hidden])', { timeout: 30000 });
    check((await page.inputValue('#note-plain')).includes('Sonnenblume42'), 'the protected note is decrypted in the browser');
    await shot(page, '44-note-unlocked');
    await page.fill('#note-plain', 'WLAN: MatCloud-Gast\nKennwort: Mondblume7');
    await Promise.all([page.waitForNavigation(), page.click('[data-note-editor] button[type=submit]')]);
    const sealed = (await api('GET', '/api/v1/sync/changes?since=0', noteToken)).data.items.find((i) => i.key === secretKey);
    const sealedBytes = Buffer.from(JSON.parse(sealed.data).bytes, 'base64');
    check(thixNoteDecrypt(sealedBytes, notePassword) === 'WLAN: MatCloud-Gast\nKennwort: Mondblume7' && sealed.contentHash === noteHash(sealedBytes),
      'saved in the browser, ThixNote decrypts it with the same password');
    check(!sealed.data.includes('Mondblume7'), 'the server never sees the text of a protected note');

    console.log('Overview, apps, settings of the clipboard, administration');
    await page.goto(base + '/');
    await shot(page, '32-overview');
    check((await page.$$eval('.mc-app', (a) => a.length)) >= 2, 'the overview shows the apps');
    await page.goto(base + '/Apps');
    await shot(page, '33-apps');
    await page.goto(base + '/Apps/Detail?id=note');
    await shot(page, '34-app-note');
    check((await page.content()).includes('>notes<'), 'the page of ThixNote shows its collection');
    await page.goto(base + '/Clip/Settings');
    await shot(page, '35-clip-settings');
    await page.goto(base + '/Admin');
    await shot(page, '36-admin-system');
    await page.goto(base + '/Admin/Connections');
    await shot(page, '38-admin-connections');
    await page.goto(base + '/Admin/Apps');
    await shot(page, '37-admin-apps');
    // switch ThixNote off: its device gets 403 and its menu goes; on again: it syncs again
    await page.click('tr:has-text("ThixNote") button[type=submit]');
    await page.waitForSelector('dialog[open]');
    await Promise.all([page.waitForNavigation(), page.click('dialog[open] .mat-btn--danger, dialog[open] .mat-btn--primary')]);
    check((await api('GET', '/api/v1/sync/changes?since=0', noteToken)).status === 403, 'a switched-off app gets 403');
    await shot(page, '37b-admin-apps-off');
    await Promise.all([page.waitForNavigation(), page.click('tr:has-text("ThixNote") button[type=submit]')]);
    check((await api('GET', '/api/v1/sync/changes?since=0', noteToken)).status === 200, 'switched on again, it syncs again');
    const health = await api('GET', '/api/health');
    check(health.status === 200 && health.data.app === 'matcloud', 'the platform contract answers /api/health');

    console.log('Account, devices, administration, about');
    for (const [url, name] of [['/Account', '10-account'], ['/Account/Security', '11-security'], ['/Account/Devices', '12-devices'], ['/Admin/Users', '13-users'], ['/About', '15-about']]) {
      await page.goto(base + url);
      await shot(page, name);
    }
    await page.goto(base + '/Admin/Users');
    await page.click('text=' + person.name);
    await page.waitForLoadState();
    await shot(page, '14-user-edit');
    await page.goto(base + '/Account/Devices');
    await page.click('text=MacBook Pro');
    await page.waitForLoadState();
    await shot(page, '16-device');
    // remove the Mac: its token ends
    await page.click('button.mat-btn--danger[formaction]');
    await page.waitForSelector('dialog[open]');
    await Promise.all([page.waitForNavigation(), page.click('dialog[open] .mat-btn--danger, dialog[open] .mat-btn--primary')]);
    const revoked = await api('GET', '/api/v1/clip/changes?since=0', mac.data.token);
    check(revoked.status === 401, 'a device removed in the browser gets 401');
    await page.goto(base + '/Clip/Edit?id=999999');
    await shot(page, '17-not-found');

    console.log('Phone (390 px) and dark');
    for (const scheme of ['light', 'dark']) {
      const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, colorScheme: scheme, locale: 'de-DE' });
      const p = await phone.newPage();
      await signIn(p, admin);
      for (const [url, name] of [['/', 'overview'], ['/Apps', 'apps'], ['/Connect', 'connect'], ['/Note', 'notes'], ['/Note/View?key=' + projectKey, 'note'], ['/Clip', 'history'], [imageHref, 'clip-image'], [firstText, 'clip-text'], ['/Clip/Edit', 'new'], ['/Account', 'account'], ['/Account/Devices', 'devices'], ['/Admin/Users', 'users'], ['/Admin/Apps', 'admin-apps']]) {
        await p.goto(base + url);
        await shot(p, `20-phone-${scheme}-${name}`);
        await shotEnd(p, `20-phone-${scheme}-${name}-end`);
      }
      await p.goto(base + '/Clip');
      await p.click('.mat-burger');
      await p.waitForTimeout(400);
      await shot(p, `20-phone-${scheme}-menu`);
      const overflow = await p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      check(overflow <= 1, `no sideways scrolling at 390 px (${scheme})`);
      const fontSize = await p.$eval('#Text', (e) => parseFloat(getComputedStyle(e).fontSize)).catch(() => 16);
      check(fontSize >= 16, 'text fields have 16 px on a touch screen (no zoom on iOS)');
      await phone.close();
    }
    const dark = await browser.newContext({ viewport: { width: 1280, height: 860 }, colorScheme: 'dark', locale: 'de-DE' });
    const d = await dark.newPage();
    await signIn(d, admin);
    await d.goto(base + '/Clip');
    await shot(d, '21-desktop-dark-history');
    await d.goto(base + imageHref);
    await shot(d, '22-desktop-dark-clip');

    console.log('Offline (installed app)');
    await d.goto(base + '/Clip');
    await d.waitForFunction(() => navigator.serviceWorker && navigator.serviceWorker.controller !== null, null, { timeout: 15000 }).catch(() => d.reload());
    await d.waitForFunction(() => navigator.serviceWorker && navigator.serviceWorker.controller !== null, null, { timeout: 15000 }).catch(() => {});
    await d.goto(base + '/Clip');
    await d.waitForTimeout(500);
    await dark.setOffline(true);
    await d.goto(base + '/Clip').catch(() => {});
    await d.waitForTimeout(500);
    check((await d.$$eval('.mc-clip', (r) => r.length).catch(() => 0)) > 0, 'offline: the last history is still shown');
    check(await d.isVisible('#mc-offline'), 'offline: the top bar says "Offline"');
    await shot(d, '23-offline-history');
    await d.goto(base + '/Account/Security').catch(() => {});
    await d.waitForTimeout(500);
    check((await d.content()).includes('Keine Verbindung'), 'offline: another page shows the offline page');
    await shot(d, '24-offline-page');
    await dark.setOffline(false);
    // the offline page reloads by itself on the "online" event (and every 10 s); the button is the manual way
    const reconnected = await d.waitForFunction(() => !document.getElementById('mc-offline-page'), null, { timeout: 15000 }).then(() => true).catch(() => false);
    if (!reconnected) { await d.click('#mc-retry').catch(() => {}); await d.waitForTimeout(1500); }
    check(!(await d.content()).includes('Keine Verbindung') && d.url().includes('/Account/Security'), 'back online: the page that was asked for loads by itself');
    await shot(d, '25-back-online');
    await dark.close();

    console.log('Sign-out and the second account');
    await page.goto(base + '/Clip');
    await page.click('.mat-user');
    await Promise.all([page.waitForNavigation(), page.click('#user-menu button[type=submit]')]);
    check(page.url().includes('/Account/Login'), 'sign-out leads to the sign-in');
    await shot(page, '18-login');
    await signIn(page, person);
    check(page.url().includes('/Account/Security'), 'a password set by the administrator has to be changed first');
    await shot(page, '19-must-change-password');
  } finally {
    await browser.close();
  }

  if (errors.length) { console.log('Script errors on the pages:\n  ' + [...new Set(errors)].join('\n  ')); failures += errors.length; }
  console.log(failures === 0 ? '\nAll checks passed. Screenshots: ' + shots : `\n${failures} check(s) failed. Screenshots: ${shots}`);
  process.exit(failures === 0 ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(1); });
