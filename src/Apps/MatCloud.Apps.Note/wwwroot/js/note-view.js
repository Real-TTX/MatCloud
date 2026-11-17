/* MatCloud, ThixNote: the page of a note. "E" opens the editor (not while a field or a dialog has the keys); links to other sites open in a new tab,
   so the note stays where it is. */
(function () {
  'use strict';

  var edit = document.querySelector('[data-note-edit]');
  document.addEventListener('keydown', function (e) {
    if (!edit || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey || e.key.toLowerCase() !== 'e') { return; }
    var t = e.target;
    if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) { return; }
    if (document.querySelector('dialog[open], .mat-menu:not([hidden])')) { return; }
    e.preventDefault();
    location.href = edit.getAttribute('href');
  });

  Array.prototype.forEach.call(document.querySelectorAll('.mc-paper a[href]'), function (a) {
    if (a.host && a.host !== location.host) { a.target = '_blank'; }
  });
})();
