/* NetworkFolderManager Web – läuft in jedem Browser (macOS, iPhone, Linux, Windows).
   Keine externen Abhängigkeiten. Alle Inhalte werden per DOM-API erzeugt (kein innerHTML mit Nutzerdaten). */
'use strict';
(() => {

// ---------------------------------------------------------------- Sprache
// Reihenfolge: Profil (nach der Anmeldung) > zuletzt gewählt (dieser Browser) > Browsersprache
const storedLang = (() => { try { return localStorage.getItem('nfm.lang'); } catch { return null; } })();
let EN = storedLang ? storedLang === 'en' : !(navigator.language || 'de').toLowerCase().startsWith('de');
const LOCALE = () => (EN ? 'en-GB' : 'de-DE');
document.documentElement.lang = EN ? 'en' : 'de';

/** Sprache wechseln; angemeldet → auch im Profil speichern (gilt dann auf allen Geräten und in der Windows-App). */
async function setLanguage(lang, saveToProfile = true) {
  EN = lang === 'en';
  document.documentElement.lang = lang;
  try { localStorage.setItem('nfm.lang', lang); } catch { /* egal */ }
  if (saveToProfile && S.user && !S.user.mustChangePassword && S.user.language !== lang) {
    try { S.user = await api('PUT', '/auth/language', { language: lang }); } catch { /* offline o.ä. */ }
  }
  render();
}

/** Nach der Anmeldung: Profilsprache übernehmen bzw. – falls noch keine gesetzt – die aktuelle speichern. */
async function applyProfileLanguage() {
  if (!S.user || S.user.mustChangePassword) return;
  const lang = EN ? 'en' : 'de';
  if (S.user.language === 'de' || S.user.language === 'en') {
    if (S.user.language !== lang) {
      EN = S.user.language === 'en';
      document.documentElement.lang = S.user.language;
      try { localStorage.setItem('nfm.lang', S.user.language); } catch { }
    }
  } else {
    try { S.user = await api('PUT', '/auth/language', { language: lang }); } catch { }
  }
}

// Der Server antwortet auf Englisch; Fehler werden anhand des Codes übersetzt.
const ERRORS_DE = {
  tooManyAttempts: 'Zu viele Fehlversuche. Bitte in einigen Minuten erneut versuchen.',
  badLogin: 'Benutzername oder Passwort ist falsch.',
  wrongCurrentPassword: 'Das bisherige Passwort ist falsch.',
  wrongPassword: 'Falsches Ordnerpasswort.',
  samePassword: 'Das neue Passwort muss sich vom bisherigen unterscheiden.',
  notFound: 'Nicht gefunden.',
  notOwner: 'Das darf nur der Besitzer des Ordners.',
  ownerMustOpenFirst: 'Dieser Ordner muss zuerst von seinem Besitzer geöffnet werden.',
  mustChangePassword: 'Dieser Ordner wurde für dich angelegt. Bitte lege beim ersten Öffnen ein eigenes Passwort fest.',
  isDir: 'Das ist ein Ordner.',
  noUser: 'Diesen Benutzer gibt es nicht.',
  cannotDeleteSelf: 'Du kannst dein eigenes Profil hier nicht löschen.',
  badServerName: 'Bitte einen Servernamen (1–64 Zeichen) angeben.',
  unauthorized: 'Bitte anmelden.',
  mustChangeAccountPassword: 'Bitte zuerst ein eigenes Passwort für dein Profil festlegen.',
  notAdmin: 'Nur für Admins.',
  csrf: 'Anfrage abgelehnt.',
  badRequest: 'Ungültige Anfrage.',
  io: 'Speicherfehler auf dem Server.',
  folderLocked: 'Der Ordner ist gesperrt. Bitte erneut das Ordnerpasswort eingeben.',
  tooLarge: 'Anfrage zu groß.',
  exists: 'Ein Element mit diesem Namen existiert bereits.',
  noParent: 'Der übergeordnete Ordner existiert nicht.',
  badPath: 'Ungültiger Pfad oder Name.',
  weakPassword: 'Das Passwort muss mindestens 8 Zeichen lang sein.',
  badUsername: 'Benutzername: 3–32 Zeichen, nur Buchstaben, Ziffern, Punkt, Minus und Unterstrich.',
  badName: 'Ungültiger Ordnername: 1–64 Zeichen, ohne / \\ : * ? " < > | % # und nicht mit Leerzeichen oder Punkt am Anfang/Ende.',
  userExists: 'Dieser Benutzername ist bereits vergeben.',
  badSetupCode: 'Der Setup-Code ist falsch.',
  registrationClosed: 'Auf diesem Server können keine neuen Profile angelegt werden. Bitte den Admin fragen.',
  lastAdmin: 'Der letzte Admin kann nicht entfernt werden.',
  folderExists: 'Ein Ordner mit diesem Namen existiert auf dem Server bereits.',
  badLanguage: 'Ungültige Sprache.',
};
const errorText = (code, serverMessage) => (!EN && code && ERRORS_DE[code]) || serverMessage;
const DICT = {
  'Anmelden': 'Sign in', 'Registrieren': 'Create profile', 'Benutzername': 'Username', 'Passwort': 'Password',
  'Passwort wiederholen': 'Repeat password', 'Anzeigename (optional)': 'Display name (optional)',
  'Setup-Code': 'Setup code', 'Abmelden': 'Sign out', 'Passwort ändern': 'Change password',
  'Neuer Ordner': 'New folder', 'Meine Ordner': 'My folders', 'Weitere Ordner auf {0}': 'Other folders on {0}',
  'Noch keine eigenen Ordner.': 'No folders yet.', 'Ordnerpasswort': 'Folder password', 'Öffnen': 'Open',
  'Abbrechen': 'Cancel', 'Schließen': 'Close', 'Speichern': 'Save', 'Löschen': 'Delete', 'Fotos': 'Photos',
  'Dateien': 'Files', 'Hochladen': 'Upload', 'Herunterladen': 'Download', 'Umbenennen': 'Rename',
  'Als Netzlaufwerk verbinden': 'Connect as network drive', 'Sperren': 'Lock', 'Ordner löschen': 'Delete folder',
  'Name': 'Name', 'Geändert': 'Modified', 'Größe': 'Size', 'Suchen': 'Search', 'Admin': 'Admin',
  'Benutzer': 'Users', 'Ordner': 'Folders', 'Server': 'Server', 'Besitzer': 'Owner', 'Angelegt': 'Created',
  'Speicher': 'Storage', 'Status': 'Status', 'Rolle': 'Role', 'Letzte Anmeldung': 'Last sign-in',
  'Benutzer anlegen': 'Create user', 'Ordner für Benutzer anlegen': 'Create folder for user',
  'Startpasswort': 'Initial password', 'Besitzer ändern': 'Change owner', 'Passwort zurücksetzen': 'Reset password',
  'Admin-Rechte geben': 'Make admin', 'Admin-Rechte entziehen': 'Remove admin', 'Wartet auf erstes Öffnen': 'Waiting for first open',
  'Aktiv': 'Active', 'Mitglied': 'Member', 'Servername': 'Server name', 'Jeder darf sich ein Profil anlegen': 'Anyone may create a profile',
  'Gespeichert.': 'Saved.', 'Fingerabdruck': 'Fingerprint', 'Version': 'Version', 'Zertifikat herunterladen': 'Download certificate',
  'Noch keine Fotos oder Videos.': 'No photos or videos yet.', 'Fotos hochladen': 'Upload photos',
  'Dieser Ordner ist leer.': 'This folder is empty.', 'Dateien hierher ziehen zum Hochladen': 'Drop files here to upload',
  'Uploads': 'Uploads', 'Fertig': 'Done', 'Fehler': 'Error', 'Hallo, {0}': 'Hello, {0}',
  'Altes Passwort': 'Current password', 'Neues Passwort': 'New password', 'Die Passwörter stimmen nicht überein.': 'The passwords do not match.',
  'Das Passwort muss mindestens 8 Zeichen lang sein.': 'The password must be at least 8 characters long.',
  'Server nicht erreichbar.': 'Server not reachable.', 'Wirklich löschen?': 'Really delete?',
  'Andere Server im Netzwerk': 'Other servers on the network', 'Ordner von {0}': 'Folder of {0}',
  'Neuer Name': 'New name', 'Verbinden': 'Connect', 'Kopieren': 'Copy', 'Kopiert.': 'Copied.',
  'Passwort festlegen': 'Set password', 'Weiter': 'Continue', 'Erstellen': 'Create', 'Anlegen': 'Create',
  'Benutzername oder Passwort ist falsch.': 'Wrong username or password.', 'Falsches Ordnerpasswort.': 'Wrong folder password.',
  'Der Ordner ist gesperrt. Bitte erneut das Ordnerpasswort eingeben.': 'The folder is locked. Please enter the folder password again.',
  'Dieser Benutzername ist bereits vergeben.': 'This username is already taken.',
  'Ein Ordner mit diesem Namen existiert auf dem Server bereits.': 'A folder with this name already exists on this server.',
};
const t = (s, ...a) => (EN && DICT[s] ? DICT[s] : s).replace(/\{(\d)\}/g, (_, i) => a[+i]);

// ---------------------------------------------------------------- Icons (eigene, schlichte Linien-Icons)
const ICONS = {
  folder: '<svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  folderFill: '<svg viewBox="0 0 48 40"><path d="M2 6a4 4 0 0 1 4-4h11l4 4h21a4 4 0 0 1 4 4v24a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4z" opacity=".55"/><path d="M2 12a4 4 0 0 1 4-4h36a4 4 0 0 1 4 4v22a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4z"/></svg>',
  photo: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="3"/><circle cx="9" cy="10" r="2"/><path d="m21 16-5-5-9 9"/></svg>',
  files: '<svg viewBox="0 0 24 24"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>',
  upload: '<svg viewBox="0 0 24 24"><path d="M12 16V4m0 0-4 4m4-4 4 4"/><path d="M4 16v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/></svg>',
  download: '<svg viewBox="0 0 24 24"><path d="M12 4v12m0 0-4-4m4 4 4-4"/><path d="M4 18v1a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-1"/></svg>',
  plus: '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>',
  folderPlus: '<svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 10v6M9 13h6"/></svg>',
  trash: '<svg viewBox="0 0 24 24"><path d="M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V4h6v3"/></svg>',
  lock: '<svg viewBox="0 0 24 24"><rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>',
  key: '<svg viewBox="0 0 24 24"><circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l3 3M15 8l2 2"/></svg>',
  drive: '<svg viewBox="0 0 24 24"><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M5 13 7.5 5h9L19 13M7 16.5h.01M11 16.5h6"/></svg>',
  shield: '<svg viewBox="0 0 24 24"><path d="M12 3 4 6v6c0 5 3.5 8 8 9 4.5-1 8-4 8-9V6z"/><path d="m9 12 2 2 4-4"/></svg>',
  user: '<svg viewBox="0 0 24 24"><circle cx="12" cy="8" r="4"/><path d="M4 21c1-4 4-6 8-6s7 2 8 6"/></svg>',
  logout: '<svg viewBox="0 0 24 24"><path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 16l-4-4 4-4M6 12h10"/></svg>',
  close: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  left: '<svg viewBox="0 0 24 24"><path d="m15 5-7 7 7 7"/></svg>',
  right: '<svg viewBox="0 0 24 24"><path d="m9 5 7 7-7 7"/></svg>',
  more: '<svg viewBox="0 0 24 24"><circle cx="5" cy="12" r="1.3"/><circle cx="12" cy="12" r="1.3"/><circle cx="19" cy="12" r="1.3"/></svg>',
  grid: '<svg viewBox="0 0 24 24"><rect x="4" y="4" width="7" height="7" rx="1.5"/><rect x="13" y="4" width="7" height="7" rx="1.5"/><rect x="4" y="13" width="7" height="7" rx="1.5"/><rect x="13" y="13" width="7" height="7" rx="1.5"/></svg>',
  list: '<svg viewBox="0 0 24 24"><path d="M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01"/></svg>',
  play: '<svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>',
  server: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M4 20h4L19 9l-4-4L4 16z"/></svg>',
  copy: '<svg viewBox="0 0 24 24"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>',
  cert: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M7 8h10M7 11h6"/><circle cx="17" cy="17" r="3"/><path d="m15.5 19.5-1 2.5M18.5 19.5l1 2.5"/></svg>',
  back: '<svg viewBox="0 0 24 24"><path d="M19 12H5m0 0 6-6m-6 6 6 6"/></svg>',
  globe: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.5 2.5 3.5 5.5 3.5 9s-1 6.5-3.5 9c-2.5-2.5-3.5-5.5-3.5-9s1-6.5 3.5-9z"/></svg>',
  doc: '<svg viewBox="0 0 40 48"><path d="M6 2h20l12 12v30a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z" fill="var(--bg-elev)" stroke="var(--line)" stroke-width="1.5"/><path d="M26 2v10a2 2 0 0 0 2 2h10" fill="none" stroke="var(--line)" stroke-width="1.5"/></svg>',
};

// ---------------------------------------------------------------- DOM-Hilfen
function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat(Infinity)) {
    if (kid == null || kid === false) continue;
    el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
  }
  return el;
}
function icon(name, cls = '') {
  const s = document.createElement('span');
  s.className = 'ic ' + cls;
  s.innerHTML = ICONS[name] || ''; // nur feste, eigene SVGs
  s.setAttribute('aria-hidden', 'true');
  return s;
}
const app = document.getElementById('app');
const mount = (...nodes) => app.replaceChildren(...nodes);
const spinner = () => h('div', { class: 'empty' }, h('div', { class: 'spinner', style: { margin: '0 auto' } }));
function toast(msg, err = false) {
  const el = h('div', { class: 'toast' + (err ? ' err' : '') }, msg);
  document.getElementById('toasts').append(el);
  setTimeout(() => el.remove(), err ? 6000 : 3000);
}
const go = hash => { if (location.hash === hash) render(); else location.hash = hash; };

