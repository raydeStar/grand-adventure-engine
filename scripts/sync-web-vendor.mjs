import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const projectRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webRoot = resolve(projectRoot, 'src', 'GAE.Dashboard.Api', 'wwwroot');

const assets = [
  ['node_modules/@microsoft/signalr/dist/browser/signalr.min.js', 'vendor/signalr-10.0.11.min.js'],
  ['node_modules/rot-js/dist/rot.min.js', 'vendor/rot-2.2.1.min.js'],
  ['node_modules/@fontsource/ibm-plex-mono/files/ibm-plex-mono-latin-400-normal.woff2', 'fonts/ibm-plex-mono-latin-400-normal.woff2'],
  ['node_modules/@fontsource/ibm-plex-mono/files/ibm-plex-mono-latin-700-normal.woff2', 'fonts/ibm-plex-mono-latin-700-normal.woff2'],
  ['scripts/vendor-licenses/signalr-MIT.txt', 'vendor/licenses/signalr-MIT.txt'],
  ['node_modules/rot-js/license.txt', 'vendor/licenses/rot-js.txt'],
  ['node_modules/@fontsource/ibm-plex-mono/LICENSE', 'vendor/licenses/ibm-plex-mono.txt']
];

for (const [sourcePath, destinationPath] of assets) {
  const source = resolve(projectRoot, sourcePath);
  const destination = resolve(webRoot, destinationPath);
  await mkdir(dirname(destination), { recursive: true });
  await copyFile(source, destination);

  if (destinationPath.endsWith('.txt')) {
    const copiedText = await readFile(destination, 'utf8');
    await writeFile(destination, copiedText.replace(/[\t ]+$/gm, ''), 'utf8');
  }
}

console.info(`Sir Thaddeus has bottled ${assets.length} web assets for offline use. No CDN seance required.`);
