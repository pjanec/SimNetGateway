#!/usr/bin/env node
// Parse every ```mermaid block in the given markdown file(s).
//
// Why this exists: a Mermaid syntax error renders as an ERROR BOX on the GitHub blob
// page -- worse than no diagram, and invisible to anyone who does not open the page.
// Real case (2026-08-20): `Default` is a reserved word in stateDiagram-v2, and only a
// parser caught it. Eyeballing does not.
//
//   node scripts/mermaid-check.mjs docs/blueprints/DESIGN_Foo.md [more.md ...]
//
// Requires mermaid + jsdom. If they are not installed this exits 0 with a SKIP notice,
// so it never blocks a machine that cannot run it -- run it where you can before pushing.
import fs from 'node:fs';

import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

// Resolve from this repo first, then from MERMAID_PREFIX (a scratch dir holding
// `npm install mermaid@11 jsdom`), so no dependency has to be added to the repo.
async function load(name) {
  try { return await import(name); } catch { /* fall through */ }
  const prefix = process.env.MERMAID_PREFIX;
  if (!prefix) throw new Error(`cannot resolve ${name}`);
  const req = createRequire(path.join(path.resolve(prefix), 'noop.js'));
  return await import(pathToFileURL(req.resolve(name)).href);
}

let mermaid;
try {
  const { JSDOM } = await load('jsdom');
  const dom = new JSDOM('<!doctype html><html><body></body></html>');
  globalThis.window = dom.window;
  globalThis.document = dom.window.document; for (const k of ["Element","HTMLElement","SVGElement","Node","DocumentFragment","navigator"]) { try { globalThis[k] = dom.window[k]; } catch {} }
  mermaid = (await load('mermaid')).default;
} catch {
  console.log('SKIP: mermaid/jsdom not resolvable here.');
  console.log('      mkdir -p /tmp/mm && cd /tmp/mm && npm install mermaid@11 jsdom');
  console.log('      MERMAID_PREFIX=/tmp/mm node scripts/mermaid-check.mjs <file.md>');
  process.exit(0);
}

const files = process.argv.slice(2);
if (files.length === 0) {
  console.error('usage: node scripts/mermaid-check.mjs <file.md> [...]');
  process.exit(2);
}

let bad = 0, total = 0;
for (const file of files) {
  const src = fs.readFileSync(file, 'utf8');
  const blocks = [...src.matchAll(/```mermaid\n([\s\S]*?)```/g)].map(m => m[1]);
  if (blocks.length === 0) { console.log(`${file}: no mermaid blocks`); continue; }
  for (const [i, b] of blocks.entries()) {
    total++;
    const head = b.trim().split('\n')[0];
    try {
      await mermaid.parse(b);
      console.log(`${file} #${i + 1} ${head}  OK`);
    } catch (e) {
      bad++;
      console.log(`${file} #${i + 1} ${head}  FAIL:\n${String(e.message || e).slice(0, 600)}\n`);
    }
  }
}

console.log(bad ? `\n${bad} of ${total} block(s) FAILED to parse.`
                : `\nAll ${total} mermaid block(s) parse.`);
process.exit(bad ? 1 : 0);
