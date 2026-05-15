/*
script.js
- Handles UI behaviors (forms via fetch, profile menu, directory filtering, module launch)
- Uses vanilla JavaScript only
*/

function qs(sel, root = document) {
  return root.querySelector(sel);
}

function qsa(sel, root = document) {
  return Array.from(root.querySelectorAll(sel));
}

function getBackendUrl() {
  return document.body?.dataset?.backend || '/projects/Pharma4.0/backend.php';
}

function appUrl(path) {
  const clean = String(path || '').replace(/^\/+/, '');
  return `/projects/Pharma4.0/${clean}`;
}

function toast(type, title, message) {
  const host = qs('#toast');
  if (!host) return;

  const el = document.createElement('div');
  el.className = `toast__item ${type === 'good' ? 'is-good' : type === 'warn' ? 'is-warn' : 'is-bad'}`;
  el.innerHTML = `<div class="toast__title"></div><div class="toast__msg"></div>`;
  el.querySelector('.toast__title').textContent = title || '';
  el.querySelector('.toast__msg').textContent = message || '';
  host.appendChild(el);

  setTimeout(() => {
    el.style.opacity = '0';
    el.style.transform = 'translateY(4px)';
    el.style.transition = 'opacity .18s ease, transform .18s ease';
    setTimeout(() => el.remove(), 220);
  }, 3400);
}

async function postForm(form) {
  const fd = new FormData(form);
  fd.set('json', '1');
  const res = await fetch(getBackendUrl(), {
    method: 'POST',
    headers: { 'Accept': 'application/json', 'X-Requested-With': 'fetch' },
    body: fd
  });

  const text = await res.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch (_) {
    const compact = text.replace(/\s+/g, ' ').trim();
    json = {
      ok: false,
      message: compact ? `Server returned non-JSON from ${res.url} (${res.status}): ${compact.slice(0, 180)}` : `Server returned an empty response from ${res.url} (${res.status}).`
    };
  }

  if (!res.ok && json && typeof json.ok === 'undefined') {
    json.ok = false;
  }
  return json;
}

function setupFlash() {
  const close = qs('[data-close-flash]');
  if (!close) return;
  close.addEventListener('click', () => {
    const flash = close.closest('.flash');
    if (flash) flash.remove();
  });
}

function setupAuthAjax() {
  qsa('form.js-ajax-form').forEach((form) => {
    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const btn = form.querySelector('button[type="submit"]');
      if (btn) btn.disabled = true;
      const json = await postForm(form);
      if (btn) btn.disabled = false;

      if (json.ok) {
        toast('good', 'Success', json.message || 'Done.');
        if (json.redirect) {
          setTimeout(() => { window.location.href = appUrl(json.redirect); }, 450);
        }
        return;
      }

      toast('bad', 'Error', json.message || 'Something went wrong.');
    });
  });
}