// ---------------------------------------------------------------- Formatierung
const fmtSize = b => {
  if (b == null) return '';
  if (b < 1024) return b + ' B';
  const u = ['KB', 'MB', 'GB', 'TB']; let i = -1;
  do { b /= 1024; i++; } while (b >= 1024 && i < u.length - 1);
  return b.toLocaleString(LOCALE(), { maximumFractionDigits: b < 10 ? 1 : 0 }) + ' ' + u[i];
};
const fmtDate = d => d ? new Date(d).toLocaleDateString(LOCALE(), { day: 'numeric', month: 'short', year: 'numeric' }) : '–';
const fmtDateTime = d => d ? new Date(d).toLocaleString(LOCALE(), { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '–';
const monthKey = d => new Date(d).toLocaleDateString(LOCALE(), { month: 'long', year: 'numeric' });
const initials = n => (n || '?').split(/[\s._-]+/).filter(Boolean).slice(0, 2).map(x => x[0].toUpperCase()).join('');
const enc = encodeURIComponent;
const joinPath = (dir, name) => (dir === '/' ? '' : dir) + '/' + name;
const parentPath = p => p.lastIndexOf('/') <= 0 ? '/' : p.slice(0, p.lastIndexOf('/'));
const baseName = p => p.slice(p.lastIndexOf('/') + 1);
const hidden = n => n.startsWith('._') || n === '.DS_Store' || n === 'Thumbs.db' || n === 'desktop.ini';
function randomPassword() {
  const a = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  const r = crypto.getRandomValues(new Uint32Array(12));
  const s = Array.from(r, x => a[x % a.length]).join('');
  return s.slice(0, 4) + '-' + s.slice(4, 8) + '-' + s.slice(8);
}

// ---------------------------------------------------------------- API
class ApiError extends Error {
  constructor(status, code, message) { super(message); this.status = status; this.code = code; }
}
async function api(method, url, body) {
  const opt = { method, headers: { 'X-NFM': '1' }, credentials: 'same-origin' };
  if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  let res;
  try { res = await fetch('/api' + url, opt); } catch { throw new ApiError(0, 'network', t('Server nicht erreichbar.')); }
  if (res.status === 204) return null;
  let data = null;
  try { data = await res.json(); } catch { /* leer */ }
  if (!res.ok) {
    const err = new ApiError(res.status, data && data.code, errorText(data && data.code, (data && data.error) || ('HTTP ' + res.status)));
    if (res.status === 401 && err.code === 'unauthorized' && S.user) { S.user = null; render(); }
    if (res.status === 403 && err.code === 'mustChangeAccountPassword' && S.user) { S.user.mustChangePassword = true; render(); }
    throw err;
  }
  return data;
}
const fileUrl = (f, path, download) => `/api/folders/${f.id}/file?path=${enc(path)}${download ? '&download=1' : ''}`;

// ---------------------------------------------------------------- Zustand & Navigation
const S = {
  info: null, user: null, folders: [], unlocked: {}, authMode: null,
  layout: (() => { try { return localStorage.getItem('nfm.layout') || 'grid'; } catch { return 'grid'; } })(),
  refresh: null, drop: null,
};
function route() {
  const parts = location.hash.replace(/^#\/?/, '').split('/');
  if (parts[0] === 'admin') return { name: 'admin', tab: parts[1] || 'folders' };
  if (parts[0] === 'f' && parts[1]) {
    const view = parts[2] === 'files' ? 'files' : 'photos';
    const path = view === 'files' && parts[3] ? decodeURIComponent(parts.slice(3).join('/')) : '/';
    return { name: 'folder', id: parts[1], view, path: path.startsWith('/') ? path : '/' + path };
  }
  return { name: 'home' };
}
const folderHash = (id, view, path) => `#/f/${id}/${view}` + (view === 'files' && path && path !== '/' ? '/' + enc(path) : '');

async function render() {
  closeMenu();
  S.refresh = null; S.drop = null;
  if (!S.user) return renderAuth();
  if (S.user.mustChangePassword) return renderAccountPasswordRequired();
  const r = route();
  if (r.name === 'admin' && S.user.isAdmin) return renderAdmin(r.tab);
  if (r.name === 'folder') return renderFolder(r);
  return renderHome();
}

// ---------------------------------------------------------------- Dialoge
function dialog({ title, sub, notice, fields = [], body, ok, okClass = '', cancel = t('Abbrechen'), submit, wide }) {
  return new Promise(resolve => {
    const err = h('div', { class: 'error', role: 'alert' });
    const inputs = {};
    const okBtn = ok ? h('button', { class: 'btn ' + okClass, type: 'submit' }, ok) : null;
    const close = val => { overlay.remove(); document.removeEventListener('keydown', onKey); resolve(val); };
    const onKey = e => { if (e.key === 'Escape') close(null); };
    const fieldEls = fields.map(fd => {
      let input;
      if (fd.type === 'checkbox') {
        input = h('input', { type: 'checkbox', checked: fd.value });
        inputs[fd.name] = input;
        return h('label', { class: 'check' }, input, fd.label);
      }
      if (fd.type === 'select') {
        input = h('select', { id: 'f_' + fd.name }, fd.options.map(o => h('option', { value: o.value, selected: o.value === fd.value ? true : null }, o.label)));
      } else {
        input = h('input', {
          id: 'f_' + fd.name, type: fd.type || 'text', value: fd.value ?? '', placeholder: fd.placeholder,
          autocomplete: fd.autocomplete || 'off', required: fd.required ? true : null, spellcheck: 'false', autocapitalize: 'off',
        });
      }
      inputs[fd.name] = input;
      return h('div', { class: 'field' }, h('label', { for: 'f_' + fd.name }, fd.label), input, fd.hint ? h('div', { class: 'hint' }, fd.hint) : null);
    });
    const form = h('form', {
      class: 'dialog' + (wide ? ' wide' : ''), role: 'dialog', 'aria-modal': 'true',
      onsubmit: async e => {
        e.preventDefault();
        err.textContent = '';
        const vals = {};
        for (const [k, el] of Object.entries(inputs)) vals[k] = el.type === 'checkbox' ? el.checked : el.value;
        if (okBtn) okBtn.disabled = true;
        try {
          const r = submit ? await submit(vals) : true;
          if (r !== false) close(vals);
        } catch (ex) { err.textContent = ex.message; }
        finally { if (okBtn) okBtn.disabled = false; }
      },
    },
      h('h2', {}, title),
      sub ? h('div', { class: 'sub' }, sub) : null,
      notice ? h('div', { class: 'notice' }, notice) : null,
      body || null,
      fieldEls,
      err,
      h('div', { class: 'actions' },
        cancel ? h('button', { class: 'btn secondary', type: 'button', onclick: () => close(null) }, cancel) : null,
        okBtn));
    const overlay = h('div', { class: 'overlay', onmousedown: e => { if (e.target === overlay) close(null); } }, form);
    document.addEventListener('keydown', onKey);
    document.body.append(overlay);
    setTimeout(() => (form.querySelector('input:not([type=checkbox]),select') || okBtn)?.focus(), 30);
  });
}
const confirmDialog = (title, text, okLabel = t('Löschen')) =>
  dialog({ title, sub: text, ok: okLabel, okClass: 'danger' }).then(v => v !== null);

function checkNewPassword(a, b) {
  if ((a || '').length < 8) throw new Error(t('Das Passwort muss mindestens 8 Zeichen lang sein.'));
  if (a !== b) throw new Error(t('Die Passwörter stimmen nicht überein.'));
}

// ---------------------------------------------------------------- Menü
let menuEl = null;
function closeMenu() { if (menuEl) { menuEl.remove(); menuEl = null; } }
function menu(anchor, items, head) {
  closeMenu();
  const r = anchor.getBoundingClientRect();
  menuEl = h('div', { class: 'menu', role: 'menu' },
    head ? h('div', { class: 'head' }, head) : null,
    items.filter(Boolean).map(it => h('button', {
      class: it.danger ? 'danger' : '', role: 'menuitem',
      onclick: e => { e.stopPropagation(); closeMenu(); it.action(); },
    }, icon(it.icon), it.label)));
  document.body.append(menuEl);
  const mw = menuEl.offsetWidth, mh = menuEl.offsetHeight;
  menuEl.style.left = Math.max(8, Math.min(r.right - mw, innerWidth - mw - 8)) + 'px';
  menuEl.style.top = (r.bottom + 6 + mh > innerHeight ? Math.max(8, r.top - mh - 6) : r.bottom + 6) + 'px';
  setTimeout(() => document.addEventListener('click', closeMenu, { once: true }), 0);
}

// ---------------------------------------------------------------- Anmeldung
function renderAuth() {
  const info = S.info;
  const canRegister = info.allowRegistration || info.needsSetup;
  const mode = S.authMode || (info.needsSetup ? 'register' : 'login');
  const err = h('div', { class: 'error', role: 'alert' });
  const field = (name, label, type = 'text', auto = 'off', hint) => h('div', { class: 'field' },
    h('label', { for: name }, label),
    h('input', { id: name, name, type, autocomplete: auto, required: name !== 'displayName' && name !== 'setupCode' ? true : null, autocapitalize: 'off', spellcheck: 'false' }),
    hint ? h('div', { class: 'hint' }, hint) : null);

  const fields = mode === 'login'
    ? [field('username', t('Benutzername'), 'text', 'username'), field('password', t('Passwort'), 'password', 'current-password')]
    : [
      info.needsSetup ? h('div', { class: 'notice info' }, EN
        ? 'This server has no admin yet. Enter the setup code from the server log (or "nfm-server info") to become the admin.'
        : 'Dieser Server hat noch keinen Admin. Mit dem Setup-Code aus dem Server-Protokoll (oder „nfm-server info“) wirst du Admin.') : null,
      field('username', t('Benutzername'), 'text', 'username', EN ? '3–32 characters: letters, digits, . _ -' : '3–32 Zeichen: Buchstaben, Ziffern, . _ -'),
      field('displayName', t('Anzeigename (optional)'), 'text', 'name'),
      field('password', t('Passwort'), 'password', 'new-password', EN ? 'At least 8 characters' : 'Mindestens 8 Zeichen'),
      field('password2', t('Passwort wiederholen'), 'password', 'new-password'),
      info.needsSetup ? field('setupCode', t('Setup-Code'), 'text', 'off') : null,
    ];

  const submit = h('button', { class: 'btn wide', type: 'submit' }, mode === 'login' ? t('Anmelden') : t('Registrieren'));
  const form = h('form', {
    onsubmit: async e => {
      e.preventDefault();
      err.textContent = '';
      const v = Object.fromEntries(new FormData(form));
      submit.disabled = true;
      try {
        let res;
        if (mode === 'login') {
          res = await api('POST', '/auth/login', { username: v.username, password: v.password, device: deviceName() });
        } else {
          checkNewPassword(v.password, v.password2);
          res = await api('POST', '/auth/register', {
            username: v.username, password: v.password, displayName: v.displayName || null,
            setupCode: v.setupCode || null, device: deviceName(),
          });
          S.info = await api('GET', '/info');
        }
        S.user = res.user;
        await applyProfileLanguage();
        go('#/');
      } catch (ex) { err.textContent = ex.message; }
      finally { submit.disabled = false; }
    },
  }, fields, err, submit);

  const servers = h('div', { class: 'servers', hidden: true });
  api('GET', '/servers').then(list => {
    if (!list || !list.length) return;
    servers.hidden = false;
    servers.append(h('div', { class: 'small muted', style: { marginBottom: '6px' } }, t('Andere Server im Netzwerk')),
      list.map(s => h('a', { href: s.url }, icon('server'), h('span', { class: 'grow' }, s.name), h('span', { class: 'small muted' }, s.address))));
  }).catch(() => { });

  mount(h('div', { class: 'auth' }, h('div', { class: 'auth-card' },
    h('div', { class: 'auth-logo' }, h('img', { src: 'icon.svg', alt: '' }),
      h('h1', { style: { fontSize: '22px' } }, info.name),
      h('div', { class: 'muted small' }, 'NetworkFolderManager · ' + (EN ? 'encrypted folders' : 'verschlüsselte Ordner'))),
    canRegister ? h('div', { class: 'seg', role: 'tablist' },
      h('button', { type: 'button', class: mode === 'login' ? 'on' : '', onclick: () => { S.authMode = 'login'; render(); } }, t('Anmelden')),
      h('button', { type: 'button', class: mode === 'register' ? 'on' : '', onclick: () => { S.authMode = 'register'; render(); } }, t('Registrieren'))) : null,
    form,
    servers,
    h('div', { class: 'auth-foot' },
      EN ? 'Certificate warning? ' : 'Zertifikatswarnung? ',
      h('a', { href: '/ca.crt' }, t('Zertifikat herunterladen')),
      h('div', { class: 'mono', style: { marginTop: '6px' } }, fpShort(info.fingerprint)),
      h('div', { style: { marginTop: '10px' } },
        h('a', { href: '#', onclick: e => { e.preventDefault(); setLanguage('de'); }, style: { fontWeight: EN ? 400 : 700 } }, 'Deutsch'),
        ' · ',
        h('a', { href: '#', onclick: e => { e.preventDefault(); setLanguage('en'); }, style: { fontWeight: EN ? 700 : 400 } }, 'English'))))));
  setTimeout(() => form.querySelector('input')?.focus(), 30);
}
const fpShort = fp => (fp || '').slice(0, 32).replace(/(.{4})/g, '$1 ').trim();
function deviceName() {
  const ua = navigator.userAgent;
  const os = /iPhone/.test(ua) ? 'iPhone' : /iPad/.test(ua) ? 'iPad' : /Mac OS X/.test(ua) ? 'Mac' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : '';
  const br = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Firefox\//.test(ua) ? 'Firefox' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
  return `${br} (${os || 'Web'})`;
}

function renderAccountPasswordRequired() {
  mount(h('div', { class: 'auth' }, h('div', { class: 'auth-card' },
    h('div', { class: 'auth-logo' }, icon('key', 'xl'), h('h1', { style: { fontSize: '22px' } }, t('Passwort festlegen')),
      h('div', { class: 'muted' }, EN ? 'Your profile was created or reset by the admin. Please choose your own password.'
        : 'Dein Profil wurde vom Admin angelegt oder zurückgesetzt. Bitte wähle ein eigenes Passwort.')))));
  dialog({
    title: t('Passwort festlegen'), cancel: t('Abmelden'), ok: t('Speichern'),
    fields: [
      { name: 'old', label: EN ? 'Initial password (from the admin)' : 'Startpasswort (vom Admin)', type: 'password', autocomplete: 'current-password' },
      { name: 'pw', label: t('Neues Passwort'), type: 'password', autocomplete: 'new-password' },
      { name: 'pw2', label: t('Passwort wiederholen'), type: 'password', autocomplete: 'new-password' },
    ],
    submit: async v => {
      checkNewPassword(v.pw, v.pw2);
      S.user = await api('POST', '/auth/password', { oldPassword: v.old, newPassword: v.pw });
      await applyProfileLanguage();
      toast(t('Gespeichert.'));
      render();
    },
  }).then(v => { if (v === null) logout(); });
}

async function logout() {
  try { await api('POST', '/auth/logout'); } catch { /* egal */ }
  S.user = null; S.unlocked = {}; S.folders = [];
  location.hash = '#/';
  render();
}

function changeAccountPassword() {
  dialog({
    title: t('Passwort ändern'), sub: EN ? 'Password for your profile on this server.' : 'Passwort deines Profils auf diesem Server.', ok: t('Speichern'),
    fields: [
      { name: 'old', label: t('Altes Passwort'), type: 'password', autocomplete: 'current-password' },
      { name: 'pw', label: t('Neues Passwort'), type: 'password', autocomplete: 'new-password' },
      { name: 'pw2', label: t('Passwort wiederholen'), type: 'password', autocomplete: 'new-password' },
    ],
    submit: async v => {
      checkNewPassword(v.pw, v.pw2);
      S.user = await api('POST', '/auth/password', { oldPassword: v.old, newPassword: v.pw });
      toast(t('Gespeichert.'));
    },
  });
}

// ---------------------------------------------------------------- Kopfzeile
function topbar() {
  const u = S.user;
  return h('header', { class: 'topbar' },
    h('button', { class: 'brand', onclick: () => go('#/') }, h('img', { src: 'icon.svg', alt: '' }),
      h('span', {}, 'NetworkFolderManager'), h('span', { class: 'srv' }, '· ' + S.info.name)),
    h('div', { class: 'spacer' }),
    u.isAdmin ? h('button', { class: 'btn secondary', onclick: () => go('#/admin') }, icon('shield'), t('Admin')) : null,
    h('button', {
      class: 'avatar', title: u.displayName, 'aria-label': u.displayName,
      onclick: e => {
        e.stopPropagation();
        menu(e.currentTarget, [
          { icon: 'key', label: t('Passwort ändern'), action: changeAccountPassword },
          u.isAdmin ? { icon: 'shield', label: t('Admin'), action: () => go('#/admin') } : null,
          { icon: 'globe', label: EN ? 'Sprache: Deutsch' : 'Language: English', action: () => setLanguage(EN ? 'de' : 'en') },
          { icon: 'cert', label: t('Zertifikat herunterladen'), action: () => { location.href = '/ca.crt'; } },
          { icon: 'logout', label: t('Abmelden'), action: logout, danger: true },
        ], [h('div', { style: { fontWeight: 600 } }, u.displayName), h('div', { class: 'small muted' }, '@' + u.username + ' · ' + S.info.name)]);
      },
    }, initials(u.displayName)));
}

// ---------------------------------------------------------------- Startseite
async function renderHome() {
  const main = h('main', { class: 'page' }, spinner());
  mount(topbar(), main);
  try { S.folders = await api('GET', '/folders'); } catch (e) { main.replaceChildren(h('div', { class: 'empty' }, e.message)); return; }
  const mine = S.folders.filter(f => f.isMine), others = S.folders.filter(f => !f.isMine);

  const tile = f => h('button', { class: 'tile', onclick: () => openFolder(f) },
    icon('folderFill', 'fi'),
    h('div', { class: 'name' }, f.name),
    f.mustChangePassword ? h('span', {}, h('span', { class: 'badge warn' }, EN ? 'New – set password' : 'Neu – Passwort festlegen')) : null,
    h('div', { class: 'meta' }, icon('lock', ''), ' ', EN ? 'Created ' : 'Erstellt ', fmtDate(f.createdAt)));

  main.replaceChildren(
    h('div', { class: 'page-head' },
      h('div', {}, h('h1', {}, t('Hallo, {0}', S.user.displayName)),
        h('div', { class: 'muted' }, EN ? 'Your folders are encrypted with their own password – only people who know it can see the contents.'
          : 'Deine Ordner sind mit ihrem eigenen Passwort verschlüsselt – nur wer es kennt, sieht den Inhalt.')),
      h('button', { class: 'btn', onclick: createFolderDialog }, icon('plus'), t('Neuer Ordner'))),
    h('section', { class: 'section' }, h('h2', {}, t('Meine Ordner')),
      h('div', { class: 'tiles' }, mine.map(tile),
        h('button', { class: 'tile add', onclick: createFolderDialog }, icon('plus', 'lg'), t('Neuer Ordner')))),
    others.length ? h('section', { class: 'section' }, h('h2', {}, t('Weitere Ordner auf {0}', S.info.name)),
      h('div', { class: 'list' }, others.map(f => h('div', { class: 'row', role: 'button', tabindex: 0, onclick: () => openFolder(f), onkeydown: e => { if (e.key === 'Enter') openFolder(f); } },
        icon('folder', 'lg'), h('div', { class: 'grow' }, h('div', { class: 'title' }, f.name), h('div', { class: 'small muted' }, t('Ordner von {0}', f.ownerName))),
        icon('lock'))))) : null);
}

function createFolderDialog() {
  dialog({
    title: t('Neuer Ordner'),
    sub: EN ? 'The folder is encrypted with this password. Anyone who knows it can open the folder – the admin cannot.'
      : 'Der Ordner wird mit diesem Passwort verschlüsselt. Wer es kennt, kann den Ordner öffnen – der Admin nicht.',
    ok: t('Erstellen'),
    fields: [
      { name: 'name', label: t('Name') },
      { name: 'pw', label: t('Ordnerpasswort'), type: 'password', autocomplete: 'new-password', hint: EN ? 'At least 8 characters. If you forget it, the contents cannot be recovered.' : 'Mindestens 8 Zeichen. Vergessen = Inhalt nicht wiederherstellbar.' },
      { name: 'pw2', label: t('Passwort wiederholen'), type: 'password', autocomplete: 'new-password' },
    ],
    submit: async v => {
      checkNewPassword(v.pw, v.pw2);
      const f = await api('POST', '/folders', { name: v.name, password: v.pw });
      const r = await api('POST', `/folders/${f.id}/unlock`, { password: v.pw });
      S.unlocked[f.id] = r;
      S.folders.push(f);
      go(folderHash(f.id, 'files'));
    },
  });
}

function openFolder(f, view = 'photos') {
  if (S.unlocked[f.id]) return go(folderHash(f.id, view));
  unlockDialog(f, view);
}

function unlockDialog(f, view = 'photos') {
  const first = f.mustChangePassword && f.isMine;
  return dialog({
    title: f.name,
    sub: f.isMine ? (EN ? 'Enter the folder password.' : 'Ordnerpasswort eingeben.') : t('Ordner von {0}', f.ownerName),
    notice: first ? (EN ? 'This folder was created for you by the admin. Enter the initial password – then choose your own. The admin will not be able to see your files.'
      : 'Dieser Ordner wurde vom Admin für dich angelegt. Gib das Startpasswort ein und lege danach ein eigenes fest – der Admin kann deine Dateien dann nicht sehen.') : null,
    ok: first ? t('Weiter') : t('Öffnen'),
    fields: [{ name: 'pw', label: first ? t('Startpasswort') : t('Ordnerpasswort'), type: 'password', autocomplete: 'current-password' }],
    submit: async v => {
      try {
        S.unlocked[f.id] = await api('POST', `/folders/${f.id}/unlock`, { password: v.pw });
        go(folderHash(f.id, view));
      } catch (e) {
        if (e.code === 'mustChangePassword') { setTimeout(() => folderPasswordDialog(f, v.pw, view), 0); return true; }
        throw e;
      }
    },
  });
}

function folderPasswordDialog(f, oldPw, view = 'photos') {
  const first = !!oldPw;
  dialog({
    title: first ? t('Passwort festlegen') : t('Passwort ändern'),
    sub: first ? (EN ? 'Choose your own folder password. A new key is generated so that nobody else can decrypt the folder.'
      : 'Wähle ein eigenes Ordnerpasswort. Es wird ein neuer Schlüssel erzeugt – niemand sonst kann den Ordner entschlüsseln.')
      : (EN ? 'Everyone who opened the folder will have to enter the new password.' : 'Alle, die den Ordner geöffnet haben, müssen das neue Passwort eingeben.'),
    ok: t('Speichern'),
    fields: [
      first ? null : { name: 'old', label: t('Altes Passwort'), type: 'password', autocomplete: 'current-password' },
      { name: 'pw', label: t('Neues Passwort'), type: 'password', autocomplete: 'new-password' },
      { name: 'pw2', label: t('Passwort wiederholen'), type: 'password', autocomplete: 'new-password' },
    ].filter(Boolean),
    submit: async v => {
      checkNewPassword(v.pw, v.pw2);
      S.unlocked[f.id] = await api('POST', `/folders/${f.id}/password`, { oldPassword: oldPw || v.old, newPassword: v.pw });
      f.mustChangePassword = false;
      toast(t('Gespeichert.'));
      go(folderHash(f.id, view));
    },
  });
}

// ---------------------------------------------------------------- Ordneransicht
async function renderFolder(r) {
  if (!S.folders.length) { try { S.folders = await api('GET', '/folders'); } catch { /* unten */ } }
  const f = S.folders.find(x => x.id === r.id);
  if (!f) { go('#/'); return; }

  let stats;
  try {
    stats = await api('GET', `/folders/${f.id}/stats`);
    if (!S.unlocked[f.id]) S.unlocked[f.id] = { davUrl: `${location.origin}/dav/${enc(f.name)}/` };
  } catch (e) {
    delete S.unlocked[f.id];
    await renderHome();
    if (e.code === 'folderLocked') unlockDialog(f, r.view); else toast(e.message, true);
    return;
  }

  const nav = (view, ic, label, count) => h('button', { class: 'nav' + (r.view === view ? ' on' : ''), onclick: () => go(folderHash(f.id, view)) },
    icon(ic), label, count != null ? h('span', { class: 'count' }, count) : null);
  const content = h('section', { class: 'content' });
  const sidebar = h('nav', { class: 'sidebar' },
    h('div', { class: 'fname' }, icon('lock'), f.name),
    nav('photos', 'photo', t('Fotos'), stats.photos + stats.videos),
    nav('files', 'files', t('Dateien'), stats.files),
    h('div', { class: 'lbl' }, t('Ordner')),
    h('button', { class: 'nav', onclick: () => mountDialog(f) }, icon('drive'), h('span', { class: 'desk-label' }, EN ? 'Network drive' : 'Netzlaufwerk')),
    f.isMine ? h('button', { class: 'nav desk', onclick: () => folderPasswordDialog(f) }, icon('key'), t('Passwort ändern')) : null,
    f.isMine ? h('button', { class: 'nav danger desk', onclick: () => deleteOwnFolder(f) }, icon('trash'), t('Ordner löschen')) : null,
    h('div', { class: 'grow' }),
    h('div', { class: 'small muted desk', style: { padding: '0 10px 8px' } }, fmtSize(stats.bytes) + ' · ' + (EN ? 'encrypted' : 'verschlüsselt')),
    h('button', { class: 'nav', onclick: () => lockFolder(f) }, icon('lock'), t('Sperren')));

  mount(topbar(), h('div', { class: 'shell' }, sidebar, content));
  S.drop = files => uploadFiles(f, r.view === 'photos' ? '/Fotos' : r.path, files);
  if (r.view === 'photos') await photosView(f, content);
  else await filesView(f, content, r.path);
}

async function lockFolder(f) {
  try { await api('POST', `/folders/${f.id}/lock`); } catch { /* egal */ }
  delete S.unlocked[f.id];
  go('#/');
}

async function deleteOwnFolder(f) {
  await dialog({
    title: t('Ordner löschen'),
    sub: EN ? `“${f.name}” and all its files will be deleted permanently. Enter the folder password to confirm.`
      : `„${f.name}“ und alle Dateien darin werden endgültig gelöscht. Zur Bestätigung das Ordnerpasswort eingeben.`,
    ok: t('Löschen'), okClass: 'danger',
    fields: [{ name: 'pw', label: t('Ordnerpasswort'), type: 'password', autocomplete: 'current-password' }],
    submit: async v => {
      await api('DELETE', `/folders/${f.id}`, { password: v.pw });
      delete S.unlocked[f.id];
      S.folders = S.folders.filter(x => x.id !== f.id);
      toast(EN ? 'Folder deleted.' : 'Ordner gelöscht.');
      go('#/');
    },
  });
}

function handleFolderError(f, e) {
  if (e.code === 'folderLocked') { delete S.unlocked[f.id]; go('#/'); setTimeout(() => unlockDialog(f), 50); }
  else toast(e.message, true);
}

function uploadButton(f, dir, label = t('Hochladen')) {
  const input = h('input', { type: 'file', multiple: true, hidden: true, onchange: () => { uploadFiles(f, dir, [...input.files]); input.value = ''; } });
  return [input, h('button', { class: 'btn', onclick: () => input.click() }, icon('upload'), label)];
}

// ----- Fotos
async function photosView(f, content) {
  const grid = h('div', {}, spinner());
  content.replaceChildren(
    h('div', { class: 'toolbar' }, h('h1', {}, t('Fotos')), h('div', { class: 'spacer' }), uploadButton(f, '/Fotos')),
    grid);
  S.refresh = () => photosView(f, content);
  let media;
  try { media = await api('GET', `/folders/${f.id}/media`); } catch (e) { return handleFolderError(f, e); }

  if (!media.length) {
    grid.replaceChildren(h('div', { class: 'empty' }, icon('photo'), h('div', {}, t('Noch keine Fotos oder Videos.')),
      h('div', { class: 'small', style: { margin: '6px 0 14px' } }, t('Dateien hierher ziehen zum Hochladen')),
      uploadButton(f, '/Fotos', t('Fotos hochladen'))));
    return;
  }

  const groups = new Map();
  media.forEach((m, i) => { const k = monthKey(m.modified); if (!groups.has(k)) groups.set(k, []); groups.get(k).push([m, i]); });
  grid.replaceChildren(...[...groups].map(([month, items]) => [
    h('h3', { class: 'month' }, month),
    h('div', { class: 'photos' }, items.map(([m, i]) => photoTile(f, m, () => openViewer(f, media, i)))),
  ]).flat());
}

function photoTile(f, m, onclick) {
  const src = fileUrl(f, m.path);
  const ok = e => e.target.classList.add('ok');
  const media = m.kind === 'video'
    ? h('video', { src: src + '#t=0.1', preload: 'metadata', muted: true, playsinline: true, onloadeddata: ok })
    : h('img', { src, alt: m.name, loading: 'lazy', decoding: 'async', onload: ok });
  const tile = h('button', { class: 'ph', title: m.name, onclick }, media,
    m.kind === 'video' ? h('span', { class: 'vid' }, icon('play', 'lg')) : null);
  media.addEventListener('error', () => tile.append(h('span', { class: 'broken' }, m.name)));
  return tile;
}

// ----- Dateien
async function filesView(f, content, path) {
  const body = h('div', {}, spinner());
  const search = h('input', { class: 'search', type: 'search', placeholder: t('Suchen'), oninput: () => draw() });
  const layoutBtn = (mode, ic) => h('button', {
    class: S.layout === mode ? 'on' : '', title: mode, 'aria-label': mode,
    onclick: () => { S.layout = mode; try { localStorage.setItem('nfm.layout', mode); } catch { } filesView(f, content, path); },
  }, icon(ic));

  const crumbs = h('div', { class: 'crumbs' });
  const parts = path.split('/').filter(Boolean);
  crumbs.append(h('button', { class: parts.length ? 'parent' : '', onclick: () => go(folderHash(f.id, 'files')) }, t('Dateien')));
  parts.forEach((p, i) => {
    const target = '/' + parts.slice(0, i + 1).join('/');
    crumbs.append(h('span', { class: 'sep' }, '›'),
      h('button', { class: i < parts.length - 1 ? 'parent' : '', onclick: () => go(folderHash(f.id, 'files', target)) }, p));
  });

  content.replaceChildren(
    h('div', { class: 'toolbar' }, crumbs, h('div', { class: 'spacer' }), search,
      h('div', { class: 'segbtn' }, layoutBtn('grid', 'grid'), layoutBtn('list', 'list')),
      h('button', { class: 'btn secondary', onclick: () => newDirDialog(f, path) }, icon('folderPlus'), t('Neuer Ordner')),
      uploadButton(f, path)),
    body);
  S.refresh = () => filesView(f, content, path);

  let items;
  try { items = await api('GET', `/folders/${f.id}/items?path=${enc(path)}`); } catch (e) {
    if (e.code === 'notFound') { go(folderHash(f.id, 'files')); return; }
    return handleFolderError(f, e);
  }
  items = items.filter(i => !hidden(i.name));

  function draw() {
    const q = search.value.trim().toLowerCase();
    const list = q ? items.filter(i => i.name.toLowerCase().includes(q)) : items;
    if (!list.length) {
      body.replaceChildren(h('div', { class: 'empty' }, icon('files'), h('div', {}, q ? '–' : t('Dieser Ordner ist leer.')),
        q ? null : h('div', { class: 'small', style: { marginTop: '6px' } }, t('Dateien hierher ziehen zum Hochladen'))));
      return;
    }
    const open = it => it.isDir ? go(folderHash(f.id, 'files', it.path)) : openFile(f, it, list);
    const more = it => h('button', {
      class: 'btn icon ghost more', title: '…', 'aria-label': EN ? 'More' : 'Mehr',
      onclick: e => { e.stopPropagation(); itemMenu(f, it, e.currentTarget, open); },
    }, icon('more'));

    if (S.layout === 'list') {
      body.replaceChildren(h('div', { class: 'list files-list' },
        h('div', { class: 'row', style: { cursor: 'default', background: 'transparent' } },
          h('span', { class: 'ficon' }), h('div', { class: 'grow small muted' }, t('Name')),
          h('div', { class: 'col small' }, t('Geändert')), h('div', { class: 'col size small' }, t('Größe')), h('span', { style: { width: '34px' } })),
        list.map(it => h('div', { class: 'row', ondblclick: () => open(it) },
          fileIcon(it, f, true),
          h('div', { class: 'grow' }, h('div', { class: 'title nm', role: 'button', tabindex: 0, onclick: () => open(it), onkeydown: e => { if (e.key === 'Enter') open(it); } }, it.name)),
          h('div', { class: 'col' }, fmtDateTime(it.modified)),
          h('div', { class: 'col size' }, it.isDir ? '–' : fmtSize(it.size)),
          more(it)))));
    } else {
      body.replaceChildren(h('div', { class: 'files-grid' }, list.map(it => h('button', { class: 'fcard', onclick: () => open(it) },
        h('span', { class: 'thumb' }, fileIcon(it, f, false)),
        h('span', { class: 'nm' }, it.name),
        h('span', { class: 'sub' }, it.isDir ? fmtDate(it.modified) : fmtSize(it.size)),
        more(it)))));
    }
  }
  draw();
}

function fileIcon(it, f, small) {
  if (it.isDir) return h('span', { class: 'ficon', style: { color: '#5ac8fa' } }, icon('folderFill', 'ficon'));
  if (it.kind === 'image' && !small) {
    const img = h('img', { src: fileUrl(f, it.path), alt: '', loading: 'lazy', decoding: 'async' });
    img.addEventListener('error', () => img.replaceWith(docIcon(it)));
    return img;
  }
  return docIcon(it);
}
const KIND_COLORS = { image: '#34c759', video: '#af52de', audio: '#ff2d55', pdf: '#ff3b30', text: '#8e8e93', file: '#0a84ff' };
function docIcon(it) {
  const ext = (it.name.includes('.') ? it.name.split('.').pop() : '').slice(0, 4).toUpperCase();
  return h('span', { class: 'ficon', style: { position: 'relative', display: 'inline-block' } }, icon('doc', 'ficon'),
    ext ? h('span', {
      style: {
        position: 'absolute', left: '50%', bottom: '18%', transform: 'translateX(-50%)', background: KIND_COLORS[it.kind] || KIND_COLORS.file,
        color: '#fff', fontSize: '9px', fontWeight: 700, padding: '1px 4px', borderRadius: '3px', letterSpacing: '.02em',
      },
    }, ext) : null);
}

function itemMenu(f, it, anchor, open) {
  menu(anchor, [
    { icon: it.isDir ? 'folder' : 'photo', label: t('Öffnen'), action: () => open(it) },
    it.isDir ? null : { icon: 'download', label: t('Herunterladen'), action: () => download(f, it) },
    { icon: 'edit', label: t('Umbenennen'), action: () => renameDialog(f, it) },
    { icon: 'trash', label: t('Löschen'), danger: true, action: () => deleteItem(f, it) },
  ]);
}

function download(f, it) {
  const a = h('a', { href: fileUrl(f, it.path, true), download: it.name });
  document.body.append(a); a.click(); a.remove();
}

function newDirDialog(f, path) {
  dialog({
    title: t('Neuer Ordner'), ok: t('Erstellen'), fields: [{ name: 'name', label: t('Name') }],
    submit: async v => {
      await api('POST', `/folders/${f.id}/mkdir`, { path: joinPath(path, v.name.trim()) });
      S.refresh && S.refresh();
    },
  });
}

function renameDialog(f, it) {
  dialog({
    title: t('Umbenennen'), ok: t('Speichern'), fields: [{ name: 'name', label: t('Neuer Name'), value: it.name }],
    submit: async v => {
      const name = v.name.trim();
      if (!name || name === it.name) return;
      await api('POST', `/folders/${f.id}/move`, { from: it.path, to: joinPath(parentPath(it.path), name) });
      S.refresh && S.refresh();
    },
  });
}

async function deleteItem(f, it) {
  if (!await confirmDialog(t('Wirklich löschen?'), it.isDir
    ? (EN ? `The folder “${it.name}” and everything in it will be deleted.` : `Der Ordner „${it.name}“ und alles darin wird gelöscht.`)
    : `„${it.name}“`)) return false;
  try {
    await api('POST', `/folders/${f.id}/delete`, { path: it.path });
    S.refresh && S.refresh();
    return true;
  } catch (e) { handleFolderError(f, e); return false; }
}

function openFile(f, it, list) {
  if (it.kind === 'image' || it.kind === 'video' || (it.kind === 'text' && it.size < 2 * 1024 * 1024)) {
    const viewable = list.filter(x => x.kind === 'image' || x.kind === 'video' || (x.kind === 'text' && x.size < 2 * 1024 * 1024));
    openViewer(f, viewable, viewable.indexOf(it));
  } else if (it.kind === 'pdf' || it.kind === 'audio') {
    window.open(fileUrl(f, it.path), '_blank', 'noopener');
  } else {
    download(f, it);
  }
}

// ----- Betrachter
function openViewer(f, list, index) {
  let i = Math.max(0, index);
  const stage = h('div', { class: 'stage' });
  const title = h('div'), sub = h('div');
  const counter = h('div', { class: 'vcount' });
  const vbtn = (ic, label, onclick) => h('button', { class: 'vbtn', title: label, 'aria-label': label, onclick }, icon(ic));
  const prev = h('button', { class: 'vbtn nav-l', 'aria-label': '←', onclick: () => step(-1) }, icon('left'));
  const next = h('button', { class: 'vbtn nav-r', 'aria-label': '→', onclick: () => step(1) }, icon('right'));
  const viewer = h('div', { class: 'viewer', role: 'dialog', 'aria-modal': 'true' },
    h('div', { class: 'vbar' },
      vbtn('close', t('Schließen'), close),
      h('div', { class: 't' }, title, sub),
      vbtn('download', t('Herunterladen'), () => download(f, list[i])),
      vbtn('trash', t('Löschen'), async () => {
        if (await deleteItem(f, list[i])) {
          list.splice(i, 1);
          if (!list.length) return close();
          i = Math.min(i, list.length - 1);
          show();
        }
      })),
    prev, next, stage, counter);

  function show() {
    const it = list[i];
    title.textContent = it.name;
    sub.textContent = fmtDateTime(it.modified) + ' · ' + fmtSize(it.size);
    counter.textContent = `${i + 1} / ${list.length}`;
    prev.hidden = i === 0; next.hidden = i === list.length - 1;
    const src = fileUrl(f, it.path);
    if (it.kind === 'video') stage.replaceChildren(h('video', { src, controls: true, autoplay: true, playsinline: true }));
    else if (it.kind === 'text') {
      const pre = h('pre', {}, '…');
      stage.replaceChildren(pre);
      fetch(src, { credentials: 'same-origin' }).then(r => r.text()).then(tx => { pre.textContent = tx; }).catch(() => { pre.textContent = '—'; });
    } else {
      const img = h('img', { src, alt: it.name });
      img.addEventListener('error', () => stage.replaceChildren(h('div', { style: { textAlign: 'center' } },
        h('div', {}, EN ? 'This image format cannot be shown in this browser.' : 'Dieses Bildformat kann der Browser nicht anzeigen.'),
        h('button', { class: 'btn', style: { marginTop: '12px' }, onclick: () => download(f, it) }, icon('download'), t('Herunterladen')))));
      stage.replaceChildren(img);
      // Nachbarbilder vorladen
      [list[i + 1], list[i - 1]].forEach(n => { if (n && n.kind === 'image') new Image().src = fileUrl(f, n.path); });
    }
  }
  function step(d) { const n = i + d; if (n >= 0 && n < list.length) { i = n; show(); } }
  function close() { document.removeEventListener('keydown', onKey); viewer.remove(); }
  function onKey(e) {
    if (document.querySelector('.overlay')) return;
    if (e.key === 'Escape') close();
    else if (e.key === 'ArrowLeft') step(-1);
    else if (e.key === 'ArrowRight') step(1);
  }
  let tx = null;
  stage.addEventListener('touchstart', e => { tx = e.touches[0].clientX; }, { passive: true });
  stage.addEventListener('touchend', e => {
    if (tx == null) return;
    const dx = e.changedTouches[0].clientX - tx; tx = null;
    if (Math.abs(dx) > 50) step(dx < 0 ? 1 : -1);
  });
  document.addEventListener('keydown', onKey);
  document.body.append(viewer);
  show();
}

// ----- Netzlaufwerk
function mountDialog(f) {
  const url = (S.unlocked[f.id] && S.unlocked[f.id].davUrl) || `${location.origin}/dav/${enc(f.name)}/`;
  const copy = () => {
    (navigator.clipboard ? navigator.clipboard.writeText(url) : Promise.reject())
      .then(() => toast(t('Kopiert.')))
      .catch(() => { const r = document.createRange(); r.selectNodeContents(code); getSelection().removeAllRanges(); getSelection().addRange(r); });
  };
  const code = h('code', {}, url);
  dialog({
    title: t('Als Netzlaufwerk verbinden'), wide: true, cancel: t('Schließen'),
    sub: EN ? 'Mount the folder like a drive in Finder or Explorer. Files stay encrypted on the server.'
      : 'Den Ordner wie ein Laufwerk im Finder bzw. Explorer einbinden. Die Dateien bleiben auf dem Server verschlüsselt.',
    body: h('div', {},
      h('div', { class: 'copybox' }, code, h('button', { class: 'btn secondary', type: 'button', onclick: copy }, icon('copy'), t('Kopieren'))),
      h('h3', {}, 'macOS (Finder)'),
      h('ol', { class: 'steps' },
        h('li', {}, EN ? 'Once: download the ' : 'Einmalig: ', h('a', { href: '/ca.crt' }, t('Zertifikat herunterladen')),
          EN ? ', double-click it, open it in Keychain Access → “Trust” → “Always Trust”.' : ', doppelklicken, in der Schlüsselbundverwaltung öffnen → „Vertrauen“ → „Immer vertrauen“.'),
        h('li', {}, EN ? 'Finder → Go → Connect to Server (⌘K), paste the address, click “Connect”.' : 'Finder → Gehe zu → Mit Server verbinden (⌘K), Adresse einfügen, „Verbinden“.'),
        h('li', {}, EN ? 'Name: ' : 'Name: ', h('b', {}, f.name), EN ? ' · Password: the folder password.' : ' · Passwort: das Ordnerpasswort.')),
      h('h3', {}, 'Windows'),
      h('p', { class: 'muted', style: { margin: '6px 0 0' } }, EN
        ? 'In the NetworkFolderManager app: open the folder → “Connect as network drive” – it sets everything up automatically.'
        : 'In der NetworkFolderManager-App: Ordner öffnen → „Als Netzlaufwerk verbinden“ – richtet alles automatisch ein.')),
  });
}

// ----- Upload
const U = { queue: [], active: 0, panel: null, list: null, head: null, total: 0, done: 0, failed: 0, dirs: new Set() };
async function uploadFiles(f, dir, files) {
  files = [...files].filter(x => x && x.size !== undefined);
  if (!files.length) return;
  if (dir !== '/' && !U.dirs.has(f.id + dir)) {
    // Zielordner (z.B. /Fotos) bei Bedarf anlegen
    try { await api('POST', `/folders/${f.id}/mkdir`, { path: dir }); } catch (e) { if (e.code !== 'exists') { handleFolderError(f, e); return; } }
    U.dirs.add(f.id + dir);
  }
  ensurePanel();
  for (const file of files) {
    const bar = h('i'), barWrap = h('div', { class: 'bar' }, bar), state = h('span', {}, '0 %');
    U.list.append(h('div', { class: 'ui' }, h('div', { class: 'n' }, h('span', {}, file.name), state), barWrap));
    U.queue.push({ f, dir, file, bar, barWrap, state });
    U.total++;
  }
  updateHead();
  pump();
}
function ensurePanel() {
  if (U.panel && document.body.contains(U.panel)) return;
  U.total = U.done = U.failed = 0;
  U.head = h('span', {});
  U.list = h('div', { class: 'ul' });
  U.panel = h('div', { class: 'uploads' }, h('div', { class: 'uh' }, U.head,
    h('button', { class: 'btn icon ghost', 'aria-label': t('Schließen'), onclick: () => { U.panel.remove(); } }, icon('close'))), U.list);
  document.body.append(U.panel);
}
function updateHead() {
  U.head.textContent = U.done + U.failed < U.total
    ? `${t('Uploads')} ${U.done + U.failed + 1}/${U.total}`
    : `${t('Fertig')}: ${U.done}` + (U.failed ? ` · ${t('Fehler')}: ${U.failed}` : '');
}
function pump() {
  while (U.active < 3 && U.queue.length) {
    const job = U.queue.shift();
    U.active++;
    uploadOne(job).then(() => { U.done++; job.barWrap.classList.add('done'); job.state.textContent = '✓'; },
      e => { U.failed++; job.barWrap.classList.add('err'); job.bar.style.width = '100%'; job.state.textContent = e.message; })
      .finally(() => {
        U.active--; updateHead(); pump();
        if (!U.active && !U.queue.length) {
          S.refresh && S.refresh();
          const panel = U.panel;
          if (!U.failed) setTimeout(() => { if (U.panel === panel && !U.active) panel.remove(); }, 4000);
        }
      });
  }
}
function uploadOne({ f, dir, file, bar, state }) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', `/api/folders/${f.id}/file?path=${enc(joinPath(dir, file.name))}&mode=rename&mtime=${file.lastModified || 0}`);
    xhr.setRequestHeader('X-NFM', '1');
    xhr.upload.onprogress = e => {
      if (!e.lengthComputable) return;
      const p = Math.round(e.loaded * 100 / e.total);
      bar.style.width = p + '%'; state.textContent = p + ' %';
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) return resolve();
      let msg = 'HTTP ' + xhr.status;
      try { const e = JSON.parse(xhr.responseText); msg = errorText(e.code, e.error) || msg; } catch { }
      reject(new Error(msg));
    };
    xhr.onerror = () => reject(new Error(t('Server nicht erreichbar.')));
    xhr.send(file);
  });
}

