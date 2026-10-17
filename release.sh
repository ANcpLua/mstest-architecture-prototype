#!/usr/bin/env bash
# Publish the exact source checked with native tests, coverage and inspections.
set -euo pipefail
cd "$(dirname "$0")"
[[ $# -ge 1 && $1 =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'usage: ./release.sh <x.y.z> [--filter <MSTest filter> --minimum-tests <count>]' >&2; exit 2; }
version=$1
shift
build_args=()
if [[ $# != 0 ]]; then
  [[ $# == 4 && $1 == --filter && -n $2 && $3 == --minimum-tests && $4 =~ ^[1-9][0-9]*$ ]] || {
    echo 'A focused release requires both --filter and --minimum-tests.' >&2; exit 2;
  }
  build_args=(--TestFilter "$2" --MinimumExpectedTests "$4")
fi
[[ $(git branch --show-current) == main ]] || { echo 'Release from main.' >&2; exit 1; }
[[ -z $(git status --porcelain) ]] || { echo 'Commit the complete change before release.' >&2; exit 1; }
git fetch origin main
commit=$(git rev-parse HEAD)
[[ $commit == "$(git rev-parse origin/main)" ]] || { echo 'Local main must equal origin/main.' >&2; exit 1; }
if git show-ref --verify --quiet "refs/tags/v$version"; then
  echo 'Release tag already exists; inspect its evidence instead.' >&2; exit 1
fi
mkdir -p "artifacts/release-$version"
evidence="$PWD/artifacts/release-$version"
python3 - "$version" "$evidence" <<'PY'
from pathlib import Path
import sys, xml.etree.ElementTree as E
version, output=sys.argv[1:]
assert E.parse('Directory.Build.props').findtext('.//Version') == version, 'Version does not match'
text=Path('CHANGELOG.md').read_text()
heading='\n## '+version+'\n'
assert heading in text, 'Changelog entry is missing'
Path(output,'notes.md').write_text(text.split(heading,1)[1].split('\n## ',1)[0].strip()+'\n')
PY
command -v dotcov > "$evidence/dotcov-path.txt"
dotcov --version > "$evidence/dotcov-version.txt"
archive="$evidence/prototype-$version.tar.gz"
git archive --format=tar.gz --prefix="prototype-$version/" "$commit" > "$archive"
scratch=$(mktemp -d "${TMPDIR:-/tmp}/prototype-release-$version.XXXXXXXX")
# Retain the exact checked source and every telemetry phase for inspection.
# Nothing in the release restores checkout assets into a disposable package cache.
tar -xzf "$archive" -C "$scratch"
source_dir="$scratch/prototype-$version"
printf '%s\n' "$source_dir" > "$evidence/source-path.txt"
printf '%s\n' "$commit" > "$evidence/source-commit.txt"
printf '%s\n' "${build_args[@]}" > "$evidence/test-scope.txt"
if ! (cd "$source_dir" && ./build.sh --configuration Release "${build_args[@]}") > "$evidence/released-source-gate.log" 2>&1; then
  echo "Released-source gate failed. Complete evidence: $evidence/released-source-gate.log" >&2
  exit 1
fi
[[ $(git rev-parse HEAD) == "$commit" && -z $(git status --porcelain) ]] || {
  echo 'Source changed during verification; nothing published.' >&2; exit 1;
}
cp "$source_dir/artifacts/coverage.json" "$evidence/coverage.json"
cp "$source_dir/artifacts/jb-inspections.xml" "$evidence/jb-inspections.xml"
git tag -a "v$version" "$commit" -m "Architecture prototype $version"
git push origin "v$version"
gh release create "v$version" --verify-tag --title "v$version" --notes-file "$evidence/notes.md" \
  "$archive" "$evidence/coverage.json" "$evidence/jb-inspections.xml" "$evidence/test-scope.txt"
printf 'VERIFIED v%s commit=%s source=%s\n' "$version" "$commit" "$source_dir" | tee "$evidence/VERIFIED"
