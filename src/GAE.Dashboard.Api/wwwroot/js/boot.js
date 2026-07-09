/*
 * Power-on boot sequence for the portal. Plays once per browser session,
 * is skippable (any key / click), and is fully skipped under reduced-motion.
 * Decoupled from app state: it's just a self-removing overlay.
 */
(function () {
  var screen = document.getElementById('boot-screen');
  if (!screen) return;
  var log = document.getElementById('boot-log');

  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var alreadyBooted = false;
  try { alreadyBooted = sessionStorage.getItem('gae.booted') === '1'; } catch (e) {}

  function remove() { if (screen && screen.parentNode) screen.parentNode.removeChild(screen); }
  function finish() {
    screen.classList.add('boot-done');
    setTimeout(remove, 650);
  }

  // Returning within the session, or "I'd like calm" — don't perform.
  if (reduce || alreadyBooted) { remove(); return; }
  try { sessionStorage.setItem('gae.booted', '1'); } catch (e) {}

  var lines = [
    'power-on self test',
    'loading world registry',
    'waking the narrator',
    'restoring persistent state',
    'establishing realtime link'
  ];

  var i = 0;
  var done = false;

  function step() {
    if (done || !log) return;
    if (i < lines.length) {
      var el = document.createElement('span');
      el.className = 'boot-line';
      el.innerHTML = '<span class="boot-dots">&gt; ' + lines[i++] + '</span><span class="ok">[ OK ]</span>';
      log.appendChild(el);
      setTimeout(step, 150);
    } else {
      var rd = document.createElement('span');
      rd.className = 'boot-line';
      rd.innerHTML = '<span class="ready">READY.</span>';
      log.appendChild(rd);
      setTimeout(finish, 520);
    }
  }

  // Begin after the power-on flash + content fade-in have played.
  setTimeout(step, 580);

  function skip() {
    if (done) return;
    done = true;
    finish();
  }
  screen.addEventListener('click', skip);
  function onKey() { skip(); document.removeEventListener('keydown', onKey); }
  document.addEventListener('keydown', onKey);
})();
