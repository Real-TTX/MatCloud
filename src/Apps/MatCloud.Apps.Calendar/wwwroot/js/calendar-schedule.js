/* MatCloud, ThixCalendar: the calendar page. MatUI's schedule ([data-schedule], LIST-14) draws the views; this script
   - loads the events of what is shown (mat:schedule-range -> data-events-url&from=…&to=…) and hands them over (MatUi.schedule.setEvents),
   - gives every event the colour of its calendar (--mc-event; the content security policy forbids style attributes, CSSOM is allowed),
   - keeps the address (?view=…&date=…), the return addresses of the forms and "Neuer Termin" (the shown day) in step,
   - loads again when something changed (calendar-live.js sends mc:calendar-changed) and
   - switches a calendar on or off in the background (the switches of "Meine Kalender"). */
(function () {
  'use strict';

  var el = document.querySelector('[data-schedule]');
  if (!el || !window.fetch) { return; }

  var source = el.getAttribute('data-events-url');
  var looks = {};          // the address of an event -> { colour, off }
  var range = null;        // what is shown: { from, to, view, date }
  var asked = 0;           // the number of the newest request: an older answer is dropped
  var valid = /^#[0-9a-fA-F]{3,8}$/;

  function api() { return window.MatUi && window.MatUi.schedule; }

  function paint() {
    var nodes = el.querySelectorAll('.mat-schedule__event');
    for (var i = 0; i < nodes.length; i++) {
      var look = looks[nodes[i].getAttribute('href')];
      if (!look) { continue; }
      if (valid.test(look.colour)) { nodes[i].style.setProperty('--mc-event', look.colour); }
      nodes[i].classList.add('mc-ev');
      nodes[i].classList.toggle('is-off', !!look.off);
    }
  }

  function load(quiet) {
    if (!range || !api()) { return; }
    var mine = ++asked;
    if (!quiet) { api().setLoading(el, true); }
    var url = source + '&from=' + encodeURIComponent(range.from) + '&to=' + encodeURIComponent(range.to);
    fetch(url, { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, cache: 'no-store' })
      .then(function (r) {
        if (r.status === 401 || (r.redirected && /\/Account\/Login/.test(r.url))) { location.reload(); return null; }
        if (!r.ok) { throw new Error('HTTP ' + r.status); }
        return r.json();
      })
      .then(function (answer) {
        if (!answer || mine !== asked) { return; }
        looks = {};
        answer.events.forEach(function (e) { looks[e.url] = { colour: e.colour, off: e.off }; });
        api().setEvents(el, answer.events);
      })
      .catch(function () {
        if (mine !== asked) { return; }
        api().setLoading(el, false);
        setTimeout(function () { if (mine === asked) { load(true); } }, 5000);
      });
  }

  function remember() {
    var address = '/Calendar?view=' + range.view + '&date=' + range.date;
    if (location.pathname + location.search !== address && window.history && history.replaceState) { history.replaceState(null, '', address); }
    document.querySelectorAll('[data-return-url]').forEach(function (input) { input.value = address; });
    // "Neuer Termin" starts on the day that is shown (in the day view), otherwise on today
    document.querySelectorAll('a[href^="/Calendar/Event"]').forEach(function (a) {
      if (/[?&]key=/.test(a.getAttribute('href'))) { return; }
      a.setAttribute('href', range.view === 'day' ? '/Calendar/Event?date=' + range.date : '/Calendar/Event');
    });
  }

  el.addEventListener('mat:schedule-range', function (e) {
    range = { from: e.detail.from, to: e.detail.to, view: e.detail.view, date: e.detail.date };
    remember();
    load(false);
  });

  // the schedule draws its body anew on every page and view: paint what it drew
  var body = null;
  var observer = new MutationObserver(paint);
  function watch() {
    var b = el.querySelector('.mat-schedule__body');
    if (b && b !== body) { body = b; observer.observe(b, { childList: true, subtree: true }); paint(); }
  }
  new MutationObserver(watch).observe(el, { childList: true });
  watch();

  // a device or MatCloud's sync changed something: load what is shown again, without the loading state
  document.addEventListener('mc:calendar-changed', function () { load(true); });

  // the first range is sent by the schedule when the page is ready; if the schedule was ready before this script listened, ask it
  setTimeout(function () {
    if (range || !api()) { return; }
    var now = api().get(el);
    if (now) { range = { from: now.from, to: now.to, view: now.view, date: now.date }; remember(); load(false); }
  }, 300);

  // a click into a free place of the time grid (day, week) starts a new event on that day at that half hour
  function iso(d) { return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0'); }
  el.addEventListener('click', function (e) {
    var col = e.target.closest && e.target.closest('.mat-schedule__col');
    if (!col || !range || e.target.closest('.mat-schedule__event')) { return; }
    var cols = Array.prototype.slice.call(col.parentNode.querySelectorAll('.mat-schedule__col'));
    var hours = parseFloat(col.parentNode.getAttribute('data-sch-hours')) || 24;
    var label = el.querySelector('.mat-schedule__hour');
    var h0 = label ? parseInt(label.textContent, 10) || 0 : 0;
    var box = col.getBoundingClientRect();
    var half = Math.floor(((e.clientY - box.top) / box.height) * hours * 2);
    var minutes = Math.max(0, Math.min(47, h0 * 2 + half)) * 30;
    var parts = range.from.split('-');
    var day = new Date(+parts[0], +parts[1] - 1, +parts[2] + cols.indexOf(col));
    location.href = '/Calendar/Event?date=' + iso(day) + '&time=' + String(Math.floor(minutes / 60)).padStart(2, '0') + ':' + String(minutes % 60).padStart(2, '0');
  });

  // "Meine Kalender": a switch posts its form in the background and loads the events again; if it fails, the switch goes back
  document.addEventListener('change', function (e) {
    var input = e.target;
    var form = input && input.closest && input.closest('[data-cal-toggle]');
    if (!form || input.type !== 'checkbox') { return; }
    var data = new FormData(form);
    input.disabled = true;
    fetch(form.getAttribute('action') || location.href, { method: 'POST', body: data, credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
      .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } load(true); })
      .catch(function () { input.checked = !input.checked; })
      .then(function () { input.disabled = false; });
  });
})();