// Drag & Drop (nur in der Ordneransicht)
let dragDepth = 0, dropzone = null;
const hasFiles = e => e.dataTransfer && [...e.dataTransfer.types].includes('Files');
window.addEventListener('dragenter', e => {
  if (!S.drop || !hasFiles(e)) return;
  e.preventDefault();
  if (dragDepth++ === 0) {
    dropzone = h('div', { class: 'dropzone' }, h('div', {}, icon('upload', 'lg'), t('Dateien hierher ziehen zum Hochladen')));
    document.body.append(dropzone);
  }
});
window.addEventListener('dragleave', e => { if (S.drop && hasFiles(e) && --dragDepth <= 0) { dragDepth = 0; dropzone && dropzone.remove(); } });
window.addEventListener('dragover', e => { if (S.drop && hasFiles(e)) e.preventDefault(); });
window.addEventListener('drop', e => {
  if (!S.drop || !hasFiles(e)) return;
  e.preventDefault();
  dragDepth = 0; dropzone && dropzone.remove();
  S.drop([...e.dataTransfer.files]);
});

// ---------------------------------------------------------------- Admin
async function renderAdmin(tab) {
  const main = h('main', { class: 'page' });
  mount(topbar(), main);
  const tabBtn = (id, label) => h('button', { class: tab === id ? 'on' : '', onclick: () => go('#/admin/' + id) }, label);
  const body = h('div', {}, spinner());
  main.replaceChildren(
    h('div', { class: 'page-head' }, h('div', {}, h('h1', {}, t('Admin')), h('div', { class: 'muted' }, S.info.name))),
    h('div', { class: 'tabs' }, tabBtn('folders', t('Ordner')), tabBtn('users', t('Benutzer')), tabBtn('server', t('Server'))),
    body);
  try {
    if (tab === 'users') await adminUsers(body);
    else if (tab === 'server') await adminServer(body);
    else await adminFolders(body);
  } catch (e) { body.replaceChildren(h('div', { class: 'empty' }, e.message)); }
}

