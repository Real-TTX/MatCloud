/* MatCloud, ThixNote: the editor of a note ([data-md-editor] around a textarea [data-md-input]).
   Markdown (data-mode="markdown"):
   - the toolbar and the keyboard set the marks: Strg+B fett, Strg+I kursiv, Strg+Umschalt+X durchgestrichen, Strg+E Code, Strg+K Link,
     Strg+Umschalt+1 … 3 Überschrift, Strg+Umschalt+8 / 7 / 9 Liste, nummerierte Liste, Aufgabe; a second time takes the mark away again.
   - Enter continues a list, a numbered list, a task list or a quote; Enter on an empty item ends it. Tab / Umschalt+Tab indent list items.
   - a link pasted over selected text becomes [text](link).
   - the preview is drawn by the server (/live/note/preview, Markdig like the page of the note): beside the text on a wide screen ("Geteilt"), or
     instead of it ("Vorschau"); the choice is remembered.
   Every editor (also plain text and a protected note): words and characters, Strg+S saves, a warning before unsaved text is left.
   Every change goes through execCommand('insertText'), so Strg+Z undoes it like typing. */
(function () {
  'use strict';

  var MODE_KEY = 'mc-note-editor-mode';
  var LIST = /^(\s*)([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?/;
  var MARKER = /^([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?/;
  var wide = window.matchMedia('(min-width: 1100px)');
  var token = document.querySelector('[data-page-token] input[name="__RequestVerificationToken"]');

  Array.prototype.forEach.call(document.querySelectorAll('[data-md-editor]'), setUp);

  function setUp(editor) {
    var input = editor.querySelector('[data-md-input]');
    if (!input) { return; }
    var form = input.form;
    var write = editor.querySelector('.mc-editor__write');
    var preview = editor.querySelector('[data-md-preview]');
    var previewBody = editor.querySelector('[data-md-preview-body]');
    var count = editor.querySelector('[data-md-count]');
    var state = editor.querySelector('[data-md-state]');
    var modeButtons = editor.querySelectorAll('[data-md-mode]');
    var title = form ? form.querySelector('input[name="Title"]') : null;
    var initial = input.value;
    var initialTitle = title ? title.value : '';
    // a page the server sent back with an error holds text that is not saved yet
    var unsaved = !!document.querySelector('[data-validation-summary]');
    var submitting = false;
    var mode = 'write';

    function markdown() { return editor.getAttribute('data-mode') === 'markdown'; }

    // ---- changing the text (undoable) ----

    function replace(start, end, text, selStart, selEnd) {
      var value = input.value;
      var expected = value.slice(0, start) + text + value.slice(end);
      input.focus();
      input.setSelectionRange(start, end);
      var done = false;
      try { done = document.execCommand('insertText', false, text); } catch (e) { done = false; }
      if (!done || input.value !== expected) {
        input.value = expected;
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
      input.setSelectionRange(selStart, selEnd === undefined ? selStart : selEnd);
    }

    /** The whole lines the selection touches. */
    function lines() {
      var v = input.value, s = input.selectionStart, e = input.selectionEnd;
      var start = v.lastIndexOf('\n', s - 1) + 1;
      if (e > s && v.charAt(e - 1) === '\n') { e--; }
      var end = v.indexOf('\n', e);
      if (end < 0) { end = v.length; }
      return { start: start, end: end, list: v.slice(start, end).split('\n'), fromEnd: end - input.selectionEnd };
    }

    function putLines(r, out) {
      var text = out.join('\n');
      if (r.list.length > 1) { replace(r.start, r.end, text, r.start, r.start + text.length); }
      else { replace(r.start, r.end, text, Math.max(r.start, r.start + text.length - Math.max(0, r.fromEnd))); }
    }

    /** Marks around the selection (or around a placeholder); marks that are there already are taken away. */
    function wrap(mark, placeholder) {
      var s = input.selectionStart, e = input.selectionEnd, v = input.value;
      var sel = v.slice(s, e);
      var n = mark.length;
      if (v.slice(s - n, s) === mark && v.slice(e, e + n) === mark) {
        replace(s - n, e + n, sel, s - n, e - n);
        return;
      }

      if (sel.length >= 2 * n && sel.slice(0, n) === mark && sel.slice(-n) === mark) {
        var inner = sel.slice(n, sel.length - n);
        replace(s, e, inner, s, s + inner.length);
        return;
      }

      // the spaces at the ends of a selection stay outside the marks (**word** , not ** word **)
      var lead = sel.match(/^\s*/)[0];
      var trail = sel.slice(lead.length).match(/\s*$/)[0];
      var core = sel.slice(lead.length, sel.length - trail.length) || placeholder;
      var from = s + lead.length + n;
      replace(s, e, lead + mark + core + mark + trail, from, from + core.length);
    }

    /** A list, numbered list, task list or quote for every line of the selection; when all lines are one already, it is taken away. */
    function prefix(kind) {
      var r = lines();
      var tests = { ul: /^\s*[-*+]\s+(?!\[[ xX]\]\s)/, ol: /^\s*\d+[.)]\s+/, task: /^\s*[-*+]\s+\[[ xX]\]\s+/, quote: /^\s*>\s?/ };
      var filled = r.list.filter(function (l) { return l.trim(); });
      var all = filled.length > 0 && filled.every(function (l) { return tests[kind].test(l); });
      var number = 0;
      var out = r.list.map(function (line) {
        if (!line.trim() && r.list.length > 1) { return line; }
        if (all) { return kind === 'quote' ? line.replace(/^(\s*)>\s?/, '$1') : line.replace(LIST, '$1'); }
        var indent = line.match(/^\s*/)[0];
        var body = line.slice(indent.length);
        if (kind !== 'quote') { body = body.replace(MARKER, ''); }
        number++;
        var mark = kind === 'ul' ? '- ' : kind === 'ol' ? number + '. ' : kind === 'task' ? '- [ ] ' : '> ';
        return indent + mark + body;
      });
      putLines(r, out);
    }

    /** Heading 1 to 3 on the line of the cursor; the same level again (or a fourth press of the button) makes it text again. */
    function heading(level) {
      var r = lines();
      var line = r.list[0];
      var m = line.match(/^(#{1,6})\s+/);
      var current = m ? m[1].length : 0;
      var next = level ? (current === level ? 0 : level) : (current >= 3 ? 0 : current + 1);
      var text = (next ? new Array(next + 1).join('#') + ' ' : '') + (m ? line.slice(m[0].length) : line);
      replace(r.start, r.start + line.length, text, r.start + text.length);
    }

    function link() {
      var s = input.selectionStart, e = input.selectionEnd, sel = input.value.slice(s, e);
      if (/^(https?:\/\/|mailto:)\S+$/i.test(sel)) {
        replace(s, e, '[Text](' + sel + ')', s + 1, s + 5);
        return;
      }

      var label = sel || 'Text';
      var from = s + label.length + 3;
      replace(s, e, '[' + label + '](https://)', sel ? from : s + 1, sel ? from + 8 : s + 1 + label.length);
    }

    /** A block on lines of its own, with an empty line before it. */
    function block(text, selFrom, selLength) {
      var v = input.value, s = input.selectionStart, e = input.selectionEnd;
      var before = v.slice(0, s);
      var lead = before.length === 0 || /\n\n$/.test(before) ? '' : /\n$/.test(before) ? '\n' : '\n\n';
      var tail = v.slice(e).charAt(0) === '\n' || e === v.length ? '' : '\n';
      var at = s + lead.length + selFrom;
      replace(s, e, lead + text + tail, at, at + selLength);
    }

    function codeBlock() {
      var s = input.selectionStart, e = input.selectionEnd;
      if (s === e) { block('```\n\n```\n', 4, 0); return; }
      var r = lines();
      var body = input.value.slice(r.start, r.end);
      replace(r.start, r.end, '```\n' + body + '\n```', r.start + 4, r.start + 4 + body.length);
    }

    var actions = {
      heading: function () { heading(0); },
      bold: function () { wrap('**', 'fett'); },
      italic: function () { wrap('_', 'kursiv'); },
      strike: function () { wrap('~~', 'durchgestrichen'); },
      code: function () { wrap('`', 'Code'); },
      link: link,
      ul: function () { prefix('ul'); },
      ol: function () { prefix('ol'); },
      task: function () { prefix('task'); },
      quote: function () { prefix('quote'); },
      codeblock: codeBlock,
      table: function () { block('| Spalte 1 | Spalte 2 |\n| --- | --- |\n|  |  |\n', 2, 8); },
      rule: function () { block('---\n', 4, 0); }
    };

    Array.prototype.forEach.call(editor.querySelectorAll('[data-md]'), function (button) {
      // the selection of the text stays while a button is pressed
      button.addEventListener('mousedown', function (e) { e.preventDefault(); });
      button.addEventListener('click', function () {
        var act = actions[button.getAttribute('data-md')];
        if (act && markdown() && mode !== 'preview') { act(); }
      });
    });

    // ---- the keyboard ----

    input.addEventListener('keydown', function (e) {
      if (e.isComposing || (e.getModifierState && e.getModifierState('AltGraph'))) { return; }
      var mod = e.ctrlKey || e.metaKey;
      if (mod && !e.altKey && e.key && e.key.toLowerCase() === 's') { e.preventDefault(); save(); return; }
      if (!markdown()) { return; }

      if (mod && !e.altKey) {
        var key = e.key.toLowerCase();
        var act = null;
        if (!e.shiftKey && key === 'b') { act = actions.bold; }
        else if (!e.shiftKey && key === 'i') { act = actions.italic; }
        else if (!e.shiftKey && key === 'k') { act = actions.link; }
        else if (!e.shiftKey && key === 'e') { act = actions.code; }
        else if (e.shiftKey && key === 'x') { act = actions.strike; }
        else if (e.shiftKey && e.code === 'Digit8') { act = actions.ul; }
        else if (e.shiftKey && e.code === 'Digit7') { act = actions.ol; }
        else if (e.shiftKey && e.code === 'Digit9') { act = actions.task; }
        else if (e.shiftKey && /^Digit[123]$/.test(e.code)) { var level = Number(e.code.slice(5)); act = function () { heading(level); }; }
        if (act) { e.preventDefault(); act(); }
        return;
      }

      if (e.key === 'Enter' && !mod && !e.shiftKey && !e.altKey) { continueBlock(e); return; }
      if (e.key === 'Tab' && !mod && !e.altKey) { indent(e); }
    });

    function continueBlock(e) {
      var s = input.selectionStart, v = input.value;
      if (s !== input.selectionEnd) { return; }
      var start = v.lastIndexOf('\n', s - 1) + 1;
      var end = v.indexOf('\n', s);
      if (end < 0) { end = v.length; }
      var line = v.slice(start, end);
      var m = line.match(/^(\s*)([-*+]|(\d+)([.)]))\s+(\[[ xX]\]\s+)?/);
      var q = m ? null : line.match(/^(\s*>\s?)/);
      if (!m && !q) { return; }
      var marker = m ? m[0] : q[0];
      if (s < start + marker.length) { return; } // the cursor in front of the mark: a plain new line
      e.preventDefault();
      if (!line.slice(marker.length).trim()) {
        // Enter on an empty item ends the list
        replace(start, end, '', start);
        return;
      }

      var next = m ? m[1] + (m[3] ? (Number(m[3]) + 1) + m[4] : m[2]) + ' ' + (m[5] ? '[ ] ' : '') : q[1].replace(/\s*$/, ' ');
      replace(s, s, '\n' + next, s + 1 + next.length);
    }

    function indent(e) {
      var r = lines();
      if (!r.list.every(function (l) { return !l.trim() || LIST.test(l); })) { return; } // Tab leaves the field as usual
      e.preventDefault();
      var out = r.list.map(function (l) {
        if (!l.trim()) { return l; }
        return e.shiftKey ? l.replace(/^( {1,2}|\t)/, '') : '  ' + l;
      });
      putLines(r, out);
    }

    // a link pasted over selected text becomes a link of that text
    input.addEventListener('paste', function (e) {
      if (!markdown() || !e.clipboardData) { return; }
      var url = (e.clipboardData.getData('text/plain') || '').trim();
      var s = input.selectionStart, end = input.selectionEnd;
      if (s === end || !/^(https?:\/\/|mailto:)\S+$/i.test(url)) { return; }
      var sel = input.value.slice(s, end);
      if (/\n/.test(sel) || /^(https?:\/\/|mailto:)/i.test(sel)) { return; }
      e.preventDefault();
      var text = '[' + sel + '](' + url + ')';
      replace(s, end, text, s + text.length);
    });

    // ---- the preview ----

    var timer = 0, sent = null, asked = 0;

    function render() {
      if (!preview || preview.hidden) { return; }
      var text = input.value;
      var heading = title ? title.value : '';
      if (sent === text + '\u0000' + heading) { return; }
      sent = text + '\u0000' + heading;
      var id = ++asked;
      var body = new FormData();
      body.append('text', text);
      body.append('title', heading);
      editor.classList.add('is-rendering');
      fetch('/live/note/preview', { method: 'POST', body: body, credentials: 'same-origin', headers: { 'RequestVerificationToken': token ? token.value : '' } })
        .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.text(); })
        .then(function (html) {
          if (id !== asked) { return; }
          // HTML of Markdig with HTML of the note's own switched off (the server never passes a tag of the text through)
          previewBody.innerHTML = html.trim() ? html : '<p class="mc-editor__empty">Hier erscheint die Notiz, wie sie aussehen wird.</p>';
          editor.classList.remove('is-stale');
          follow();
        })
        .catch(function () { if (id === asked) { sent = null; editor.classList.add('is-stale'); } })
        .then(function () { if (id === asked) { editor.classList.remove('is-rendering'); } });
    }

    function later() { clearTimeout(timer); timer = setTimeout(render, 250); }

    /** In the split view the preview follows the text: the same share scrolled. */
    function follow() {
      if (mode !== 'split') { return; }
      var max = input.scrollHeight - input.clientHeight;
      var share = max > 0 ? input.scrollTop / max : 0;
      preview.scrollTop = share * (preview.scrollHeight - preview.clientHeight);
    }

    function setMode(next, remember) {
      if (!markdown() || !preview) { next = 'write'; }
      if (next === 'split' && !wide.matches) { next = 'write'; }
      mode = next;
      editor.setAttribute('data-view', mode);
      if (preview) { preview.hidden = mode === 'write'; }
      write.hidden = mode === 'preview';
      Array.prototype.forEach.call(modeButtons, function (b) { b.setAttribute('aria-pressed', String(b.getAttribute('data-md-mode') === mode)); });
      Array.prototype.forEach.call(editor.querySelectorAll('[data-md]'), function (b) { b.disabled = mode === 'preview'; });
      if (remember) { try { localStorage.setItem(MODE_KEY, mode); } catch (e) { /* private mode */ } }
      if (mode !== 'write') { sent = null; render(); }
    }

    Array.prototype.forEach.call(modeButtons, function (b) {
      b.addEventListener('click', function () {
        setMode(b.getAttribute('data-md-mode'), true);
        if (mode !== 'preview') { input.focus(); }
      });
    });

    function chosenMode() {
      var stored = null;
      try { stored = localStorage.getItem(MODE_KEY); } catch (e) { stored = null; }
      return stored || (wide.matches ? 'split' : 'write');
    }

    if (wide.addEventListener) { wide.addEventListener('change', function () { setMode(chosenMode(), false); }); }
    input.addEventListener('scroll', follow, { passive: true });

    // a new note: Markdown or text
    Array.prototype.forEach.call(document.querySelectorAll('[data-note-kind]'), function (radio) {
      radio.addEventListener('change', function () {
        if (!radio.checked) { return; }
        var md = radio.value === 'Markdown';
        editor.setAttribute('data-mode', md ? 'markdown' : 'text');
        editor.classList.toggle('is-plain', !md);
        setMode(md ? chosenMode() : 'write', false);
      });
    });

    // ---- words, characters, unsaved ----

    function changed() { return unsaved || input.value !== initial || (title && title.value !== initialTitle); }

    function update() {
      var text = input.value;
      var words = (text.match(/[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*/gu) || []).length;
      if (count) {
        count.textContent = words.toLocaleString('de-DE') + (words === 1 ? ' Wort' : ' Wörter') + ' · ' + text.length.toLocaleString('de-DE') + ' Zeichen';
      }
      if (state) {
        var dirty = changed();
        state.textContent = dirty ? 'Nicht gespeichert' : '';
        editor.classList.toggle('is-dirty', !!dirty);
      }
    }

    input.addEventListener('input', function () { update(); later(); });
    if (title) { title.addEventListener('input', function () { update(); later(); }); }

    // a protected note: its text arrives after it was decrypted (note-crypt.js)
    input.addEventListener('note:loaded', function () { initial = input.value; unsaved = false; update(); });

    function save() {
      if (!form) { return; }
      if (form.requestSubmit) { form.requestSubmit(); } else { form.submit(); }
    }

    if (form) {
      form.addEventListener('submit', function (e) {
        submitting = true;
        // the check of the fields (or the encryption) may stop it: then leaving warns again
        setTimeout(function () { if (e.defaultPrevented && !form.querySelector('[data-note-bytes]')) { submitting = false; } }, 0);
      });
    }

    window.addEventListener('beforeunload', function (e) {
      if (submitting || !changed()) { return; }
      e.preventDefault();
      e.returnValue = '';
    });

    setMode(chosenMode(), false);
    update();
  }
})();
