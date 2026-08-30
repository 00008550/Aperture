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

# Any Map<Something>("route") call, not a fixed list of verbs. The verb list missed
# MapHealthChecks — two routes were invisible to the gate that exists to prove no route is
# unpoliced, which is the one blind spot that gate must not have. It will equally miss the
# next MapHub or MapRazorPages, so the pattern is the shape of the call, not a vocabulary.
# The endpoint architecture test in Aperture.Api.Tests asserts the same rule against the
# endpoints the host actually built; this grep is the cheap copy that runs without a build.
ROUTE_CALL='Map[A-Z][A-Za-z]*\("'

endpoints() {
  hr "ENDPOINTS (route -> policy)"
  local found=0 unpoliced=0
  while IFS= read -r line; do
    local file=${line%%:*}
    local rest=${line#*:}
    local lineno=${rest%%:*}
    local text=${rest#*:}
    local route
    route=$(printf '%s' "$text" | grep -oE "${ROUTE_CALL}[^\"]*\"" | head -1)
    [ -z "$route" ] && continue
    found=$((found + 1))
    # The policy may sit on this line or anywhere in the chained calls that follow it, so
    # scan to the end of the statement rather than a fixed number of lines. A fixed window
    # reported /api/me as unpoliced purely because its handler lambda was long — a gate that
    # depends on formatting is a gate people learn to override.
    local endline
    endline=$(awk -v s="$lineno" 'NR>=s && /;[[:space:]]*$/ {print NR; exit}' "$file")
    [ -z "$endline" ] && endline=$((lineno + 6))
    local policy
    policy=$(sed -n "${lineno},${endline}p" "$file" \
      | grep -oE 'RequirePermission\("[^"]*"\)|RequireAuthorization\("[^"]*"\)|RequireAuthorization\(\)|AllowAnonymous\(\)' \
      | head -1)
    if [ -z "$policy" ]; then
      policy="*** NO POLICY ***"
      unpoliced=$((unpoliced + 1))
    fi
    printf '  %-58s %-28s %s:%s\n' "$route" "$policy" "${file#./}" "$lineno"
    # Test sources are excluded: they map deliberately unpoliced routes to prove the
    # architecture test catches them, and counting those as findings would train everyone to
    # ignore the gate.
  done < <(grep -rn --include=*.cs -E "$ROUTE_CALL" src 2>/dev/null | grep -v '\.Tests/')
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
  # Migrations and model snapshots are generated restatements of the configurations; counting
  # them reported every table three times.
  local files
  files=$(grep -rl --include=*.cs 'ToTable(' src 2>/dev/null | grep -v '/Migrations/' | sort)
  if [ -z "$files" ]; then
    printf '  no EF entity configurations found yet\n'
  else
    local out
    out=$(echo "$files" | xargs awk -f "$(dirname "$0")/measure_schema.awk" | sort)
    echo "$out"
    printf '\n  %s tables\n' "$(echo "$out" | grep -c 'mapped columns')"
  fi
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

# The two invariants worth failing a build over. Both are cheap greps, both encode a
# rule from CLAUDE.md, and both currently pass trivially because the surfaces they
# guard do not exist yet — which is the point of adding them before the surfaces do.
gate() {
  local failures=0

  hr "GATE 1 — every mapped route carries an authorization policy (CLAUDE.md invariant 4)"
  local unpoliced
  unpoliced=$(endpoints | grep -c 'NO POLICY' || true)
  if [ "$unpoliced" -gt 0 ]; then
    endpoints | grep 'NO POLICY'
    printf '  FAIL: %d route(s) mapped without a policy\n' "$unpoliced"
    failures=$((failures + 1))
  else
    printf '  pass\n'
  fi

  hr "GATE 2 — raw SQL passes tenant_id explicitly (CLAUDE.md invariant 2)"
  # EF inherits the global query filter; Dapper and FromSql do not. A raw statement
  # with no tenant_id in sight is a cross-tenant read, not a style preference.
  local leaked=0
  while IFS= read -r hit; do
    [ -z "$hit" ] && continue
    local file=${hit%%:*}
    local rest=${hit#*:}
    local lineno=${rest%%:*}
    if ! sed -n "$((lineno > 4 ? lineno - 4 : 1)),$((lineno + 12))p" "$file" | grep -qi 'tenant'; then
      printf '  %s:%s  raw SQL with no tenant_id nearby\n' "${file#./}" "$lineno"
      leaked=$((leaked + 1))
    fi
    # Matched on Dapper/EF raw-SQL entry points specifically. A bare `ExecuteAsync(`
    # was the first pattern here and it flagged BackgroundService.ExecuteAsync — a gate
    # that cries wolf gets disabled, so it is narrow on purpose.
  done < <(grep -rn --include=*.cs -E 'FromSql(Raw|Interpolated)?|ExecuteSql(Raw|Interpolated)?|\.Query(Async|First|FirstAsync|Single|SingleAsync|Multiple)?<|Dapper' src 2>/dev/null)
  if [ "$leaked" -gt 0 ]; then
    printf '  FAIL: %d raw SQL call(s) with no visible tenant predicate\n' "$leaked"
    failures=$((failures + 1))
  else
    printf '  pass\n'
  fi

  hr "ADVISORY (not a build failure)"
  permissions | grep -E 'never enforced' || true
  tests | grep -E 'NO TESTS|spec files' || true

  printf '\n'
  if [ "$failures" -gt 0 ]; then
    printf 'GATE FAILED: %d invariant(s) violated\n' "$failures"
    return 1
  fi
  printf 'GATE PASSED\n'
}

case "${1:-all}" in
  endpoints) endpoints ;;
  gate) gate ;;
  permissions) permissions ;;
  schema) schema ;;
  tests) tests ;;
  all) endpoints; permissions; schema; tests ;;
  *) echo "usage: $0 {gate|endpoints|permissions|schema|tests|all}"; exit 2 ;;
esac
