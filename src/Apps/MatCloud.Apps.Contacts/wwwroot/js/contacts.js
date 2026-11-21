/* ThixContacts in MatCloud.
   1. The selection: the boxes of the rows ([data-select-row], all at once [data-select-all]) enable the actions by what they need (data-needs: "some",
      "several" = at least two); the export dialog says whether the selection or everything goes.
   2. The form of a contact: "hinzufügen" ([data-add-row="Email"]) adds a row from its <template data-row-template>, the x of a row ([data-remove-row])
      removes it.
   3. Live: the list ([data-live] with data-live-revision) waits on /live/contacts/revision?since=N and reloads its rows when a device changed a contact;
      never under an open dialog or while rows are selected. Offline it waits for "online"; after a sign-out (401) it stops. */
(function () {
  'use strict';

  // ---- 1. the selection ----

  function boxes() { return Array.prototype.slice.call(document.querySelectorAll('[data-select-row]')); }

  function selected() { return boxes().filter(function (b) { return b.checked; }); }

  function updateSelection() {
    var count = selected().length;
    document.querySelectorAll('[data-needs]').forEach(function (el) {
      var need = el.getAttribute('data-needs');
      el.disabled = !(need === 'some' ? count > 0 : need === 'several' ? count > 1 : false);
    });
    boxes().forEach(function (b) { var row = b.closest('[data-item]'); if (row) { row.classList.toggle('is-selected', b.checked); } });
    var all = document.querySelector('[data-select-all]');
    if (all) {
      var n = boxes().length;
      all.checked = n > 0 && count === n;
      all.indeterminate = count > 0 && count < n;
    }
    var hint = document.querySelector('[data-export-hint]');
    if (hint) {
      hint.textContent = count === 0 ? 'Ohne Auswahl werden alle Kontakte exportiert.'
        : count === 1 ? 'Der ausgewählte Kontakt wird exportiert.' : count + ' ausgewählte Kontakte werden exportiert.';
    }
  }

  document.addEventListener('change', function (e) {
    var t = e.target;
    if (t.matches && t.matches('[data-select-all]')) {
      boxes().forEach(function (b) { b.checked = t.checked; });
    }
    if (t.matches && (t.matches('[data-select-row]') || t.matches('[data-select-all]'))) { updateSelection(); }
  });
  updateSelection();

  // ---- 2. rows of the form ----

  document.addEventListener('click', function (e) {
    var add = e.target.closest && e.target.closest('[data-add-row]');
    if (add) {
      var kind = add.getAttribute('data-add-row');
      var template = document.querySelector('template[data-row-template="' + kind + '"]');
      var rows = document.querySelector('[data-rows="' + kind + '"]');
      if (template && rows) {
        var row = template.content.firstElementChild.cloneNode(true);
        rows.appendChild(row);
        var field = row.querySelector('input, textarea');
        if (field) { field.focus(); }
      }
      return;
    }
    var remove = e.target.closest && e.target.closest('[data-remove-row]');
    if (remove) {
      var r = remove.closest('[data-row]');
      if (r) { r.remove(); }
    }
  });

  // ---- 3. live ----

  var root = document.querySelector('[data-live]');
  if (!root || !window.fetch) { return; }

  var revision = Number(root.getAttribute('data-live-revision') || '0');
  var failures = 0;
  var stopped = false;
  var running = false;

  function sleep(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  function busy() {
    return !!document.querySelector('dialog[open], .mat-menu:not([hidden])') || selected().length > 0;
  }

  function refresh() {
    return fetch(location.href, { credentials: 'same-origin', headers: { 'Accept': 'text/html' }, cache: 'no-store' }).then(function (r) {
      if (r.status === 401 || r.redirected && /\/Account\/Login/.test(r.url)) { stopped = true; return; }
      if (!r.ok) { throw new Error('HTTP ' + r.status); }
      return r.text().then(function (html) {
        var next = new DOMParser().parseFromString(html, 'text/html');
        var fresh = next.querySelector('[data-live]');
        if (!fresh) { return; }
        var region = root.querySelector('[data-live-region]');
        var freshRegion = fresh.querySelector('[data-live-region]');
        if (region && freshRegion) { region.replaceWith(document.importNode(freshRegion, true)); }
        var info = root.querySelector('.mat-toolbar__info');
        var freshInfo = fresh.querySelector('.mat-toolbar__info');
        if (info && freshInfo) { info.textContent = freshInfo.textContent; }
        revision = Math.max(revision, Number(fresh.getAttribute('data-live-revision') || '0'));
        root.setAttribute('data-live-revision', String(revision));
        updateSelection();
        if (window.MatUi && window.MatUi.refresh) { window.MatUi.refresh(root); }
      });
    });
  }

  function loop() {
    if (running) { return; }
    running = true;
    (function next() {
      if (stopped || navigator.onLine === false) { running = false; return; }
      fetch('/live/contacts/revision?since=' + revision + '&timeout=25', { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, cache: 'no-store' })
        .then(function (r) {
          if (r.status === 401 || r.status === 404) { stopped = true; return null; }
          if (!r.ok) { throw new Error('HTTP ' + r.status); }
          return r.json();
        })
        .then(function (answer) {
          failures = 0;
          if (!answer || answer.latest <= revision) { return; }
          var wait = function () { return busy() ? sleep(1500).then(wait) : refresh(); };
          return wait();
        })
        .catch(function () {
          failures++;
          return sleep(Math.min(30000, 1000 * Math.pow(2, Math.min(failures, 5))));
        })
        .then(next);
    })();
  }

  window.addEventListener('online', function () { refresh().catch(function () { /* the loop tries again */ }).then(loop); });
  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible' && !busy()) { refresh().catch(function () { /* the loop tries again */ }); }
  });
  loop();
})();