function setupExactAuth() {
  const container = qs('#container');
  const signUpButton = qs('#signUp');
  const signInButton = qs('#signIn');
  const signupForm = qs('#signupForm');
  const loginForm = qs('#loginForm');
  const signupMessage = qs('#signupMessage');
  const loginMessage = qs('#loginMessage');

  if (!container || !signupForm || !loginForm) return;

  const forgotPasswordLink = qs('#forgotPasswordLink');
  const emailRequestModal = qs('#emailRequestModal');
  const otpEntryModal = qs('#otpEntryModal');
  const changePasswordModal = qs('#changePasswordModal');
  const forgotEmailInput = qs('#forgotEmail');
  const sendOtpButton = qs('#sendOtpButton');
  const forgotEmailMessage = qs('#forgotEmailMessage');
  const otpInput = qs('#otpInput');
  const verifyOtpButton = qs('#verifyOtpButton');
  const resendOtpButton = qs('#resendOtpButton');
  const otpTimerDisplay = qs('#otpTimer');
  const otpMessage = qs('#otpMessage');
  const newPasswordInput = qs('#newPassword');
  const confirmNewPasswordInput = qs('#confirmNewPassword');
  const resetPasswordButton = qs('#resetPasswordButton');
  const changePasswordMessage = qs('#changePasswordMessage');
  const closeButtons = qsa('.close-button');

  let forgotPasswordEmail = '';
  let resetCode = '';
  let otpCountdownInterval = null;

  function showMessage(element, type, message) {
    if (!element) return;
    element.textContent = message || '';
    element.className = `message ${type || ''}`;
  }

  function clearMessages() {
    showMessage(signupMessage, '', '');
    showMessage(loginMessage, '', '');
  }

  function clearFormFields() {
    ['#signupName', '#signupEmail', '#signupPassword', '#loginEmail', '#loginPassword'].forEach((sel) => {
      const el = qs(sel);
      if (el) el.value = '';
    });
  }

  function isValidEmail(email) {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(String(email || ''));
  }

  function showAuthModal(modalElement) {
    if (modalElement) modalElement.style.display = 'flex';
  }

  function hideAuthModal(modalElement) {
    if (modalElement) modalElement.style.display = 'none';
  }

  function formatTime(seconds) {
    const min = Math.floor(seconds / 60);
    const sec = seconds % 60;
    return `${min.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
  }

  function startOtpTimer(minutes) {
    clearInterval(otpCountdownInterval);
    let time = minutes * 60;
    if (otpTimerDisplay) otpTimerDisplay.textContent = `OTP expires in ${formatTime(time)}`;
    otpCountdownInterval = setInterval(() => {
      time -= 1;
      if (otpTimerDisplay) otpTimerDisplay.textContent = `OTP expires in ${formatTime(time)}`;
      if (time <= 0) {
        clearInterval(otpCountdownInterval);
        if (otpTimerDisplay) otpTimerDisplay.textContent = 'OTP has expired.';
        showMessage(otpMessage, 'error', 'OTP expired. Please resend.');
        if (otpInput) otpInput.disabled = true;
        if (verifyOtpButton) verifyOtpButton.disabled = true;
      } else {
        if (otpInput) otpInput.disabled = false;
        if (verifyOtpButton) verifyOtpButton.disabled = false;
      }
    }, 1000);
  }

  signUpButton?.addEventListener('click', () => {
    container.classList.add('right-panel-active');
    clearMessages();
    clearFormFields();
  });

  signInButton?.addEventListener('click', () => {
    container.classList.remove('right-panel-active');
    clearMessages();
    clearFormFields();
  });

  signupForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const name = qs('#signupName')?.value.trim();
    const email = qs('#signupEmail')?.value.trim();
    const password = qs('#signupPassword')?.value || '';

    if (!name || !email || !password) {
      showMessage(signupMessage, 'error', 'Please fill all fields.');
      return;
    }
    if (!isValidEmail(email)) {
      showMessage(signupMessage, 'error', 'Please enter a valid email address.');
      return;
    }

    showMessage(signupMessage, 'neutral', 'Registering...');
    const json = await postForm(signupForm);
    if (json.ok) {
      showMessage(signupMessage, 'success', 'Registration successful. Opening your dashboard...');
      setTimeout(() => { window.location.href = appUrl(json.redirect || 'index.php?page=dashboard'); }, 700);
    } else {
      showMessage(signupMessage, 'error', json.message || 'Registration failed.');
    }
  });

  loginForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const email = qs('#loginEmail')?.value.trim();
    const password = qs('#loginPassword')?.value || '';

    if (!email || !password) {
      showMessage(loginMessage, 'error', 'Please enter email and password.');
      return;
    }
    if (!isValidEmail(email)) {
      showMessage(loginMessage, 'error', 'Please enter a valid email address.');
      return;
    }

    showMessage(loginMessage, 'neutral', 'Signing in...');
    const json = await postForm(loginForm);
    if (json.ok) {
      showMessage(loginMessage, 'success', json.message || 'Login successful.');
      setTimeout(() => { window.location.href = appUrl(json.redirect || 'index.php?page=dashboard'); }, 700);
    } else {
      showMessage(loginMessage, 'error', json.message || 'Invalid email or password.');
    }
  });

  forgotPasswordLink?.addEventListener('click', (e) => {
    e.preventDefault();
    forgotPasswordEmail = '';
    resetCode = '';
    if (forgotEmailInput) forgotEmailInput.value = '';
    showMessage(forgotEmailMessage, 'neutral', '');
    showAuthModal(emailRequestModal);
  });

  async function requestOtp() {
    forgotPasswordEmail = String(forgotEmailInput?.value || forgotPasswordEmail || '').trim();
    if (!isValidEmail(forgotPasswordEmail)) {
      showMessage(forgotEmailMessage, 'error', 'Please enter a valid email address.');
      return;
    }

    showMessage(forgotEmailMessage, 'neutral', 'Sending OTP...');
    const fd = new FormData();
    fd.set('action', 'forgot_start');
    fd.set('email', forgotPasswordEmail);

    const res = await fetch(getBackendUrl(), { method: 'POST', headers: { 'Accept': 'application/json' }, body: fd });
    const json = await res.json().catch(() => ({ ok: false, message: 'Unable to send OTP.' }));

    if (json.ok) {
      resetCode = String(json.demo_code || '');
      showMessage(forgotEmailMessage, 'success', `OTP created: ${resetCode}`);
      setTimeout(() => {
        hideAuthModal(emailRequestModal);
        showAuthModal(otpEntryModal);
        if (otpInput) otpInput.value = '';
        showMessage(otpMessage, 'neutral', `For local XAMPP demo, use OTP ${resetCode}.`);
        startOtpTimer(10);
      }, 700);
    } else {
      showMessage(forgotEmailMessage, 'error', json.message || 'Error sending OTP.');
    }
  }

  sendOtpButton?.addEventListener('click', requestOtp);

  verifyOtpButton?.addEventListener('click', () => {
    const otp = String(otpInput?.value || '').trim();
    if (otp.length !== 6 || !/^\d+$/.test(otp)) {
      showMessage(otpMessage, 'error', 'Please enter a valid 6-digit OTP.');
      return;
    }
    if (otp !== resetCode) {
      showMessage(otpMessage, 'error', 'Invalid or expired OTP.');
      return;
    }
    showMessage(otpMessage, 'success', 'OTP verified successfully.');
    clearInterval(otpCountdownInterval);
    setTimeout(() => {
      hideAuthModal(otpEntryModal);
      showAuthModal(changePasswordModal);
      if (newPasswordInput) newPasswordInput.value = '';
      if (confirmNewPasswordInput) confirmNewPasswordInput.value = '';
      showMessage(changePasswordMessage, 'neutral', '');
    }, 700);
  });

  resendOtpButton?.addEventListener('click', () => {
    if (!forgotPasswordEmail) {
      showMessage(otpMessage, 'error', 'No email registered for password reset. Please go back and enter your email.');
      return;
    }
    if (forgotEmailInput) forgotEmailInput.value = forgotPasswordEmail;
    requestOtp();
  });

  resetPasswordButton?.addEventListener('click', async () => {
    const newPassword = newPasswordInput?.value || '';
    const confirmNewPassword = confirmNewPasswordInput?.value || '';

    if (!newPassword || !confirmNewPassword) {
      showMessage(changePasswordMessage, 'error', 'Please enter and confirm your new password.');
      return;
    }
    if (newPassword !== confirmNewPassword) {
      showMessage(changePasswordMessage, 'error', 'Passwords do not match.');
      return;
    }
    if (newPassword.length < 8) {
      showMessage(changePasswordMessage, 'error', 'Password must be at least 8 characters long.');
      return;
    }

    showMessage(changePasswordMessage, 'neutral', 'Resetting password...');
    const fd = new FormData();
    fd.set('action', 'forgot_finish');
    fd.set('email', forgotPasswordEmail);
    fd.set('code', resetCode);
    fd.set('new_password', newPassword);

    const res = await fetch(getBackendUrl(), { method: 'POST', headers: { 'Accept': 'application/json' }, body: fd });
    const json = await res.json().catch(() => ({ ok: false, message: 'Unable to reset password.' }));

    if (json.ok) {
      showMessage(changePasswordMessage, 'success', json.message || 'Password reset successfully.');
      setTimeout(() => {
        hideAuthModal(changePasswordModal);
        clearFormFields();
        container.classList.remove('right-panel-active');
      }, 900);
    } else {
      showMessage(changePasswordMessage, 'error', json.message || 'Error resetting password.');
    }
  });

  closeButtons.forEach((button) => {
    button.addEventListener('click', () => {
      const modalId = button.dataset.modal;
      hideAuthModal(qs(`#${modalId}`));
      if (modalId === 'otpEntryModal') clearInterval(otpCountdownInterval);
    });
  });

  window.addEventListener('click', (event) => {
    [emailRequestModal, otpEntryModal, changePasswordModal].forEach((modal) => {
      if (event.target === modal) {
        hideAuthModal(modal);
        if (modal === otpEntryModal) clearInterval(otpCountdownInterval);
      }
    });
  });
}