async function adminFolders(body) {
  const [folders, users] = await Promise.all([api('GET', '/admin/folders'), api('GET', '/admin/users')]);
  const reload = () => adminFolders(body);
  body.replaceChildren(
    h('div', { class: 'notice info' }, icon('shield'), ' ', EN
      ? 'As admin you can create, reassign and delete folders – but not open them. Their contents are encrypted with the folder password.'
      : 'Als Admin kannst du Ordner anlegen, neu zuweisen und löschen – aber nicht öffnen. Die Inhalte sind mit dem Ordnerpasswort verschlüsselt.'),
    h('div', { class: 'toolbar' }, h('h2', {}, `${folders.length} ${t('Ordner')}`), h('div', { class: 'spacer' }),
      h('button', { class: 'btn', onclick: () => adminCreateFolder(users, reload) }, icon('folderPlus'), t('Ordner für Benutzer anlegen'))),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, [t('Name'), t('Besitzer'), t('Angelegt'), t('Speicher'), t('Status'), ''].map(x => h('th', {}, x)))),
      h('tbody', {}, folders.length ? folders.map(f => h('tr', {},
        h('td', {}, h('b', {}, f.name)),
        h('td', {}, f.ownerName),
        h('td', {}, fmtDate(f.createdAt), f.createdBy ? h('span', { class: 'small muted' }, ' · ' + f.createdBy) : null),
        h('td', {}, fmtSize(f.storageBytes)),
        h('td', {}, f.mustChangePassword ? h('span', { class: 'badge warn' }, t('Wartet auf erstes Öffnen')) : h('span', { class: 'badge gray' }, t('Aktiv'))),
        h('td', {}, h('div', { class: 'acts' },
          h('button', { class: 'btn ghost', onclick: () => adminChangeOwner(f, users, reload) }, icon('user'), t('Besitzer ändern')),
          h('button', {
            class: 'btn danger-ghost', onclick: async () => {
              if (!await confirmDialog(t('Ordner löschen'), EN ? `“${f.name}” of ${f.ownerName} and all its files will be deleted permanently.`
                : `„${f.name}“ von ${f.ownerName} und alle Dateien darin werden endgültig gelöscht.`)) return;
              try { await api('DELETE', `/admin/folders/${f.id}`); toast(EN ? 'Folder deleted.' : 'Ordner gelöscht.'); reload(); } catch (e) { toast(e.message, true); }
            },
          }, icon('trash'), t('Löschen')))))) : h('tr', {}, h('td', { colspan: 6, class: 'muted' }, '–'))))));
}

