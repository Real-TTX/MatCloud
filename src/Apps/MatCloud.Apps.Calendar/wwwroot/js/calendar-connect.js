/* MatCloud, ThixCalendar: the sign-in at Microsoft with a code. The card ([data-ms-code] = the id of the sign-in, [data-ms-interval] = seconds) asks
   /live/calendar/microsoft/{id} until Microsoft answers: "done" with the account goes to "Konten", a message is shown in the card. The code can be copied. */
(function () {
  'use strict';

  var card = document.querySelector('[data-ms-code]');
  document.addEventListener('click', function (event) {
    var button = event.target.closest && event.target.closest('[data-copy]');
    if (!button || !navigator.clipboard) { return; }
    navigator.clipboard.writeText(button.getAttribute('data-copy')).then(function () {
      button.setAttribute('data-tip', 'Kopiert');
    });
  });
  if (!card || !window.fetch) { return; }

  var id = card.getAttribute('data-ms-code');
  var interval = Math.max(3, Number(card.getAttribute('data-ms-interval') || '5')) * 1000;
  var state = card.querySelector('[data-ms-state]');
  var error = card.querySelector('[data-ms-error]');
  var errorText = card.querySelector('[data-ms-error-text]');

  function fail(message) {
    if (state) { state.hidden = true; }
    if (errorText) { errorText.textContent = message || 'Die Anmeldung hat nicht geklappt.'; }
    if (error) { error.hidden = false; }
  }

  function ask() {
    fetch('/live/calendar/microsoft/' + encodeURIComponent(id), { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, cache: 'no-store' })
      .then(function (r) { return r.json().then(function (body) { return { ok: r.ok, body: body }; }); })
      .then(function (answer) {
        if (!answer.ok) { fail(answer.body && answer.body.message); return; }
        if (answer.body.done) { location.href = '/Calendar/Accounts'; return; }
        setTimeout(ask, interval);
      })
      .catch(function () { setTimeout(ask, interval * 2); });
  }

  setTimeout(ask, interval);
})();
