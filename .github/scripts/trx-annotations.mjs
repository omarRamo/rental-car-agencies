#!/usr/bin/env node
// Turns failed tests of TRX result files into GitHub Actions annotations and a job summary table.
// Usage: node .github/scripts/trx-annotations.mjs <results directory>
// Annotations are visible on the run page (and through the public checks API) without downloading the artifacts.
import { appendFileSync, readdirSync, readFileSync, statSync } from "node:fs";
import { basename, join } from "node:path";

const MAX_ANNOTATIONS = 40; // GitHub keeps at most 50 annotations per step
const root = process.argv[2] ?? ".";

const decode = (text) =>
  text
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#x([0-9a-f]+);/gi, (_, hex) => String.fromCodePoint(parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, dec) => String.fromCodePoint(parseInt(dec, 10)))
    .replace(/&amp;/g, "&");
const escape = (value) => value.replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");

function* trxFiles(directory) {
  let entries = [];
  try {
    entries = readdirSync(directory);
  } catch {
    return;
  }
  for (const entry of entries) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) yield* trxFiles(path);
    else if (entry.endsWith(".trx")) yield path;
  }
}

const failures = [];
for (const path of trxFiles(root)) {
  const xml = readFileSync(path, "utf8");
  for (const match of xml.matchAll(/<UnitTestResult\b([^>]*)>([\s\S]*?)<\/UnitTestResult>/g)) {
    const attributes = match[1];
    if (!/\boutcome="Failed"/.test(attributes)) continue;
    const name = decode(/\btestName="([^"]*)"/.exec(attributes)?.[1] ?? "?");
    const message = decode(/<Message>([\s\S]*?)<\/Message>/.exec(match[2])?.[1] ?? "").trim();
    const stack = decode(/<StackTrace>([\s\S]*?)<\/StackTrace>/.exec(match[2])?.[1] ?? "").trim();
    failures.push({ file: basename(path), name, message, stack });
  }
}

for (const { name, message, stack } of failures.slice(0, MAX_ANNOTATIONS)) {
  const detail = message.slice(0, 1500) + (stack ? "\n" + stack.split(/\r?\n/).slice(0, 6).join("\n") : "");
  console.log(`::error title=${escapeProperty(name.slice(0, 200))}::${escape(detail)}`);
}

if (process.env.GITHUB_STEP_SUMMARY && failures.length > 0) {
  let table = `### ${failures.length} failed test(s)\n\n| Results file | Test | Message |\n|---|---|---|\n`;
  for (const { file, name, message } of failures) {
    const firstLine = (message.split(/\r?\n/)[0] ?? "").replace(/\|/g, "\|").slice(0, 300);
    table += `| ${file} | \`${name}\` | ${firstLine} |\n`;
  }
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, table);
}
console.log(`${failures.length} failed test(s) found in ${root}.`);
