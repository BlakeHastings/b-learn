// Assembles the npm packages from published binaries.
//
//   node npm/build.mjs --version 0.1.0 --dist dist --out out/npm
//
// dist/<rid>/blearn(.exe) for each runtime identifier that was published. Produces:
//   out/npm/blearn                 the launcher package (optionalDependencies on every platform package built)
//   out/npm/blearn-<os>-<cpu>      one package per platform, holding its binary
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, v, i, all) => (v.startsWith("--") ? [...acc, [v.slice(2), all[i + 1]]] : acc), []));
const version = args.version;
const dist = path.resolve(args.dist ?? "dist");
const out = path.resolve(args.out ?? "out/npm");
if (!version || !/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) throw new Error(`--version must be semver, got '${version}'`);

// .NET runtime identifier -> Node's process.platform / process.arch
const platforms = {
  "win-x64": ["win32", "x64"], "win-arm64": ["win32", "arm64"],
  "linux-x64": ["linux", "x64"], "linux-arm64": ["linux", "arm64"],
  "osx-x64": ["darwin", "x64"], "osx-arm64": ["darwin", "arm64"],
};

const common = {
  version,
  license: "GPL-3.0-only",
  homepage: "https://github.com/BlakeHastings/b-learn",
  repository: { type: "git", url: "git+https://github.com/BlakeHastings/b-learn.git" },
  bugs: "https://github.com/BlakeHastings/b-learn/issues",
};

fs.rmSync(out, { recursive: true, force: true });
const built = {};
for (const [rid, [os, cpu]] of Object.entries(platforms)) {
  const exe = os === "win32" ? "blearn.exe" : "blearn";
  const binary = path.join(dist, rid, exe);
  if (!fs.existsSync(binary)) continue;
  const name = `blearn-${os}-${cpu}`;
  const dir = path.join(out, name);
  fs.mkdirSync(path.join(dir, "bin"), { recursive: true });
  fs.copyFileSync(binary, path.join(dir, "bin", exe));
  fs.chmodSync(path.join(dir, "bin", exe), 0o755);
  write(dir, {
    name, ...common,
    description: `The b-learn binary for ${os} ${cpu}. Install "blearn" instead; it picks this up automatically.`,
    os: [os], cpu: [cpu], files: ["bin"], preferUnplugged: true,
  });
  fs.writeFileSync(path.join(dir, "README.md"), `# ${name}\n\nThe ${os}-${cpu} binary for [b-learn](https://github.com/BlakeHastings/b-learn). Use \`npx blearn\` instead of installing this directly.\n`);
  built[name] = version;
}
if (Object.keys(built).length === 0) throw new Error(`No binaries found under ${dist}`);

const main = path.join(out, "blearn");
fs.mkdirSync(path.join(main, "bin"), { recursive: true });
fs.copyFileSync(path.join(here, "blearn", "bin", "blearn.js"), path.join(main, "bin", "blearn.js"));
fs.copyFileSync(path.join(here, "blearn", "README.md"), path.join(main, "README.md"));
fs.copyFileSync(path.join(here, "..", "LICENSE"), path.join(main, "LICENSE"));
write(main, {
  name: "blearn", ...common,
  description: "Learn any topic as a graph of evidence-backed concepts you can dive into. Bring your own AI agent.",
  keywords: ["learning", "knowledge-graph", "ai-agents", "claude-code", "study"],
  bin: { blearn: "bin/blearn.js" },
  files: ["bin", "README.md", "LICENSE"],
  engines: { node: ">=18" },
  optionalDependencies: built,
});
console.log(`Built blearn ${version} with ${Object.keys(built).join(", ")} into ${out}`);

function write(dir, json) {
  fs.writeFileSync(path.join(dir, "package.json"), JSON.stringify(json, null, 2) + "\n");
}