function userOptions(users, selected) {
  return users.map(u => ({ value: u.username, label: `${u.displayName} (@${u.username})` })).map(o => ({ ...o, selected: o.value === selected }));
}

function adminCreateFolder(users, reload) {
  const pw = randomPassword();
  dialog({
    title: t('Ordner für Benutzer anlegen'),
    sub: EN ? 'Give the user the initial password. When they open the folder for the first time, they must choose their own password – a new key is generated, so you cannot see the contents.'
      : 'Gib dem Benutzer das Startpasswort. Beim ersten Öffnen muss er ein eigenes Passwort festlegen – dabei wird ein neuer Schlüssel erzeugt, du kannst den Inhalt also nicht sehen.',
    ok: t('Anlegen'),
    fields: [
      { name: 'owner', label: t('Besitzer'), type: 'select', options: userOptions(users), value: users.find(u => u.id !== S.user.id)?.username },
      { name: 'name', label: t('Name') },
      { name: 'pw', label: t('Startpasswort'), value: pw, hint: EN ? 'Generated – you can change it. Write it down for the user.' : 'Zufällig erzeugt – änderbar. Für den Benutzer notieren.' },
    ],
    submit: async v => {
      checkNewPassword(v.pw, v.pw);
      await api('POST', '/admin/folders', { name: v.name, password: v.pw, owner: v.owner });
      toast(EN ? `Folder created. Initial password: ${v.pw}` : `Ordner angelegt. Startpasswort: ${v.pw}`);
      reload();
    },
  });
}

