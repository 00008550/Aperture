#!/usr/bin/env bash
# measure.sh — ground truth for ap-surveyor. Reads the code, never the docs.
#
#   scripts/measure.sh endpoints     route -> authorization policy (unpoliced routes are findings)
#   scripts/measure.sh permissions   declared permissions vs. where they are enforced
#   scripts/measure.sh schema        module schemas, tables, and mapped column counts
#   scripts/measure.sh tests         test projects, test method counts, modules with none
#   scripts/measure.sh all
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

hr() { printf '\n== %s ==\n' "$1"; }

endpoints() {
  hr "ENDPOINTS (route -> policy)"
  local found=0 unpoliced=0
  while IFS= read -r line; do
    local file=${line%%:*}
    local rest=${line#*:}
    local lineno=${rest%%:*}
    local text=${rest#*:}
    local route
    route=$(printf '%s' "$text" | grep -oE 'Map(Get|Post|Put|Patch|Delete|Group)\("[^"]*"' | head -1)
    [ -z "$route" ] && continue
    found=$((found + 1))
    # the policy may sit on this line or in the chained calls that follow it
    local policy
    policy=$(sed -n "${lineno},$((lineno + 6))p" "$file" \
      | grep -oE 'RequirePermission\("[^"]*"\)|RequireAuthorization\("[^"]*"\)|RequireAuthorization\(\)|AllowAnonymous\(\)' \
      | head -1)
    if [ -z "$policy" ]; then
      policy="*** NO POLICY ***"
      unpoliced=$((unpoliced + 1))
    fi
    printf '  %-58s %-28s %s:%s\n' "$route" "$policy" "${file#./}" "$lineno"
  done < <(grep -rn --include=*.cs -E 'Map(Get|Post|Put|Patch|Delete|Group)\("' src 2>/dev/null)
  printf '\n  %d mapped routes, %d without a policy\n' "$found" "$unpoliced"
  [ "$unpoliced" -gt 0 ] && printf '  ^ every one of these is a finding, now, not later\n'
}

permissions() {
  hr "PERMISSIONS (declared -> enforced)"
  local decl
  decl=$(grep -rhoE 'public const string [A-Za-z]+ = "[^"]+"' src --include=Permissions.cs 2>/dev/null \
    | grep -oE '"[^"]+"' | tr -d '"' | sort -u)
  if [ -z "$decl" ]; then printf '  no Permissions.cs found\n'; return; fi
  local n=0 unused=0
  while IFS= read -r p; do
    [ -z "$p" ] && continue
    n=$((n + 1))
    local uses
    uses=$(grep -rl --include=*.cs -F "\"$p\"" src 2>/dev/null | grep -cv 'Permissions\.cs$')
    if [ "$uses" -eq 0 ]; then
      # also count symbolic use (Permissions.DealsRead) rather than the literal
      local sym
      sym=$(printf '%s' "$p" | awk -F'[.]' '{for(i=1;i<=NF;i++){printf toupper(substr($i,1,1)) substr($i,2)}}')
      uses=$(grep -rl --include=*.cs -F "Permissions.$sym" src 2>/dev/null | grep -cv 'Permissions\.cs$')
    fi
    if [ "$uses" -eq 0 ]; then unused=$((unused + 1)); printf '  %-34s *** DECLARED, NEVER ENFORCED ***\n' "$p"
    else printf '  %-34s enforced in %s file(s)\n' "$p" "$uses"; fi
  done <<< "$decl"
  printf '\n  %d permissions declared, %d never enforced\n' "$n" "$unused"
}

schema() {
  hr "SCHEMA (module -> schema -> tables -> mapped columns)"
  local any=0
  while IFS= read -r f; do
    any=1
    local tbl sch cols
    tbl=$(grep -oE 'ToTable\("[^"]+"' "$f" | head -1 | grep -oE '"[^"]+"' | tr -d '"')
    sch=$(grep -oE 'ToTable\("[^"]+", *"[^"]+"' "$f" | head -1 | grep -oE '"[^"]+"$' | tr -d '"')
    cols=$(grep -cE '\.Property\(' "$f")
    [ -z "$tbl" ] && continue
    printf '  %-12s %-24s %3s mapped columns   %s\n' "${sch:-<default>}" "$tbl" "$cols" "${f#./}"
  done < <(grep -rl --include=*.cs 'ToTable(' src 2>/dev/null | sort)
  [ "$any" -eq 0 ] && printf '  no EF entity configurations found yet\n'
  hr "MIGRATIONS"
  local m
  m=$(find src -type d -name Migrations 2>/dev/null | wc -l)
  printf '  %s Migrations folder(s)\n' "$m"
  find src -name '*_*.cs' -path '*Migrations*' 2>/dev/null | sed 's|^|  |'
}

tests() {
  hr "TESTS"
  local projs
  projs=$(find src -name '*.Tests.csproj' 2>/dev/null | sort)
  if [ -z "$projs" ]; then printf '  NO TEST PROJECTS AT ALL\n'; else
    while IFS= read -r p; do
      local d=$(dirname "$p")
      local facts=$(grep -rc --include=*.cs -E '^\s*\[(Fact|Theory)' "$d" 2>/dev/null | awk -F: '{s+=$2} END{print s+0}')
      printf '  %-58s %3s test methods\n' "${p#./}" "$facts"
    done <<< "$projs"
  fi
  hr "MODULES WITHOUT TESTS"
  # Counts test METHODS, not .csproj files: an empty test project is worse than none,
  # because dotnet test still exits 0 and the module looks covered. (001-P1 review.)
  local none=0
  while IFS= read -r mod; do
    local name=$(basename "$mod")
    local n
    n=$(grep -rc --include=*.cs -E '^\s*\[(Fact|Theory)' "$mod" 2>/dev/null | awk -F: '{s+=$2} END{print s+0}')
    if [ "${n:-0}" -eq 0 ]; then
      printf '  %-20s *** NO TESTS *** — a green dotnet test proves nothing about it\n' "$name"
      none=$((none + 1))
    fi
  done < <(find src/Modules -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort)
  [ "$none" -eq 0 ] && printf '  none\n'
  hr "FRONTEND TESTS"
  local specs
  specs=$(find frontend -name '*.test.ts' -o -name '*.test.tsx' -o -name '*.spec.ts' -o -name '*.spec.tsx' 2>/dev/null \
    | grep -v node_modules | wc -l)
  printf '  %s spec files under frontend/\n' "$specs"
}

case "${1:-all}" in
  endpoints) endpoints ;;
  permissions) permissions ;;
  schema) schema ;;
  tests) tests ;;
  all) endpoints; permissions; schema; tests ;;
  *) echo "usage: $0 {endpoints|permissions|schema|tests|all}"; exit 2 ;;
esac
