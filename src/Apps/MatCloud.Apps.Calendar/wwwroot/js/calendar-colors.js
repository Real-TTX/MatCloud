/* MatCloud, ThixCalendar: the colours of the calendars. The pages name the colour of an event or a calendar in data-color ("#2F6FED"); MatCloud's
   content security policy forbids style attributes, so the colour is set here as the custom property --mc-event (CSSOM is allowed) – at the start and for
   every part the live pages put in later. */
(function () {
  'use strict';

  var valid = /^#[0-9a-fA-F]{3,8}$/;

  function paint(root) {
    var nodes = root.querySelectorAll ? root.querySelectorAll('[data-color]') : [];
    for (var i = 0; i < nodes.length; i++) {
      var color = nodes[i].getAttribute('data-color');
      if (valid.test(color)) { nodes[i].style.setProperty('--mc-event', color); }
    }
    if (root.getAttribute && root.hasAttribute('data-color')) {
      var own = root.getAttribute('data-color');
      if (valid.test(own)) { root.style.setProperty('--mc-event', own); }
    }
  }

  paint(document);
  if (window.MutationObserver) {
    new MutationObserver(function (changes) {
      for (var i = 0; i < changes.length; i++) {
        for (var j = 0; j < changes[i].addedNodes.length; j++) {
          if (changes[i].addedNodes[j].nodeType === 1) { paint(changes[i].addedNodes[j]); }
        }
      }
    }).observe(document.body, { childList: true, subtree: true });
  }
})();
