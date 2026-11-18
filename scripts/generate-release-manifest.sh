#!/usr/bin/env bash

set -euo pipefail

if [[ $# -lt 6 || $# -gt 7 ]]; then
    echo "Usage: $0 <version> <tag> <repository> <zip-path> <output-path> <release-notes-path> [previous-manifest]" >&2
    exit 2
fi

version=$1
tag=$2
repository=$3
zip_path=$4
output_path=$5
release_notes_path=$6
previous_manifest=${7:-/dev/null}

if [[ ! $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Version must use X.Y.Z format." >&2
    exit 2
fi

if [[ $tag != "v$version" ]]; then
    echo "Tag $tag does not match version $version." >&2
    exit 2
fi

if [[ ! $repository =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
    echo "Repository must use owner/name format." >&2
    exit 2
fi

if [[ ! -f $zip_path ]]; then
    echo "Plugin package not found: $zip_path" >&2
    exit 2
fi

if [[ ! -s $release_notes_path ]]; then
    echo "Release notes not found or empty: $release_notes_path" >&2
    exit 2
fi

zip_name=$(basename "$zip_path")
if [[ ! $zip_name =~ ^douban-sync_([0-9]+\.[0-9]+\.[0-9]+)_jellyfin-([0-9]+\.[0-9]+\.[0-9]+)\.zip$ || ${BASH_REMATCH[1]} != "$version" ]]; then
    echo "Plugin package must match the release version and identify the Jellyfin version." >&2
    exit 2
fi
target_abi="${BASH_REMATCH[2]}.0"
checksum=$(openssl dgst -md5 -r "$zip_path" | awk '{print $1}')
timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
source_url="https://github.com/${repository}/releases/download/${tag}/${zip_name}"
changelog=$(<"$release_notes_path")
output_directory=$(dirname "$output_path")
mkdir -p "$output_directory"
temporary_path=$(mktemp "${output_directory}/manifest.XXXXXX")
trap 'rm -f "$temporary_path"' EXIT

jq -n \
    --arg version "${version}.0" \
    --arg source_url "$source_url" \
    --arg checksum "$checksum" \
    --arg target_abi "$target_abi" \
    --slurpfile previous "$previous_manifest" \
    --arg timestamp "$timestamp" \
    --arg changelog "$changelog" \
    '[
      {
        guid: "58f521f4-ff96-4ac4-b6ac-14fdc730659a",
        name: "豆瓣同步",
        description: "将 Jellyfin 中已观看的电影和完整看完的剧集季同步为豆瓣“看过”。",
        overview: "同步 Jellyfin 已观看电影和完整剧集季到豆瓣",
        owner: "doubanSyn contributors",
        category: "General",
        versions: ([
          {
            version: $version,
            changelog: $changelog,
            targetAbi: $target_abi,
            sourceUrl: $source_url,
            checksum: $checksum,
            timestamp: $timestamp
          }
        ] + [ $previous[0][]? | select(.guid == "58f521f4-ff96-4ac4-b6ac-14fdc730659a")
              | .versions[] | select(.version != $version) ])
      }
    ]' > "$temporary_path"

jq empty "$temporary_path"
mv "$temporary_path" "$output_path"
trap - EXIT
