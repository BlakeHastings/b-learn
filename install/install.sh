#!/bin/sh
# Installs b-learn for macOS or Linux:
#   curl -fsSL https://raw.githubusercontent.com/BlakeHastings/b-learn/main/install/install.sh | sh
#   curl -fsSL .../install.sh | sh -s -- --channel edge        # latest build of main
# Puts a single self-contained binary in ~/.blearn/bin (override with BLEARN_INSTALL_DIR).
set -eu

repo="BlakeHastings/b-learn"
channel="stable"
while [ $# -gt 0 ]; do
  case "$1" in
    --channel) channel="${2:-}"; shift 2 ;;
    --channel=*) channel="${1#*=}"; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

case "$(uname -s)" in
  Linux) os="linux" ;;
  Darwin) os="osx" ;;
  *) echo "Unsupported OS $(uname -s). On Windows use install.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) arch="x64" ;;
  arm64 | aarch64) arch="arm64" ;;
  *) echo "Unsupported CPU $(uname -m)." >&2; exit 1 ;;
esac

asset="blearn-$os-$arch"
case "$channel" in
  stable) url="https://github.com/$repo/releases/latest/download/$asset" ;;
  edge) url="https://github.com/$repo/releases/download/edge/$asset" ;;
  *) echo "--channel must be stable or edge" >&2; exit 2 ;;
esac
url="${BLEARN_DOWNLOAD_URL:-$url}"

dir="${BLEARN_INSTALL_DIR:-$HOME/.blearn/bin}"
mkdir -p "$dir"
echo "Downloading b-learn ($channel, $os-$arch)..."
curl -fSL --progress-bar "$url" -o "$dir/blearn.tmp"
chmod +x "$dir/blearn.tmp"
mv "$dir/blearn.tmp" "$dir/blearn"
echo "Installed $("$dir/blearn" --version) to $dir/blearn"

case ":$PATH:" in
  *":$dir:"*) echo "Run: blearn" ;;
  *)
    echo
    echo "Add it to your PATH (then open a new terminal):"
    echo "  echo 'export PATH=\"$dir:\$PATH\"' >> ~/.profile"
    echo "Or run it directly: $dir/blearn"
    ;;
esac
