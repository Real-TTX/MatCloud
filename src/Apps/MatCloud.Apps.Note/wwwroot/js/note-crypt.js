/* MatCloud, ThixNote: a protected note (.txts) is opened and saved ONLY in the browser; the password and the text never go to the server.
   The format is ThixNote's (NoteEncryption): "QNSEC1" | iterations (int32, little endian) | salt (16) | nonce (12) | tag (16) | ciphertext.
   Key: PBKDF2-SHA256 of the password (UTF-8) with the salt and the iterations, 32 bytes. Cipher: AES-256-GCM with the nonce, the magic as additional data.
   Saving keeps the salt and the iterations (the same password), takes a fresh nonce, and decrypts the result once more before it is sent. */
(function () {
  'use strict';

  var root = document.querySelector('[data-note-crypt]');
  if (!root) { return; }

  var MAGIC = new TextEncoder().encode('QNSEC1');
  var HEADER = MAGIC.length + 4 + 16 + 12 + 16;
  var unlockForm = root.querySelector('[data-note-unlock]');
  var editor = root.querySelector('[data-note-editor]');
  var password = root.querySelector('[data-note-password]');
  var error = root.querySelector('[data-note-error]');
  var plain = root.querySelector('[data-note-plain]');
  var bytesField = root.querySelector('[data-note-bytes]');
  var state = null; // { key, iterations, salt } after the unlock

  function fromBase64(text) {
    var raw = atob(text);
    var bytes = new Uint8Array(raw.length);
    for (var i = 0; i < raw.length; i++) { bytes[i] = raw.charCodeAt(i); }
    return bytes;
  }

  function toBase64(bytes) {
    var raw = '';
    for (var i = 0; i < bytes.length; i += 0x8000) { raw += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); }
    return btoa(raw);
  }

  function parse(bytes) {
    if (bytes.length < HEADER) { throw new Error('format'); }
    for (var i = 0; i < MAGIC.length; i++) { if (bytes[i] !== MAGIC[i]) { throw new Error('format'); } }
    var view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    var offset = MAGIC.length;
    var iterations = view.getInt32(offset, true); offset += 4;
    var salt = bytes.slice(offset, offset + 16); offset += 16;
    var nonce = bytes.slice(offset, offset + 12); offset += 12;
    var tag = bytes.slice(offset, offset + 16); offset += 16;
    var cipher = bytes.slice(offset);
    if (iterations <= 0) { throw new Error('format'); }
    return { iterations: iterations, salt: salt, nonce: nonce, tag: tag, cipher: cipher };
  }

  function deriveKey(secret, salt, iterations) {
    return crypto.subtle.importKey('raw', new TextEncoder().encode(secret), 'PBKDF2', false, ['deriveKey']).then(function (base) {
      return crypto.subtle.deriveKey({ name: 'PBKDF2', salt: salt, iterations: iterations, hash: 'SHA-256' }, base,
        { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    });
  }

  function decrypt(key, file) {
    // WebCrypto wants the tag after the ciphertext
    var data = new Uint8Array(file.cipher.length + file.tag.length);
    data.set(file.cipher, 0);
    data.set(file.tag, file.cipher.length);
    return crypto.subtle.decrypt({ name: 'AES-GCM', iv: file.nonce, additionalData: MAGIC, tagLength: 128 }, key, data)
      .then(function (result) { return new TextDecoder('utf-8', { fatal: true }).decode(result); });
  }

  function encrypt(key, iterations, salt, text) {
    var nonce = crypto.getRandomValues(new Uint8Array(12));
    return crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: MAGIC, tagLength: 128 }, key, new TextEncoder().encode(text))
      .then(function (result) {
        var sealed = new Uint8Array(result);
        var cipher = sealed.subarray(0, sealed.length - 16);
        var tag = sealed.subarray(sealed.length - 16);
        var file = new Uint8Array(HEADER + cipher.length);
        var view = new DataView(file.buffer);
        var offset = 0;
        file.set(MAGIC, offset); offset += MAGIC.length;
        view.setInt32(offset, iterations, true); offset += 4;
        file.set(salt, offset); offset += 16;
        file.set(nonce, offset); offset += 12;
        file.set(tag, offset); offset += 16;
        file.set(cipher, offset);
        return file;
      });
  }

  function showError(text) {
    error.textContent = text;
    error.hidden = !text;
  }

  function busy(form, on) {
    var button = form.querySelector('button[type=submit]');
    if (button) { button.disabled = on; button.classList.toggle('is-loading', on); }
  }

  if (!window.crypto || !crypto.subtle) {
    showError('Dieser Browser kann die Notiz nicht entschlüsseln (die Seite braucht HTTPS).');
    return;
  }

  unlockForm.addEventListener('submit', function (e) {
    e.preventDefault();
    showError('');
    if (!password.value) { showError('Bitte das Kennwort eingeben.'); password.focus(); return; }
    var file;
    try { file = parse(fromBase64(root.getAttribute('data-bytes') || '')); } catch (x) { showError('Diese Notiz ist keine gültige verschlüsselte ThixNote-Datei.'); return; }
    busy(unlockForm, true);
    deriveKey(password.value, file.salt, file.iterations)
      .then(function (key) {
        return decrypt(key, file).then(function (text) {
          state = { key: key, iterations: file.iterations, salt: file.salt };
          password.value = '';
          plain.value = text;
          unlockForm.hidden = true;
          editor.hidden = false;
          // while the note is open, its own buttons (save, lock) are the ones that count
          var actions = document.querySelector('[data-note-actions]');
          if (actions) { actions.hidden = true; }
          plain.focus();
        });
      })
      .catch(function () { showError('Das Kennwort stimmt nicht.'); password.select(); })
      .then(function () { busy(unlockForm, false); });
  });

  editor.addEventListener('submit', function (e) {
    if (bytesField.value) { return; } // encrypted already: let it go
    e.preventDefault();
    if (!state) { return; }
    busy(editor, true);
    var text = plain.value;
    encrypt(state.key, state.iterations, state.salt, text)
      .then(function (file) {
        // decrypt once more before anything leaves the page: a wrong file would reach every device
        return decrypt(state.key, parse(file)).then(function (check) {
          if (check !== text) { throw new Error('check'); }
          bytesField.value = toBase64(file);
          editor.submit();
        });
      })
      .catch(function () { busy(editor, false); notice('Die Notiz konnte nicht verschlüsselt werden; nichts wurde gespeichert.'); });
  });

  function notice(text) {
    if (window.MatUi && MatUi.toast) { MatUi.toast(text, { tone: 'danger' }); } else { plain.setAttribute('aria-invalid', 'true'); plain.title = text; }
  }
})();
