import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { chromium } from 'playwright-core';

const root = process.cwd();
const site = path.join(root, 'site');
const output = path.join(root, 'site-evidence');
fs.mkdirSync(path.join(output, 'screenshots'), { recursive: true });

const pages = [
  ['list', path.join(site, 'index.html')],
  ['detail-waterfall', path.join(site, 'p', 'demo-1', 'index.html')],
  ['detail-final-week', path.join(site, 'p', 'demo-2', 'index.html')],
  ['detail-agile', path.join(site, 'p', 'demo-3', 'index.html')],
  ['detail-zero-closed', path.join(site, 'p', 'demo-4', 'index.html')],
  ['detail-just-started', path.join(site, 'p', 'demo-5', 'index.html')],
];

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
const page = await context.newPage();
const metrics = {};
for (const [name, file] of pages) {
  await page.goto(pathToFileURL(file).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(150);
  metrics[name] = await page.evaluate(() => ({
    scrollHeight: document.documentElement.scrollHeight,
    clientHeight: document.documentElement.clientHeight,
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
    innerHeight: window.innerHeight,
    innerWidth: window.innerWidth,
    scrolls:
      document.documentElement.scrollHeight > document.documentElement.clientHeight ||
      document.documentElement.scrollWidth > document.documentElement.clientWidth,
  }));
  await page.screenshot({ path: path.join(output, 'screenshots', name + '.png'), fullPage: false });
  console.log(name, JSON.stringify(metrics[name]));
}
fs.writeFileSync(path.join(output, 'viewport.json'), JSON.stringify(metrics, null, 2));
const scrolling = Object.entries(metrics).filter(([, value]) => value.scrolls);
if (scrolling.length > 0) {
  console.error('PAGE SCROLLS AT 1920x1080:', scrolling.map(([name]) => name));
  process.exitCode = 1;
}
await browser.close();
