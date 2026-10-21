#!/usr/bin/env node
// Turns test results (TRX files) into GitHub Actions annotations and a job summary table: failed tests, test run
// errors (for example a crashed or aborted test host) and the run summary.
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
const escapeProperty = (value) => escape(value).replace(/:/g, "%3A").replace(/,/g, "%2C");

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
const runErrors = [];
const summaries = [];
let fileCount = 0;
for (const path of trxFiles(root)) {
  fileCount++;
  const xml = readFileSync(path, "utf8");
  // A result without output is self-closing (<UnitTestResult ... />): both forms are matched, otherwise a passed result
  // would swallow the failed result that follows it.
  for (const match of xml.matchAll(/<UnitTestResult\b((?:[^>"]|"[^"]*")*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const attributes = match[1];
    if (!/\boutcome="Failed"/.test(attributes)) continue;
    const name = decode(/\btestName="([^"]*)"/.exec(attributes)?.[1] ?? "?");
    const body = match[2] ?? "";
    const message = decode(/<Message>([\s\S]*?)<\/Message>/.exec(body)?.[1] ?? "").trim();
    const stack = decode(/<StackTrace>([\s\S]*?)<\/StackTrace>/.exec(body)?.[1] ?? "").trim();
    failures.push({ file: basename(path), name, message, stack });
  }

  const summary = /<ResultSummary\b([^>]*)>([\s\S]*?)<\/ResultSummary>/.exec(xml);
  if (summary) {
    const outcome = /\boutcome="([^"]*)"/.exec(summary[1])?.[1] ?? "?";
    const counters = /<Counters\b([^>]*)\/?>/.exec(summary[2])?.[1] ?? "";
    const count = (name) => new RegExp(`\\b${name}="(\\d+)"`).exec(counters)?.[1] ?? "?";
    summaries.push(
      `${basename(path)}: ${outcome} (total ${count("total")}, executed ${count("executed")}, passed ${count("passed")}, ` +
        `failed ${count("failed")}, error ${count("error")}, timeout ${count("timeout")}, aborted ${count("aborted")})`,
    );
    for (const info of summary[2].matchAll(/<RunInfo\b([^>]*)>([\s\S]*?)<\/RunInfo>/g)) {
      if (!/\boutcome="(Error|Failed|Aborted|Timeout)"/.test(info[1])) continue;
      const text = decode(/<Text>([\s\S]*?)<\/Text>/.exec(info[2])?.[1] ?? "").trim();
      if (text) runErrors.push({ file: basename(path), text });
    }
  }
}

if (fileCount === 0) {
  console.log(`::error title=No test results::No TRX file under ${escape(root)}: the test run produced no results (crash or setup failure).`);
}
for (const line of summaries) console.log(`::notice title=Test run summary::${escape(line)}`);
for (const { file, text } of runErrors.slice(0, 10)) {
  console.log(`::error title=${escapeProperty(`Test run error (${file})`)}::${escape(text.slice(0, 3000))}`);
}
for (const { name, message, stack } of failures.slice(0, MAX_ANNOTATIONS)) {
  const detail = message.slice(0, 1500) + (stack ? "\n" + stack.split(/\r?\n/).slice(0, 6).join("\n") : "");
  console.log(`::error title=${escapeProperty(name.slice(0, 200))}::${escape(detail)}`);
}

if (process.env.GITHUB_STEP_SUMMARY && (failures.length > 0 || runErrors.length > 0 || fileCount === 0)) {
  let table = `### Test results\n\n${summaries.map((s) => `- ${s}`).join("\n")}\n\n`;
  for (const { file, text } of runErrors) table += `**Run error (${file})**\n\n\`\`\`\n${text.slice(0, 3000)}\n\`\`\`\n\n`;
  if (failures.length > 0) {
    table += `| Results file | Test | Message |\n|---|---|---|\n`;
    for (const { file, name, message } of failures) {
      const firstLine = (message.split(/\r?\n/)[0] ?? "").replace(/\|/g, "\|").slice(0, 300);
      table += `| ${file} | \`${name}\` | ${firstLine} |\n`;
    }
  }
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, table);
}
console.log(`${fileCount} results file(s), ${failures.length} failed test(s), ${runErrors.length} run error(s) under ${root}.`);