function setupForgotFlow() {
  const startForm = qs('form.js-forgot-start');
  const finishForm = qs('form.js-forgot-finish');
  if (!startForm || !finishForm) return;

  const pill = qs('#resetCodePill');

  startForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const json = await postForm(startForm);
    if (!json.ok) {
      toast('bad', 'Reset failed', json.message || 'Unable to generate reset code.');
      return;
    }

    const demoCode = json.demo_code ? String(json.demo_code) : '';
    if (pill) {
      pill.hidden = false;
      pill.textContent = `Reset code: ${demoCode} (valid for 10 minutes)`;
    }

    const email = new FormData(startForm).get('email');
    const emailField = finishForm.querySelector('input[name="email"]');
    if (emailField && email) emailField.value = String(email);

    toast('good', 'Reset code created', json.message || 'Use the code to set a new password.');
  });

  finishForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const json = await postForm(finishForm);
    if (!json.ok) {
      toast('bad', 'Reset failed', json.message || 'Unable to update password.');
      return;
    }
    toast('good', 'Password updated', json.message || 'You can now log in.');
    if (json.redirect) {
      setTimeout(() => { window.location.href = appUrl(json.redirect); }, 650);
    }
  });
}

function setupProfileMenu() {
  const toggle = qs('[data-profile-toggle]');
  const menu = qs('[data-profile-menu]');
  if (!toggle || !menu) return;

  const setOpen = (open) => {
    menu.hidden = !open;
    toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  };

  toggle.addEventListener('click', () => setOpen(menu.hidden));

  document.addEventListener('click', (e) => {
    if (menu.hidden) return;
    const t = e.target;
    if (toggle.contains(t) || menu.contains(t)) return;
    setOpen(false);
  });

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') setOpen(false);
  });
}

