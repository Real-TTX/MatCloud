/* ThixDrive in MatCloud: the lists of the Drive.
   1. The selection: the boxes of the rows ([data-select-row], all at once [data-select-all]) enable the actions below the list by what they need
      (data-needs: "some", "one", "one-file"); "Herunterladen" gets the address of the one file, "Umbenennen" its name.
   2. Uploads: the dialog #drive-upload (choose or drop files) and files dropped on the list ([data-drop-target]) go up in pieces to /live/drive/uploads,
      each with its progress. A name that is taken asks: a new version of that file, or keep both ("Angebot (2).docx").
   3. Live: the list ([data-live] with data-live-revision) waits on /live/drive/revision?since=N and reloads its rows when the Drive changed (another
      tab, ThixWrite on a computer); never under an open dialog. Offline it waits for "online"; after a sign-out (401) it stops. */
(function () {
  'use strict';

  var CHUNK = 8 * 1024 * 1024;
  var tokenInput = document.querySelector('[data-page-token] input[name="__RequestVerificationToken"]');
  var token = tokenInput ? tokenInput.value : '';

  // ---- 1. the selection ----

  function boxes() { return Array.prototype.slice.call(document.querySelectorAll('[data-select-row]')); }

  function selected() { return boxes().filter(function (b) { return b.checked; }); }

  function rowOf(box) { return box.closest('[data-item]'); }

  function updateSelection() {
    var chosen = selected();
    var one = chosen.length === 1 ? rowOf(chosen[0]) : null;
    document.querySelectorAll('[data-needs]').forEach(function (el) {
      var need = el.getAttribute('data-needs');
      var ok = need === 'some' ? chosen.length > 0 : need === 'one' ? chosen.length === 1 : need === 'one-file' ? !!one && one.getAttribute('data-kind') === 'file' : false;
      if (el.tagName === 'A') {
        el.classList.toggle('is-disabled', !ok);
        el.setAttribute('aria-disabled', ok ? 'false' : 'true');
        el.setAttribute('href', ok && one ? '/live/drive/' + one.getAttribute('data-item') + '/download' : '#');
      } else {
        el.disabled = !ok;
      }
    });
    boxes().forEach(function (b) { var row = rowOf(b); if (row) { row.classList.toggle('is-selected', b.checked); } });
    var all = document.querySelector('[data-select-all]');
    if (all) {
      var count = boxes().length;
      all.checked = count > 0 && chosen.length === count;
      all.indeterminate = chosen.length > 0 && chosen.length < count;
    }
    var name = document.querySelector('[data-rename-name]');
    if (name && one) { name.value = one.getAttribute('data-name') || ''; }
  }

  document.addEventListener('change', function (e) {
    var t = e.target;
    if (t.matches && t.matches('[data-select-all]')) {
      boxes().forEach(function (b) { b.checked = t.checked; });
    }
    if (t.matches && (t.matches('[data-select-row]') || t.matches('[data-select-all]'))) { updateSelection(); }
  });
  document.addEventListener('click', function (e) {
    var link = e.target.closest && e.target.closest('a.is-disabled');
    if (link) { e.preventDefault(); }
  });
  updateSelection();

  // ---- 2. uploads ----

  var dialog = document.getElementById('drive-upload');
  var list = dialog ? dialog.querySelector('[data-upload-list]') : null;
  var parent = dialog ? dialog.getAttribute('data-upload-parent') : null;
  var running = 0;
  var failed = 0;

  function api(method, url, body, type) {
    var headers = { 'RequestVerificationToken': token, 'Accept': 'application/json' };
    if (type) { headers['Content-Type'] = type; }
    return fetch(url, { method: method, credentials: 'same-origin', headers: headers, body: body }).then(function (r) {
      return r.text().then(function (text) {
        var data = null;
        try { data = text ? JSON.parse(text) : null; } catch (err) { data = null; }
        return { status: r.status, ok: r.ok, data: data };
      });
    });
  }

  function fail(answer) {
    var message = answer && answer.data && answer.data.message ? answer.data.message : 'Das Hochladen hat nicht geklappt (' + (answer ? answer.status : 'Netzwerk') + ').';
    throw new Error(message);
  }

  function row(file) {
    var li = document.createElement('li');
    li.className = 'mc-drive__upload';
    li.innerHTML = '<span class="mc-drive__upload-name"></span><span class="mc-drive__upload-state"></span>' +
      '<div class="mat-meter mat-meter--sm" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><div class="mat-meter__fill"></div></div>';
    li.querySelector('.mc-drive__upload-name').textContent = file.name;
    list.appendChild(li);
    return {
      progress: function (done) {
        var pct = file.size === 0 ? 100 : Math.round(done * 100 / file.size);
        li.querySelector('.mat-meter__fill').style.width = pct + '%';
        li.querySelector('[role="progressbar"]').setAttribute('aria-valuenow', String(pct));
        li.querySelector('.mc-drive__upload-state').textContent = pct + ' %';
      },
      done: function (text) { li.classList.add('is-done'); li.querySelector('.mc-drive__upload-state').textContent = text; },
      error: function (text) { li.classList.add('is-error'); li.querySelector('.mc-drive__upload-state').textContent = text; },
    };
  }

  function start(file) {
    var request = { size: file.size, parentId: parent ? Number(parent) : null, name: file.name, onConflict: 'Fail' };
    return api('POST', '/live/drive/uploads', JSON.stringify(request), 'application/json').then(function (answer) {
      if (answer.status !== 409 || !answer.data || !answer.data.current || answer.data.current.kind !== 'File') {
        return answer.ok ? answer.data : fail(answer);
      }
      // the name is taken by a file: a new version of it, or both
      var current = answer.data.current;
      return window.MatUi.confirm('„' + current.name + '“ gibt es hier schon. Als neue Version dieser Datei speichern? „Beide behalten“ legt eine zweite Datei an.',
        { title: 'Datei vorhanden', ok: 'Neue Version', cancel: 'Beide behalten' }).then(function (asVersion) {
        var next = asVersion
          ? { size: file.size, itemId: current.id, ifMatch: current.eTag }
          : { size: file.size, parentId: request.parentId, name: file.name, onConflict: 'Rename' };
        return api('POST', '/live/drive/uploads', JSON.stringify(next), 'application/json').then(function (again) { return again.ok ? again.data : fail(again); });
      });
    });
  }

  function send(file, upload, view) {
    var offset = upload.received || 0;
    function next() {
      if (offset >= file.size) {
        return api('POST', '/live/drive/uploads/' + upload.id + '/finish').then(function (answer) { return answer.ok ? answer.data : fail(answer); });
      }
      var piece = file.slice(offset, Math.min(file.size, offset + CHUNK));
      return api('PUT', '/live/drive/uploads/' + upload.id + '/chunk?offset=' + offset, piece, 'application/octet-stream').then(function (answer) {
        if (!answer.ok) { return fail(answer); }
        offset = answer.data.received;
        view.progress(offset);
        return next();
      });
    }
    return next();
  }

  function uploadAll(files) {
    if (!dialog || !list || files.length === 0) { return; }
    if (!dialog.open && window.MatUi) { window.MatUi.dialog.open(dialog); }
    Array.prototype.forEach.call(files, function (file) {
      var view = row(file);
      running++;
      // one after the other: a question about a name is asked for one file at a time
      queue = queue.then(function () {
        return start(file).then(function (upload) { view.progress(0); return send(file, upload, view); })
          .then(function (item) { view.done(item && item.version > 1 ? 'Version ' + item.version : 'Fertig'); })
          .catch(function (err) { failed++; view.error(err.message); })
          .then(function () { running--; finishedOne(); });
      });
    });
  }

  var queue = Promise.resolve();

  function finishedOne() {
    if (running > 0) { return; }
    if (failed === 0) {
      window.MatUi.toast('Hochgeladen.', { tone: 'success' });
      // the list again, without "upload=1" (that opens the dialog)
      var url = new URL(location.href);
      url.searchParams.delete('upload');
      setTimeout(function () { location.replace(url.toString()); }, 600);
    }
    failed = 0;
  }

  if (dialog) {
    // "Hochladen" of the menu and of the other pages lead here with upload=1: the dialog opens (the file picker needs the person's click)
    if (new URLSearchParams(location.search).get('upload') === '1' && window.MatUi) { window.MatUi.dialog.open(dialog); }
    var input = dialog.querySelector('[data-upload-input]');
    input.addEventListener('change', function () { uploadAll(input.files); input.value = ''; });
    var target = document.querySelector('[data-drop-target]');
    if (target) {
      ['dragenter', 'dragover'].forEach(function (name) {
        target.addEventListener(name, function (e) {
          if (e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], 'Files') >= 0) { e.preventDefault(); target.classList.add('is-drag'); }
        });
      });
      ['dragleave', 'drop'].forEach(function (name) {
        target.addEventListener(name, function () { target.classList.remove('is-drag'); });
      });
      target.addEventListener('drop', function (e) {
        if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) { e.preventDefault(); uploadAll(e.dataTransfer.files); }
      });
    }
  }

  // ---- 3. live ----

  var root = document.querySelector('[data-live]');
  if (!root || !window.fetch) { return; }
  var revision = Number(root.getAttribute('data-live-revision') || '0');
  var failures = 0;
  var stopped = false;
  var looping = false;

  function sleep(ms) { return new Promise(function (resolve) { setTimeout(resolve, ms); }); }

  function busy() { return running > 0 || selected().length > 0 || !!document.querySelector('dialog[open], .mat-menu:not([hidden])'); }

  function refresh() {
    return fetch(location.href, { credentials: 'same-origin', headers: { 'Accept': 'text/html' }, cache: 'no-store' }).then(function (r) {
      if (r.status === 401 || r.redirected && /\/Account\/Login/.test(r.url)) { stopped = true; return; }
      if (!r.ok) { throw new Error('HTTP ' + r.status); }
      return r.text().then(function (html) {
        var fresh = new DOMParser().parseFromString(html, 'text/html').querySelector('[data-live]');
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
      });
    });
  }

  function loop() {
    if (looping) { return; }
    looping = true;
    (function next() {
      if (stopped || navigator.onLine === false) { looping = false; return; }
      fetch('/live/drive/revision?since=' + revision + '&timeout=25', { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, cache: 'no-store' })
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
  loop();
})();
