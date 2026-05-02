const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

let gitHash = 'unknown';
try {
  gitHash = execSync('git rev-parse --short HEAD', { encoding: 'utf-8' }).trim();
} catch {}

const outPath = path.join(__dirname, '..', 'dist', 'build-info.json');
fs.writeFileSync(outPath, JSON.stringify({ gitHash, buildTime: new Date().toISOString() }, null, 2) + '\n');
