#!/usr/bin/env node
// Git merge driver for .resx files: three-way merge of <data> entries by name.
//
//   git config merge.resx.name "resx entries merged by key"
//   git config merge.resx.driver "node tools/git/merge-resx.mjs %O %A %B"
//   (.gitattributes: *.resx merge=resx)
//
// Entries added on either side are kept; an entry changed or deleted on one side only takes that change; an entry
// changed differently on both sides is a conflict (exit 1, our version kept, the key printed). The header
// (<resheader>, schema) comes from our version. Output keeps our entry order, then the entries added by theirs.

import { readFileSync, writeFileSync } from "node:fs";

const [basePath, oursPath, theirsPath] = process.argv.slice(2);
if (!basePath || !oursPath || !theirsPath) {
  console.error("usage: merge-resx.mjs <base> <ours> <theirs>");
  process.exit(2);
}

const dataBlock = /^[ \t]*<data\s+name="([^"]+)"[\s\S]*?<\/data>[ \t]*\r?\n?/gm;

function parse(path) {
  const text = readFileSync(path, "utf8");
  const entries = new Map();
  let firstIndex = -1;
  for (const match of text.matchAll(dataBlock)) {
    if (firstIndex < 0) firstIndex = match.index;
    entries.set(match[1], match[0].replace(/\r?\n?$/, ""));
  }
  const closing = text.lastIndexOf("</root>");
  if (closing < 0) throw new Error(`${path}: no </root> element`);
  const header = firstIndex < 0 ? text.slice(0, closing) : text.slice(0, firstIndex);
  return { text, entries, header };
}

let base, ours, theirs;
try {
  base = parse(basePath);
  ours = parse(oursPath);
  theirs = parse(theirsPath);
} catch (error) {
  console.error(`merge-resx: ${error.message}`);
  process.exit(1);
}

const result = new Map();
const conflicts = [];

for (const [key, ourBlock] of ours.entries) {
  const baseBlock = base.entries.get(key);
  const theirBlock = theirs.entries.get(key);
  if (theirBlock === undefined) {
    // Deleted by theirs: drop it unless we changed it.
    if (baseBlock !== undefined && baseBlock === ourBlock) continue;
    if (baseBlock !== undefined) conflicts.push(`${key} (changed here, deleted there)`);
    result.set(key, ourBlock);
  } else if (theirBlock === ourBlock || theirBlock === baseBlock) {
    result.set(key, ourBlock);
  } else if (ourBlock === baseBlock) {
    result.set(key, theirBlock);
  } else {
    conflicts.push(`${key} (changed on both sides)`);
    result.set(key, ourBlock);
  }
}

for (const [key, theirBlock] of theirs.entries) {
  if (result.has(key) || ours.entries.has(key)) continue;
  const baseBlock = base.entries.get(key);
  if (baseBlock === undefined) {
    result.set(key, theirBlock); // added by theirs
  } else if (baseBlock !== theirBlock) {
    conflicts.push(`${key} (deleted here, changed there)`);
  }
  // else: deleted by us and unchanged by theirs: stay deleted.
}

const newline = ours.text.includes("\r\n") ? "\r\n" : "\n";
const body = [...result.values()].join(newline);
const output = `${ours.header.replace(/\s*$/, "")}${newline}${body}${body ? newline : ""}</root>${newline}`;
writeFileSync(oursPath, output, "utf8");

if (conflicts.length > 0) {
  console.error(`merge-resx: conflicting entries in ${oursPath}:\n  ${conflicts.join("\n  ")}`);
  process.exit(1);
}
