#!/usr/bin/env node
// Renders the logo of MatCloud (branding/matcloud.svg, branding/matcloud-maskable.svg) into the files of the web app (src/MatCloud/wwwroot/img):
// logo.svg and favicon.svg (copies), icon-192.png, icon-512.png, icon-maskable-512.png, apple-touch-icon.png (180) and favicon.ico (16, 32, 48 as PNG),
// and branding/png/. It draws with the installed Microsoft Edge (headless) through playwright-core of tests/e2e:
//   cd tests/e2e && npm install && cd ../.. && node tools/render-icons.js
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require(path.join(__dirname, '..', 'tests', 'e2e', 'node_modules', 'playwright-core'));

const root = path.join(__dirname, '..');
const branding = path.join(root, 'branding');
const img = path.join(root, 'src', 'MatCloud', 'wwwroot', 'img');
const png = path.join(branding, 'png');

async function render(page, svgFile, size) {
  const svg = fs.readFileSync(path.join(branding, svgFile), 'utf8');
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${svg.replace('<svg ', `<svg style="display:block;width:${size}px;height:${size}px" `)}</body></html>`);
  return page.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
}

/** An ICO file whose images are PNGs (Windows Vista and later, every browser). */
function ico(images) {
  const header = Buffer.alloc(6 + images.length * 16);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, data }, i) => {
    const entry = 6 + i * 16;
    header.writeUInt8(size >= 256 ? 0 : size, entry);
    header.writeUInt8(size >= 256 ? 0 : size, entry + 1);
    header.writeUInt8(0, entry + 2);
    header.writeUInt8(0, entry + 3);
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(data.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += data.length;
  });
  return Buffer.concat([header, ...images.map((i) => i.data)]);
}

(async () => {
  fs.mkdirSync(png, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage();
  try {
    for (const size of [16, 32, 48, 64, 128, 180, 192, 256, 512]) {
      fs.writeFileSync(path.join(png, `matcloud-${size}.png`), await render(page, 'matcloud.svg', size));
    }
    fs.writeFileSync(path.join(png, 'matcloud-maskable-512.png'), await render(page, 'matcloud-maskable.svg', 512));

    fs.copyFileSync(path.join(branding, 'matcloud.svg'), path.join(img, 'logo.svg'));
    fs.copyFileSync(path.join(branding, 'matcloud.svg'), path.join(img, 'favicon.svg'));
    fs.copyFileSync(path.join(png, 'matcloud-192.png'), path.join(img, 'icon-192.png'));
    fs.copyFileSync(path.join(png, 'matcloud-512.png'), path.join(img, 'icon-512.png'));
    fs.copyFileSync(path.join(png, 'matcloud-maskable-512.png'), path.join(img, 'icon-maskable-512.png'));
    fs.copyFileSync(path.join(png, 'matcloud-180.png'), path.join(img, 'apple-touch-icon.png'));
    fs.writeFileSync(path.join(img, 'favicon.ico'), ico([16, 32, 48].map((size) => ({ size, data: fs.readFileSync(path.join(png, `matcloud-${size}.png`)) }))));
    console.log('Icons written to ' + img + ' and ' + png);
  } finally {
    await browser.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
