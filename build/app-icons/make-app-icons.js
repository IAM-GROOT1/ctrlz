// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1
//
// Builds src/CtrlZ.App/Resources/app-icons.json: a full-colour 64x64 icon for apps in the built-in
// catalog, so an app that is not installed yet still shows its real icon. Nothing is downloaded at
// run time.
//
// The icons were collected once, by hand-checked downloads from each app's own website or source
// repository, Wikimedia Commons, and the icon list UniGetUI keeps (sources.json says where each one
// came from). Each was trimmed of empty borders and scaled to 64x64 PNG; dark marks got a plate.
//
//   node build/app-icons/make-app-icons.js <folder of <catalog id>.png files>
//
// Each entry: w = winget ids (lower case), n = normalised app names, d = PNG as base64.
'use strict';
const fs = require('fs');
const path = require('path');

const folder = process.argv[2];
if (!folder) throw new Error('usage: node make-app-icons.js <png folder>');
const root = path.resolve(__dirname, '..', '..');
const catalog = JSON.parse(fs.readFileSync(path.join(root, 'src', 'CtrlZ.Core', 'Resources', 'winutil-applications.json'), 'utf8'));
const outPath = path.join(root, 'src', 'CtrlZ.App', 'Resources', 'app-icons.json');

/** Same rule as BrandIcons.Normalize in the app: lower case letters, digits and '+'. */
const norm = (s) => (s || '').normalize('NFKD').toLowerCase().replace(/[^a-z0-9+]/g, '');
// Names too generic to match an installed program by name alone (the winget id still matches).
const idOnly = new Set(['files', 'helium', 'flux', 'nilesoftShell', 'uv', 'zed', 'go', 'git', 'rust', 'lua', 'yarn']);

const icons = [];
const missing = [];
for (const [id, app] of Object.entries(catalog)) {
  const file = path.join(folder, `${id}.png`);
  if (!fs.existsSync(file)) { missing.push(app.content || id); continue; }
  const png = fs.readFileSync(file);
  if (png.readUInt32BE(16) !== 64 || png.readUInt32BE(20) !== 64) throw new Error(`${id}.png is not 64x64`);
  const name = app.content || id;
  const names = idOnly.has(id) ? [] : [...new Set([norm(name), norm(name.replace(/\s*\(.*\)\s*/g, ' '))])].filter((n) => n.length > 2);
  icons.push({w: app.winget ? [app.winget.toLowerCase()] : [], n: names, d: png.toString('base64')});
}

fs.writeFileSync(outPath, JSON.stringify({icons}) + '\n');
console.log(`${icons.length} of ${Object.keys(catalog).length} catalog apps -> ${path.relative(root, outPath)}`);
console.log('no icon: ' + missing.join('; '));