function adminChangeOwner(f, users, reload) {
  dialog({
    title: t('Besitzer ändern'), sub: f.name, ok: t('Speichern'),
    fields: [{ name: 'owner', label: t('Besitzer'), type: 'select', options: userOptions(users), value: users.find(u => u.id === f.ownerId)?.username }],
    submit: async v => { await api('POST', `/admin/folders/${f.id}/owner`, { username: v.owner }); reload(); },
  });
}

async function adminUsers(body) {
  const users = await api('GET', '/admin/users');
  const reload = () => adminUsers(body);
  body.replaceChildren(
    h('div', { class: 'toolbar' }, h('h2', {}, `${users.length} ${t('Benutzer')}`), h('div', { class: 'spacer' }),
      h('button', { class: 'btn', onclick: () => adminCreateUser(reload) }, icon('plus'), t('Benutzer anlegen'))),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, [t('Benutzer'), t('Rolle'), t('Ordner'), t('Letzte Anmeldung'), ''].map(x => h('th', {}, x)))),
      h('tbody', {}, users.map(u => h('tr', {},
        h('td', {}, h('b', {}, u.displayName), h('div', { class: 'small muted' }, '@' + u.username)),
        h('td', {}, u.isAdmin ? h('span', { class: 'badge' }, t('Admin')) : h('span', { class: 'badge gray' }, t('Mitglied')),
          u.mustChangePassword ? h('span', { class: 'badge warn', style: { marginLeft: '6px' } }, EN ? 'Initial password' : 'Startpasswort') : null),
        h('td', {}, u.folderCount),
        h('td', {}, fmtDateTime(u.lastLogin)),
        h('td', {}, h('div', { class: 'acts' },
          h('button', { class: 'btn ghost', onclick: () => adminResetPassword(u, reload) }, icon('key'), t('Passwort zurücksetzen')),
          u.id === S.user.id ? null : h('button', {
            class: 'btn ghost', onclick: async () => {
              try { await api('POST', `/admin/users/${u.id}/admin`, { isAdmin: !u.isAdmin }); reload(); } catch (e) { toast(e.message, true); }
            },
          }, icon('shield'), u.isAdmin ? t('Admin-Rechte entziehen') : t('Admin-Rechte geben')),
          u.id === S.user.id ? null : h('button', {
            class: 'btn danger-ghost', onclick: async () => {
              if (!await confirmDialog(t('Wirklich löschen?'), EN ? `Profile @${u.username} will be deleted. Their folders remain and can be reassigned.`
                : `Das Profil @${u.username} wird gelöscht. Seine Ordner bleiben erhalten und können neu zugewiesen werden.`)) return;
              try { await api('DELETE', `/admin/users/${u.id}`); reload(); } catch (e) { toast(e.message, true); }
            },
          }, icon('trash'), t('Löschen'))))))))));
}

