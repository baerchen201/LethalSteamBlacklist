#!/usr/bin/env bash
cd "$(dirname "$(realpath $0)")"
shopt -s extglob globstar

dotnet build
path=$(mktemp -u --suffix=.zip)
zip $path icon.png manifest.json README.md bin/**/*.dll
echo "Zip created at $path"
