/* ═══════════════════════════════════════════════════════════
   WavenVoIP — main.js · Premium Landing Page
   ═══════════════════════════════════════════════════════════ */

document.addEventListener('DOMContentLoaded', () => {
  initNav();
  initHeroDialer();
  initShowcase();
  initScrollReveal();
  initCounters();
  initSmoothScroll();
  initHistoryTabs();
});

/* ── NAV ────────────────────────────────────────────────── */
function initNav() {
  const nav = document.getElementById('nav');
  const burger = document.getElementById('navBurger');
  const links = document.getElementById('navLinks');

  window.addEventListener('scroll', () => {
    nav.classList.toggle('scrolled', window.scrollY > 20);
  }, { passive: true });

  burger?.addEventListener('click', () => {
    links.classList.toggle('open');
  });

  links?.querySelectorAll('a').forEach(a => {
    a.addEventListener('click', () => links.classList.remove('open'));
  });
}

/* ── HERO DIALER ─────────────────────────────────────────── */
function initHeroDialer() {
  const display = document.getElementById('heroDisplay');
  if (!display) return;
  let num = '';

  document.querySelectorAll('.aw-dk').forEach(btn => {
    btn.addEventListener('click', () => {
      if (num.length < 14) {
        num += btn.dataset.h;
        display.textContent = num || '_';
      }
    });
  });

  document.querySelector('.aw-call')?.addEventListener('click', () => {
    if (num) {
      display.textContent = 'Chamando...';
      setTimeout(() => {
        num = '';
        display.textContent = '_';
      }, 2000);
    }
  });
}

/* ── APP SHOWCASE ─────────────────────────────────────────── */
function initShowcase() {
  // Outer tab buttons
  const tabs    = document.querySelectorAll('.sc-tab');
  const screens = document.querySelectorAll('.scw-screen');
  const appNavBtns = document.querySelectorAll('.scw-nb');

  // Map outer tab → screen id and app nav screen key
  const tabMap = {
    dialer:       { screen: 'scr-dialer',       nav: 'dialer'    },
    history:      { screen: 'scr-history',      nav: 'history'   },
    dashboard:    { screen: 'scr-dashboard',    nav: 'dashboard' },
    contacts:     { screen: 'scr-contacts',     nav: 'contacts'  },
    conference:   { screen: 'scr-conference',   nav: null        },
    logs:         { screen: 'scr-logs',         nav: null        },
    integrations: { screen: 'scr-integrations', nav: null        },
  };

  function activateTab(tabKey) {
    const map = tabMap[tabKey];
    if (!map) return;

    // outer tabs
    tabs.forEach(t => t.classList.toggle('active', t.dataset.tab === tabKey));

    // screens
    screens.forEach(s => s.classList.toggle('active', s.id === map.screen));

    // app nav highlight
    appNavBtns.forEach(nb => {
      nb.classList.toggle('active', nb.dataset.screen === map.nav);
    });
  }

  tabs.forEach(tab => {
    tab.addEventListener('click', () => activateTab(tab.dataset.tab));
  });

  // App nav buttons also switch screens (reverse-map)
  const navToTab = { dialer:'dialer', history:'history', dashboard:'dashboard', contacts:'contacts', settings:null };
  appNavBtns.forEach(nb => {
    nb.addEventListener('click', () => {
      const target = navToTab[nb.dataset.screen];
      if (target) activateTab(target);
    });
  });

  // Showcase dialer
  initScDialer();

  // History inner tabs
  document.querySelectorAll('.scwh-tab').forEach(ht => {
    ht.addEventListener('click', () => {
      document.querySelectorAll('.scwh-tab').forEach(x => x.classList.remove('active'));
      ht.classList.add('active');
    });
  });
}

function initScDialer() {
  const input = document.getElementById('scDialInput');
  const bksp  = document.getElementById('scBksp');
  if (!input) return;
  let val = '';

  document.querySelectorAll('.scdk').forEach(btn => {
    btn.addEventListener('click', () => {
      if (val.length < 15) {
        val += btn.dataset.d;
        input.value = val;
      }
    });
  });

  bksp?.addEventListener('click', () => {
    val = val.slice(0, -1);
    input.value = val;
  });

  document.querySelector('.scwd-call')?.addEventListener('click', () => {
    if (val) {
      const orig = input.value;
      input.value = 'Chamando...';
      setTimeout(() => {
        val = '';
        input.value = '';
      }, 2000);
    }
  });
}

/* ── HISTORY INNER TABS ──────────────────────────────────── */
function initHistoryTabs() {
  document.querySelectorAll('.scwh-tab').forEach(tab => {
    tab.addEventListener('click', () => {
      document.querySelectorAll('.scwh-tab').forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
    });
  });
}

/* ── SCROLL REVEAL ───────────────────────────────────────── */
function initScrollReveal() {
  const els = document.querySelectorAll('.reveal');
  if (!els.length) return;

  const io = new IntersectionObserver((entries) => {
    entries.forEach(e => {
      if (e.isIntersecting) {
        e.target.classList.add('visible');
        io.unobserve(e.target);
      }
    });
  }, { threshold: 0.12, rootMargin: '0px 0px -40px 0px' });

  els.forEach((el, i) => {
    el.style.transitionDelay = `${(i % 4) * 0.07}s`;
    io.observe(el);
  });
}

/* ── ANIMATED COUNTERS ───────────────────────────────────── */
function initCounters() {
  const els = document.querySelectorAll('[data-count]');
  if (!els.length) return;

  const io = new IntersectionObserver((entries) => {
    entries.forEach(e => {
      if (e.isIntersecting) {
        animateCount(e.target);
        io.unobserve(e.target);
      }
    });
  }, { threshold: 0.5 });

  els.forEach(el => io.observe(el));
}

function animateCount(el) {
  const target = parseInt(el.dataset.count, 10);
  const duration = 1400;
  const start = performance.now();

  function tick(now) {
    const pct = Math.min((now - start) / duration, 1);
    const ease = 1 - Math.pow(1 - pct, 3);
    el.textContent = Math.round(ease * target);
    if (pct < 1) requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
}

/* ── SMOOTH SCROLL ───────────────────────────────────────── */
function initSmoothScroll() {
  document.querySelectorAll('a[href^="#"]').forEach(a => {
    a.addEventListener('click', e => {
      const target = document.querySelector(a.getAttribute('href'));
      if (target) {
        e.preventDefault();
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    });
  });
}
