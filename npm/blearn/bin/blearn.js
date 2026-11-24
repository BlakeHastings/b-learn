#!/usr/bin/env node
// Runs the b-learn binary for this platform. npm installs exactly one platform package
// (blearn-<os>-<cpu>) through optionalDependencies; this script finds it and hands over.
"use strict";
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const pkg = `blearn-${process.platform}-${process.arch}`;
const exe = process.platform === "win32" ? "blearn.exe" : "blearn";

let binary;
try {
  binary = path.join(path.dirname(require.resolve(`${pkg}/package.json`)), "bin", exe);
} catch {
  console.error(
    `b-learn: no build is installed for ${process.platform}-${process.arch} (package ${pkg}).\n` +
    "Reinstall without --no-optional / --omit=optional, or download a binary from\n" +
    "https://github.com/BlakeHastings/b-learn/releases");
  process.exit(1);
}

if (process.platform !== "win32") {
  try { fs.accessSync(binary, fs.constants.X_OK); } catch { try { fs.chmodSync(binary, 0o755); } catch {} }
}

const result = spawnSync(binary, process.argv.slice(2), { stdio: "inherit" });
if (result.error) {
  console.error(`b-learn: could not start ${binary}: ${result.error.message}`);
  process.exit(1);
}
process.exit(result.status ?? 1);