function setupChangePassword() {
  const form = qs('form.js-change-password');
  if (!form) return;

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const json = await postForm(form);
    if (!json.ok) {
      toast('bad', 'Update failed', json.message || 'Unable to update password.');
      return;
    }
    toast('good', 'Updated', json.message || 'Password updated.');
    if (json.redirect) setTimeout(() => { window.location.href = appUrl(json.redirect); }, 650);
  });
}

function showModal(item) {
  const modal = qs('#moduleModal');
  if (!modal) return;

  qs('#moduleTcode').textContent = item.tcode || 'T-code';
  qs('#moduleTitle').textContent = item.module_name || 'Module';
  qs('#moduleDesc').textContent = item.description || '';
  qs('#moduleDetails').textContent = item.function_details || '';

  modal.hidden = false;
  document.body.style.overflow = 'hidden';
}

function hideModal() {
  const modal = qs('#moduleModal');
  if (!modal) return;
  modal.hidden = true;
  document.body.style.overflow = '';
}

function setupModalClose() {
  qsa('[data-modal-close]').forEach((el) => {
    el.addEventListener('click', hideModal);
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') hideModal();
  });
}

function renderDirectory(items) {
  const dirList = qs('#dirList');
  const moduleGrid = qs('#moduleGrid');
  if (!dirList || !moduleGrid) return;

  dirList.innerHTML = '';
  moduleGrid.innerHTML = '';

  for (const it of items) {
    const dir = document.createElement('div');
    dir.className = 'dirItem';
    dir.dataset.tcode = it.tcode;
    dir.innerHTML = `
      <div class="dirItem__top">
        <div class="dirItem__code">${it.tcode}</div>
        <div class="dirItem__badge">Active</div>
      </div>
      <div class="dirItem__name"></div>
      <div class="dirItem__desc"></div>
    `;
    dir.querySelector('.dirItem__name').textContent = it.module_name || '';
    dir.querySelector('.dirItem__desc').textContent = it.description || '';
    dir.addEventListener('click', () => showModal(it));
    dirList.appendChild(dir);

    const card = document.createElement('div');
    card.className = 'moduleCard';
    card.dataset.tcode = it.tcode;
    card.innerHTML = `
      <div class="moduleCard__top">
        <div class="moduleCard__code">${it.tcode}</div>
        <div class="moduleCard__status">Active</div>
      </div>
      <div class="moduleCard__name"></div>
      <div class="moduleCard__desc"></div>
    `;
    card.querySelector('.moduleCard__name').textContent = it.module_name || '';
    card.querySelector('.moduleCard__desc').textContent = it.function_details || it.description || '';
    card.addEventListener('click', () => showModal(it));
    moduleGrid.appendChild(card);
  }
}

