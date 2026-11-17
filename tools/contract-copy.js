#!/usr/bin/env node
// Copies the contract of MatCloud (src/MatCloud.Contract: routes, DTOs, limits, JSON options and the HTTP client MatCloudClient) into a Thix app
// (an app copies, nothing is loaded from elsewhere, like MatUi, MatPlatform and MatDesktop):
//   node tools/contract-copy.js <folder of the app's project that compiles it> [--dry]
// It writes every .cs file of src/MatCloud.Contract (with its sub folders, e.g. Clip/) into <folder>/MatCloud/ with the first line
//   // MatCloud.Contract <version> – a copy from MatCloud, do not edit here
// and matcloud.version next to them (the version, then the SHA-256 of each file as copied, like sha256sum), and removes the files of an older copy
// that the contract no longer has. The app's tests hold the copy to matcloud.version (see docs/CONTRACT.md).
// The version is MatCloudContractVersion in src/MatCloud.Contract/MatCloud.Contract.csproj.
// Exit code: 0 copied, 64 wrong arguments, 66 the folder is not a project folder.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const args = process.argv.slice(2);
const dry = args.includes('--dry');
const target = args.find(a => !a.startsWith('--'));
if (!target) { console.error('usage: node tools/contract-copy.js <folder of the app\'s project> [--dry]'); process.exit(64); }

const source = path.join(__dirname, '..', 'src', 'MatCloud.Contract');
const project = fs.readFileSync(path.join(source, 'MatCloud.Contract.csproj'), 'utf8');
const version = (/<MatCloudContractVersion>([^<]+)<\/MatCloudContractVersion>/.exec(project) || [])[1];
if (!version) { console.error('MatCloudContractVersion is missing in src/MatCloud.Contract/MatCloud.Contract.csproj'); process.exit(70); }

const folder = path.resolve(target);
const projects = fs.existsSync(folder) ? fs.readdirSync(folder).filter(f => f.endsWith('.csproj')) : [];
if (projects.length === 0) { console.error(`${folder} is not the folder of a .NET project (no .csproj in it).`); process.exit(66); }

const MARK = '// MatCloud.Contract ';
const DASH = '–';
const LF = String.fromCharCode(10);
const header = `${MARK}${version} ${DASH} a copy from MatCloud, do not edit here`;
const destination = path.join(folder, 'MatCloud');
const sha = text => crypto.createHash('sha256').update(text, 'utf8').digest('hex');

// Every .cs file below the contract (forward slashes, sorted), without bin/ and obj/.
function list(dir, prefix) {
  let result = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj') { continue; }
      result = result.concat(list(path.join(dir, entry.name), prefix + entry.name + '/'));
    } else if (entry.name.endsWith('.cs')) {
      result.push(prefix + entry.name);
    }
  }
  return result;
}

const files = list(source, '').sort();
const manifest = [`MatCloud.Contract ${version} ${DASH} a copy from MatCloud src/MatCloud.Contract (tools/contract-copy.js)`];
for (const file of files) {
  const text = header + LF + fs.readFileSync(path.join(source, file), 'utf8').split(String.fromCharCode(13) + LF).join(LF);
  manifest.push(`${sha(text)}  ${file}`);
  if (!dry) {
    fs.mkdirSync(path.dirname(path.join(destination, file)), { recursive: true });
    fs.writeFileSync(path.join(destination, file), text);
  }
  console.log(`${dry ? 'would copy' : 'copied'}  MatCloud/${file}`);
}

// A file of an older copy (it has the mark) that the contract does not have any more.
if (fs.existsSync(destination)) {
  for (const file of list(destination, '').filter(f => !files.includes(f))) {
    const first = fs.readFileSync(path.join(destination, file), 'utf8').split(LF, 1)[0];
    if (first.startsWith(MARK)) {
      if (!dry) { fs.unlinkSync(path.join(destination, file)); }
      console.log(`${dry ? 'would remove' : 'removed'} MatCloud/${file} (no longer in the contract)`);
    } else {
      console.warn(`kept    MatCloud/${file}: not a file of the contract (move it out of the folder)`);
    }
  }
}

if (!dry) { fs.writeFileSync(path.join(destination, 'matcloud.version'), manifest.join(LF) + LF); }
console.log(`MatCloud.Contract ${version} ${dry ? 'would be' : 'is'} in ${destination}`);
