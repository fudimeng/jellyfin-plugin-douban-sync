#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 5 ]]; then
    echo "Usage: $0 <version> <tag> <repository> <zip-path> <output-path>" >&2
    exit 2
fi

version=$1
tag=$2
repository=$3
zip_path=$4
output_path=$5

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

zip_name=$(basename "$zip_path")
checksum=$(md5sum "$zip_path" | awk '{print $1}')
timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
source_url="https://github.com/${repository}/releases/download/${tag}/${zip_name}"
output_directory=$(dirname "$output_path")
mkdir -p "$output_directory"
temporary_path=$(mktemp "${output_directory}/manifest.XXXXXX")
trap 'rm -f "$temporary_path"' EXIT

jq -n \
    --arg version "${version}.0" \
    --arg source_url "$source_url" \
    --arg checksum "$checksum" \
    --arg timestamp "$timestamp" \
    '[
      {
        guid: "58f521f4-ff96-4ac4-b6ac-14fdc730659a",
        name: "豆瓣同步",
        description: "将 Jellyfin 中已观看的电影和完整看完的剧集季同步为豆瓣“看过”。",
        overview: "同步 Jellyfin 已观看电影和完整剧集季到豆瓣",
        owner: "doubanSyn contributors",
        category: "General",
        versions: [
          {
            version: $version,
            changelog: "查看 GitHub Release 获取完整更新说明。",
            targetAbi: "10.11.11.0",
            sourceUrl: $source_url,
            checksum: $checksum,
            timestamp: $timestamp
          }
        ]
      }
    ]' > "$temporary_path"

jq empty "$temporary_path"
mv "$temporary_path" "$output_path"
trap - EXIT
