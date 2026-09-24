// 010-P5a build assertion: the Development sign-in picker must never ship in a production bundle.
// SignIn.tsx loads it behind `import.meta.env.DEV`, which Vite folds to `false` in a production
// build; this proves the fold happened by grepping every emitted file for the picker's marker.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const MARKER = 'aperture-dev-signin-picker';
const dist = fileURLToPath(new URL('../dist/', import.meta.url));

function files(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

const offenders = files(dist).filter(
  (path) => /DevSignInPicker/i.test(path) || readFileSync(path, 'utf8').includes(MARKER),
);

if (offenders.length > 0) {
  console.error(`Production bundle contains the dev sign-in picker:\n  ${offenders.join('\n  ')}`);
  process.exit(1);
}
console.log('assert-no-dev-picker: no picker code in dist/');
