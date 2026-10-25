#!/bin/sh
# BepInEx for Valheim on macOS, native on Apple Silicon (docs/macos-loader.mdx):
#   site/public/downloads/BepInEx_macos_valheim_<bepinex>-<commit>.zip
# BepInEx built from the v5-lts commit that carries PR 1402, with the source tree's run_bepinex.sh preset for
# valheim.app, and UnityDoorstop 4.6.0 (PR 114) as libdoorstop.dylib.
# Needs a .NET 8 SDK: `dotnet` on PATH, or DOTNET naming its executable.
set -eu

bepinex_commit=01fd4aabe29a3fad7d479347f4eb58f24ea46836
harmony_commit=d4cdcb4cdeac14a0b77012165f5f5a9f5032a9fa
bepinex_version=5.4.23.5
# `ci` is a rolling pre-release tag: the hash, not the tag, names the build this package was proven with.
doorstop_zip=doorstop_macos_release_4.6.0.zip
doorstop_url=https://github.com/NeighTools/UnityDoorstop/releases/download/ci/$doorstop_zip
doorstop_sha=22790b63ef25a3737eb4a80dfe49ea80cfce3bb2f48689ef19dc11bcdd390c6f

here=$(cd "$(dirname "$0")" && pwd -P)
pack=$(git -C "$here" rev-parse --show-toplevel)
work=$pack/data/macos-loader
if [ "$here" = "$pack/mod/macos" ]; then
	outdir=$pack/site/public/downloads
else
	outdir=$pack/dist
fi
mkdir -p "$outdir"
out=$outdir/BepInEx_macos_valheim_$bepinex_version-$(printf %.7s $bepinex_commit).zip
dotnet=${DOTNET:-dotnet}

fetch() { # url file sha256
	[ -f "$2" ] || curl -fsSL "$1" -o "$2"
	echo "$3  $2" | shasum -a 256 -c - >/dev/null || { echo "$2 does not match $3; the upstream file changed" >&2; exit 1; }
}

rm -rf "$work/src" "$work/stage"
mkdir -p "$work/dl" "$work/src" "$work/stage"
fetch "$doorstop_url" "$work/dl/$doorstop_zip" $doorstop_sha
curl -fsSL https://codeload.github.com/BepInEx/BepInEx/tar.gz/$bepinex_commit | tar -xzf - -C "$work/src"
curl -fsSL https://codeload.github.com/BepInEx/BepInEx.Harmony/tar.gz/$harmony_commit | tar -xzf - -C "$work/src"
src=$work/src/BepInEx-$bepinex_commit
rmdir "$src/submodules/BepInEx.Harmony"
mv "$work/src/BepInEx.Harmony-$harmony_commit" "$src/submodules/BepInEx.Harmony"

DOTNET_CLI_HOME=$work/dotnet-home NUGET_PACKAGES=$work/nuget DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
	"$dotnet" build "$src/BepInEx.Preloader/BepInEx.Preloader.csproj" -c Release -v q -p:ContinuousIntegrationBuild=true
bin=$src/BepInEx.Preloader/bin/Release/net35

stage=$work/stage
mkdir -p "$stage/BepInEx/core" "$stage/BepInEx/plugins"
cp "$bin"/*.dll "$stage/BepInEx/core/"
unzip -q -j "$work/dl/$doorstop_zip" universal/libdoorstop.dylib universal/.doorstop_version -d "$stage"
sed 's/^executable_name=""$/executable_name="valheim.app"/' "$src/doorstop/run_bepinex.sh" > "$stage/run_bepinex.sh"
grep -q '^executable_name="valheim.app"$' "$stage/run_bepinex.sh"
chmod 755 "$stage/run_bepinex.sh"
chmod 644 "$stage/libdoorstop.dylib" "$stage/.doorstop_version" "$stage"/BepInEx/core/*

# Fixed times and sorted entries, so the same inputs give the same zip.
find "$stage" -exec touch -t 202609200000 {} +
rm -f "$out"
(cd "$stage" && find . -mindepth 1 | sed 's|^\./||' | LC_ALL=C sort | zip -X -q "$out" -@)
zipinfo "$out" | grep -v '^Archive\|^Zip file\|files,'
shasum -a 256 "$out"