function adminCreateUser(reload) {
  dialog({
    title: t('Benutzer anlegen'),
    sub: EN ? 'The user must change the initial password at the first sign-in.' : 'Der Benutzer muss das Startpasswort bei der ersten Anmeldung ändern.',
    ok: t('Anlegen'),
    fields: [
      { name: 'username', label: t('Benutzername') },
      { name: 'displayName', label: t('Anzeigename (optional)') },
      { name: 'pw', label: t('Startpasswort'), value: randomPassword() },
      { name: 'admin', label: EN ? 'Admin rights' : 'Admin-Rechte', type: 'checkbox', value: false },
    ],
    submit: async v => {
      checkNewPassword(v.pw, v.pw);
      await api('POST', '/admin/users', { username: v.username, displayName: v.displayName || null, password: v.pw, isAdmin: v.admin });
      toast(EN ? `User created. Initial password: ${v.pw}` : `Benutzer angelegt. Startpasswort: ${v.pw}`);
      reload();
    },
  });
}

function adminResetPassword(u, reload) {
  dialog({
    title: t('Passwort zurücksetzen'),
    sub: (EN ? `@${u.username} will be signed out everywhere and must choose a new password at the next sign-in. Folder passwords are not affected.`
      : `@${u.username} wird überall abgemeldet und muss bei der nächsten Anmeldung ein neues Passwort wählen. Ordnerpasswörter sind davon nicht betroffen.`),
    ok: t('Speichern'),
    fields: [{ name: 'pw', label: t('Startpasswort'), value: randomPassword() }],
    submit: async v => {
      checkNewPassword(v.pw, v.pw);
      await api('POST', `/admin/users/${u.id}/password`, { password: v.pw });
      toast(EN ? `New initial password: ${v.pw}` : `Neues Startpasswort: ${v.pw}`);
      reload();
    },
  });
}