function filterDirectory(items, query) {
  const q = String(query || '').trim().toUpperCase();
  if (!q) return items;
  return items.filter((it) => {
    const a = (it.tcode || '').toUpperCase();
    const b = (it.module_name || '').toUpperCase();
    const c = (it.description || '').toUpperCase();
    return a.includes(q) || b.includes(q) || c.includes(q);
  });
}

async function loadTcodes() {
  const res = await fetch(getBackendUrl(), {
    method: 'POST',
    headers: { 'Accept': 'application/json' },
    body: new URLSearchParams({ action: 'list_tcodes' })
  });

  const json = await res.json().catch(() => ({ ok: false, message: 'Unable to load directory.' }));
  if (!json.ok) {
    toast('bad', 'Directory', json.message || 'Unable to load transaction directory.');
    return [];
  }
  return Array.isArray(json.items) ? json.items : [];
}

async function launchTcode(tcode) {
  const res = await fetch(getBackendUrl(), {
    method: 'POST',
    headers: { 'Accept': 'application/json' },
    body: new URLSearchParams({ action: 'launch_tcode', tcode: String(tcode || '') })
  });

  const json = await res.json().catch(() => ({ ok: false, message: 'Unable to launch.' }));
  if (!json.ok) {
    toast('warn', 'Launch', json.message || 'Transaction code not found.');
    return null;
  }
  return json.item || null;
}

async function setupDashboard() {
  const page = document.body.getAttribute('data-page');
  if (page !== 'dashboard') return;

  const kpiModules = qs('#kpiModules');
  const kpiToday = qs('#kpiToday');
  if (kpiToday) {
    const d = new Date();
    kpiToday.textContent = d.toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' });
  }

  const all = await loadTcodes();
  if (kpiModules) kpiModules.textContent = String(all.length);

  let current = all.slice();
  renderDirectory(current);

  const filter = qs('#directoryFilter');
  if (filter) {
    filter.addEventListener('input', () => {
      current = filterDirectory(all, filter.value);
      renderDirectory(current);
    });
  }

  const input = qs('#tcodeInput');
  const btn = qs('#launchBtn');
  const doLaunch = async () => {
    if (!input) return;
    const code = String(input.value || '').trim();
    if (!code) {
      toast('warn', 'Launch', 'Enter a transaction code.');
      return;
    }
    const item = await launchTcode(code);
    if (item) {
      input.value = item.tcode || code.toUpperCase();
      showModal(item);
    }
  };

  if (btn) btn.addEventListener('click', doLaunch);
  if (input) input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      doLaunch();
    }
  });
}

document.addEventListener('DOMContentLoaded', () => {
  setupFlash();
  setupExactAuth();
  setupAuthAjax();
  setupForgotFlow();
  setupProfileMenu();
  setupChangePassword();
  setupModalClose();
  setupDashboard();
});

