(() => {
  'use strict';
  const toggle = document.getElementById('arch-present');
  if (!toggle) return;
  function setPresentation(enabled) {
    document.body.classList.toggle('arch-presenting', enabled);
    toggle.setAttribute('aria-pressed', String(enabled));
    toggle.textContent = enabled ? 'پایان ارائه' : 'نمای ارائه';
  }
  toggle.hidden = false;
  toggle.addEventListener('click', () => setPresentation(toggle.getAttribute('aria-pressed') !== 'true'));
  document.querySelectorAll('[data-briefing-print]').forEach(button => {
    button.hidden = false;
    button.addEventListener('click', () => window.print());
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && toggle.getAttribute('aria-pressed') === 'true') {
      setPresentation(false);
      toggle.focus();
    }
  });
  document.querySelectorAll('a[href="#architecture-technical"]').forEach(link => {
    link.addEventListener('click', () => setPresentation(false));
  });
})();
