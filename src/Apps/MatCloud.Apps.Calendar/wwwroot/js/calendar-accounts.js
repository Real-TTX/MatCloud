/* MatCloud, ThixCalendar: "Konten". The tile "ICS-Abo" opens the form of the address below it (a <details>) and puts the cursor into it. */
(function () {
  'use strict';

  document.addEventListener('click', function (e) {
    var tile = e.target.closest && e.target.closest('[data-ics-open]');
    var box = document.getElementById('ics');
    if (!tile || !box) { return; }
    e.preventDefault();
    box.open = true;
    box.scrollIntoView({ behavior: 'smooth', block: 'center' });
    var input = box.querySelector('input[type=url], input[name=IcsUrl]');
    if (input) { setTimeout(function () { input.focus({ preventScroll: true }); }, 250); }
  });
})();