async function adminServer(body) {
  const st = await api('GET', '/admin/settings');
  const name = h('input', { id: 'srvname', value: st.name });
  const reg = h('input', { type: 'checkbox', checked: st.allowRegistration });
  const err = h('div', { class: 'error' });
  body.replaceChildren(h('div', { class: 'card' },
    h('div', { class: 'field' }, h('label', { for: 'srvname' }, t('Servername')), name),
    h('label', { class: 'check' }, reg, t('Jeder darf sich ein Profil anlegen')),
    h('div', { class: 'small muted', style: { margin: '0 0 12px 28px' } }, EN ? 'Otherwise only the admin can create profiles.' : 'Sonst kann nur der Admin Profile anlegen.'),
    err,
    h('button', {
      class: 'btn', onclick: async () => {
        err.textContent = '';
        try {
          await api('PUT', '/admin/settings', { name: name.value, allowRegistration: reg.checked, autoUpdate: st.autoUpdate });
          S.info = await api('GET', '/info');
          toast(t('Gespeichert.'));
          render();
        } catch (e) { err.textContent = e.message; }
      },
    }, t('Speichern')),
    h('div', { class: 'kv', style: { marginTop: '22px' } },
      h('div', {}, t('Version')), h('div', {}, S.info.version),
      h('div', {}, t('Fingerabdruck')), h('div', { class: 'mono' }, fpShort(S.info.fingerprint)),
      h('div', {}, 'WebDAV'), h('div', { class: 'mono' }, location.origin + (EN ? '/dav/<folder>/' : '/dav/<Ordner>/')),
      h('div', {}, EN ? 'Certificate' : 'Zertifikat'), h('div', {}, h('a', { href: '/ca.crt' }, t('Zertifikat herunterladen'))))),
    updateCard(st));
}

/** Text zum Update-Zustand des Servers. */
function updateStateText(u) {
  switch (u.state) {
    case 'checking': return EN ? 'Checking for updates…' : 'Suche nach Updates…';
    case 'downloading': return EN ? 'Downloading update…' : 'Update wird heruntergeladen…';
    case 'restarting': return EN ? 'Update installed – the server is restarting…' : 'Update installiert – der Server startet neu…';
    case 'installed': return EN ? 'Update installed – please restart nfm-server.' : 'Update installiert – bitte nfm-server neu starten.';
    case 'noPermission': return EN ? 'No write permission – install the new version once manually (install script); after that updates are automatic.'
      : 'Keine Schreibrechte – neue Version einmal manuell installieren (Installationsskript); danach laufen Updates automatisch.';
    case 'unsupported': return EN ? 'This server build cannot update itself.' : 'Dieser Server-Build kann sich nicht selbst aktualisieren.';
    case 'error': return (EN ? 'Error: ' : 'Fehler: ') + (u.message || '');
    default:
      if (u.updateAvailable) return EN ? `Version ${u.latestVersion} is available.` : `Version ${u.latestVersion} ist verfügbar.`;
      return u.lastCheck ? (EN ? 'The server is up to date.' : 'Der Server ist aktuell.') : '';
  }
}

function updateCard(settings) {
  const auto = h('input', { type: 'checkbox', checked: settings.autoUpdate !== false });
  const info = h('div', { class: 'small muted', style: { margin: '8px 0 12px' } }, '…');
  const versions = h('div', { class: 'kv' });
  const checkBtn = h('button', { class: 'btn secondary' }, EN ? 'Check now' : 'Jetzt prüfen');
  const installBtn = h('button', { class: 'btn', hidden: true }, EN ? 'Update now' : 'Jetzt aktualisieren');

  const show = u => {
    versions.replaceChildren(
      h('div', {}, EN ? 'Installed' : 'Installiert'), h('div', {}, u.currentVersion),
      h('div', {}, EN ? 'Latest' : 'Neueste'), h('div', {}, u.latestVersion || '–'),
      h('div', {}, EN ? 'Last check' : 'Zuletzt geprüft'), h('div', {}, fmtDateTime(u.lastCheck)));
    info.textContent = updateStateText(u);
    installBtn.hidden = !(u.updateAvailable && u.supported && u.state !== 'restarting');
    checkBtn.disabled = installBtn.disabled = !u.supported || u.busy;
    auto.disabled = !u.supported;
  };

  auto.addEventListener('change', async () => {
    try {
      await api('PUT', '/admin/settings', { name: S.info.name, allowRegistration: settings.allowRegistration, autoUpdate: auto.checked });
      settings.autoUpdate = auto.checked;
      toast(t('Gespeichert.'));
    } catch (e) { toast(e.message, true); auto.checked = !auto.checked; }
  });
  checkBtn.addEventListener('click', async () => {
    checkBtn.disabled = true; info.textContent = updateStateText({ state: 'checking' });
    try { show(await api('POST', '/admin/update/check')); } catch (e) { toast(e.message, true); checkBtn.disabled = false; }
  });
  installBtn.addEventListener('click', async () => {
    installBtn.disabled = true; info.textContent = updateStateText({ state: 'downloading' });
    try {
      const u = await api('POST', '/admin/update/install');
      show(u);
      if (u.state === 'restarting') waitForRestart(u.latestVersion);
    } catch (e) { toast(e.message, true); installBtn.disabled = false; }
  });

  api('GET', '/admin/update').then(show).catch(() => { info.textContent = ''; });

  return h('div', { class: 'card', style: { marginTop: '16px' } },
    h('h3', {}, EN ? 'Server updates' : 'Server-Updates'),
    h('label', { class: 'check', style: { marginTop: '10px' } }, auto, EN ? 'Install updates automatically' : 'Updates automatisch installieren'),
    h('div', { class: 'small muted', style: { margin: '0 0 8px 28px' } }, EN
      ? 'Checks GitHub every 6 hours. Accounts, folders and data are kept; the server restarts briefly.'
      : 'Prüft alle 6 Stunden auf GitHub. Konten, Ordner und Daten bleiben erhalten; der Server startet kurz neu.'),
    versions, info,
    h('div', { style: { display: 'flex', gap: '8px' } }, checkBtn, installBtn));
}

/** Nach einem Update warten, bis der Server mit der neuen Version wieder erreichbar ist. */
async function waitForRestart(version) {
  for (let i = 0; i < 60; i++) {
    await new Promise(r => setTimeout(r, 2000));
    try {
      const info = await (await fetch('/api/info', { headers: { 'X-NFM': '1' } })).json();
      if (info.version === version) {
        S.info = info;
        toast(EN ? `Server updated to ${version}.` : `Server auf ${version} aktualisiert.`);
        render();
        return;
      }
    } catch { /* startet noch */ }
  }
  toast(EN ? 'The server did not come back yet – please reload the page later.' : 'Der Server ist noch nicht wieder erreichbar – bitte später neu laden.', true);
}

// ---------------------------------------------------------------- Start
async function boot() {
  try { S.info = await api('GET', '/info'); }
  catch (e) { mount(h('div', { class: 'empty' }, e.message)); return; }
  document.title = `${S.info.name} · NetworkFolderManager`;
  try { S.user = await api('GET', '/auth/me'); await applyProfileLanguage(); } catch { S.user = null; }
  window.addEventListener('hashchange', render);
  render();
}
boot();
})();
