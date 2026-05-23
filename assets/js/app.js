(function () {
  const allowedThemes = new Set(['light', 'dark', 'night']);
  const savedTheme = allowedThemes.has(localStorage.getItem('pharma40-theme'))
    ? localStorage.getItem('pharma40-theme')
    : 'light';
  document.documentElement.dataset.theme = savedTheme;
  document.body.dataset.theme = savedTheme;

  const trigger = document.querySelector('[data-profile-trigger]');
  const dropdown = document.querySelector('.profile-dropdown');
  const themeSelect = document.getElementById('themeSelect');

  if (themeSelect) {
    themeSelect.value = savedTheme;
    themeSelect.addEventListener('change', () => {
      const nextTheme = allowedThemes.has(themeSelect.value) ? themeSelect.value : 'light';
      document.documentElement.dataset.theme = nextTheme;
      document.body.dataset.theme = nextTheme;
      localStorage.setItem('pharma40-theme', nextTheme);
    });
  }

  if (trigger && dropdown) {
    trigger.addEventListener('click', () => {
      dropdown.hidden = !dropdown.hidden;
    });
    document.addEventListener('click', (event) => {
      if (!trigger.contains(event.target) && !dropdown.contains(event.target)) {
        dropdown.hidden = true;
      }
    });
  }
})();
