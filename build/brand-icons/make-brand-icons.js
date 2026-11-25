// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1
//
// Builds src/CtrlZ.App/Resources/brand-icons.json: brand logos for apps in the built-in catalog,
// taken from Simple Icons (CC0 1.0, https://simpleicons.org). Reads the package's data files
// only; none of its code runs.
//
//   npm pack simple-icons@<version>   (in an empty folder), then extract it, then:
//   node build/brand-icons/make-brand-icons.js <extracted>/package
//
// Each entry: t = brand title, c = brand colour (hex), p = path data for WPF (24x24 box,
// non-zero fill), w = winget ids, n = normalised app names that use it.
'use strict';
const fs = require('fs');
const path = require('path');

const pkg = process.argv[2];
if (!pkg) throw new Error('usage: node make-brand-icons.js <simple-icons package folder>');
const root = path.resolve(__dirname, '..', '..');
const catalogPath = path.join(root, 'src', 'CtrlZ.Core', 'Resources', 'winutil-applications.json');
const outPath = path.join(root, 'src', 'CtrlZ.App', 'Resources', 'brand-icons.json');

const data = JSON.parse(fs.readFileSync(path.join(pkg, 'data', 'simple-icons.json'), 'utf8'));
const icons = Array.isArray(data) ? data : data.icons;
const version = JSON.parse(fs.readFileSync(path.join(pkg, 'package.json'), 'utf8')).version;
const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8'));

/** Same rule as BrandIcons.Normalize in the app: lower case letters, digits and '+'. */
const norm = (s) => (s || '').normalize('NFKD').toLowerCase().replace(/[^a-z0-9+]/g, '');

// Catalog ids whose brand has another name in Simple Icons (checked by hand).
const bySlug = {
  '1password': '1password', '7zip': '7zip', audacity: 'audacity', autohotkey: 'autohotkey', bitwarden: 'bitwarden',
  blender: 'blender', brave: 'brave', chrome: 'googlechrome', discord: 'discord', dropbox: 'dropbox',
  epicgames: 'epicgames', firefox: 'firefoxbrowser', firefoxesr: 'firefoxbrowser', foobar: 'foobar2000', gimp: 'gimp',
  git: 'git', githubdesktop: 'github', githubcli: 'github', gog: 'gogdotcom', golang: 'go', googledrive: 'googledrive',
  jetbrains: 'jetbrains', joplin: 'joplin', keepassxc: 'keepassxc', libreoffice: 'libreoffice', librewolf: 'librewolf',
  mullvadbrowser: 'mullvad', neovim: 'neovim', nextclouddesktop: 'nextcloud', nodejs: 'nodedotjs', notepadplus: 'notepadplusplus',
  obs: 'obsstudio', obsidian: 'obsidian', postman: 'postman', python3: 'python', qbittorrent: 'qbittorrent', rustlang: 'rust',
  sharex: 'sharex', signal: 'signal', steam: 'steam', sublimetext: 'sublimetext', syncthing: 'syncthing', telegram: 'telegram',
  thunderbird: 'thunderbird', tor: 'torbrowser', ubisoft: 'ubisoft', unity: 'unity', vivaldi: 'vivaldi', vlc: 'vlcmediaplayer',
  vscodium: 'vscodium', whatsapp: 'whatsapp', wireshark: 'wireshark', zoom: 'zoom', zotero: 'zotero',
  jellyfinmediaplayer: 'jellyfin', jellyfinserver: 'jellyfin', plex: 'plex', OpenVPN: 'openvpn', teamspeak3: 'teamspeak',
  teamspeak6: 'teamspeak', Zed: 'zedindustries', geforcenow: 'nvidia', okular: 'kde', enteauth: 'ente',
  msiafterburner: 'msi', CloudflareWARP: 'cloudflare',
};
// Name matches that point at a different brand with the same name.
const never = new Set(['files', 'helium', 'flux', 'nilesoftShell']);
const suffixes = /\b(desktop|app|client|browser|player|studio|for windows|portable|launcher|community|edition|stable|beta|lts)\b/gi;

const byName = new Map();
for (const icon of icons) {
  for (const k of [icon.title, icon.slug, ...((icon.aliases && icon.aliases.aka) || [])]) {
    if (!byName.has(norm(k))) byName.set(norm(k), icon);
  }
}

/** Re-writes SVG path data with explicit separators (arc flags may be packed, e.g. "a1 1 0 011 1"). */
function toWpfPath(d) {
  const counts = {m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, a: 7, z: 0};
  const number = /^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?/;
  const parts = [];
  let cmd = null, arg = 0, i = 0;
  while (i < d.length) {
    const ch = d[i];
    if (/[\s,]/.test(ch)) { i++; continue; }
    if (/[a-zA-Z]/.test(ch)) {
      if (!(ch.toLowerCase() in counts)) throw new Error(`unknown command ${ch}`);
      cmd = ch; parts.push(ch); arg = 0; i++; continue;
    }
    if (cmd === null) throw new Error('number before command');
    if (cmd.toLowerCase() === 'a' && (arg % 7 === 3 || arg % 7 === 4)) {
      if (ch !== '0' && ch !== '1') throw new Error(`bad arc flag ${ch}`);
      parts.push(ch); i++; arg++; continue;
    }
    const m = d.slice(i).match(number);
    if (!m) throw new Error(`bad number at ${d.slice(i, i + 10)}`);
    parts.push(m[0]); i += m[0].length; arg++;
  }
  return 'F1 ' + parts.join(' ');
}

const bySlugOut = new Map();
const misses = [];
for (const [id, app] of Object.entries(catalog)) {
  const name = app.content || id;
  let icon = null;
  if (bySlug[id]) icon = icons.find((x) => x.slug === bySlug[id]) || null;
  if (!icon && !never.has(id)) {
    const keys = [norm(name), norm(name.replace(suffixes, ''))];
    if (app.winget) {
      const [publisher, product] = app.winget.split('.');
      keys.push(norm(product), norm(publisher + product));
    }
    for (const k of keys) { if (k && byName.has(k)) { icon = byName.get(k); break; } }
  }
  if (!icon) { misses.push(name); continue; }
  const svg = fs.readFileSync(path.join(pkg, 'icons', `${icon.slug}.svg`), 'utf8');
  const d = (svg.match(/<path d="([^"]+)"/) || [])[1];
  if (!d) { misses.push(name); continue; }
  let entry = bySlugOut.get(icon.slug);
  if (!entry) {
    entry = {t: icon.title, c: icon.hex, p: toWpfPath(d), w: [], n: [norm(icon.title)]};
    bySlugOut.set(icon.slug, entry);
  }
  if (app.winget && !entry.w.includes(app.winget.toLowerCase())) entry.w.push(app.winget.toLowerCase());
  for (const n of [norm(name), norm(name.replace(/\s*\(.*\)\s*/g, ''))]) if (n && !entry.n.includes(n)) entry.n.push(n);
}

const result = {source: `Simple Icons ${version} (CC0 1.0)`, icons: [...bySlugOut.values()]};
fs.writeFileSync(outPath, JSON.stringify(result, null, 0) + '\n');
console.log(`${result.icons.length} logos for ${Object.keys(catalog).length - misses.length} of ${Object.keys(catalog).length} catalog apps -> ${path.relative(root, outPath)}`);
