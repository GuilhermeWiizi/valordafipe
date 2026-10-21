#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
bash -n scripts/deploy.sh
mkdir -p .artifacts/deploy-tests
base=$(mktemp -d "$PWD/.artifacts/deploy-tests/run.XXXXXX")
mkdir -p "$base/bin"
cat > "$base/bin/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$MOCK_LOG"
case "$*" in
    *" config --quiet") [[ "$MOCK_FAIL" != config ]] ;;
    *" build web")
        [[ "$BUILD_REVISION" == "$MOCK_REVISION" ]] || exit 8
        [[ "$MOCK_FAIL" != build ]] ;;
    *" up -d "*) [[ "$MOCK_FAIL" != up ]] ;;
    *" port web 8080") echo '127.0.0.1:18081' ;;
    *" exec -T web printenv Seo__SiteUrl") echo 'https://fipe.example.test' ;;
esac
STUB
cat > "$base/bin/curl" <<'STUB'
#!/usr/bin/env bash
printf 'curl %s\n' "$*" >> "$MOCK_LOG"
case "${!#}" in
    "http://127.0.0.1:18081/health/version?revision=$MOCK_REVISION")
        [[ "$MOCK_FAIL" != http ]] || exit 7
        if [[ "$MOCK_FAIL" == old-image ]]; then echo old-revision; else echo "$MOCK_REVISION"; fi ;;
    "https://fipe.example.test/health/version?revision=$MOCK_REVISION")
        [[ "$MOCK_FAIL" != public-http ]] || exit 7
        if [[ "$MOCK_FAIL" == old-public ]]; then echo old-revision; else echo "$MOCK_REVISION"; fi ;;
    *) exit 9 ;;
esac
STUB
cat > "$base/bin/sleep" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$base/bin/"*
export PATH="$base/bin:$PATH"
for scenario in success config build up http old-image public-http old-public missing-env dirty wrong-revision; do
    fixture="$base/$scenario"
    mkdir -p "$fixture/scripts"
    cp scripts/deploy.sh "$fixture/scripts/deploy.sh"
    git -C "$fixture" init -q
    git -C "$fixture" add scripts/deploy.sh
    git -C "$fixture" -c user.name=DeployTest -c user.email=test@example.test -c commit.gpgsign=false commit -qm fixture
    export MOCK_REVISION
    MOCK_REVISION=$(git -C "$fixture" rev-parse HEAD)
    expected=$MOCK_REVISION
    if [[ "$scenario" == wrong-revision ]]; then expected=0000000000000000000000000000000000000000; fi
    if [[ "$scenario" == dirty ]]; then printf '\n# dirty\n' >> "$fixture/scripts/deploy.sh"; fi
    if [[ "$scenario" != missing-env ]]; then printf 'POSTGRES_PASSWORD=test-placeholder\n' > "$fixture/.env.vps"; fi
    export MOCK_LOG="$fixture/commands.log" MOCK_FAIL="$scenario"
    : > "$MOCK_LOG"
    actual=0
    bash "$fixture/scripts/deploy.sh" "$expected" > "$fixture/output.log" 2>&1 || actual=$?
    if [[ "$scenario" == success ]]; then
        [[ "$actual" == 0 ]] || { cat "$fixture/output.log"; exit 1; }
        grep -q 'confirmado no container e em https://fipe.example.test' "$fixture/output.log"
        grep -q 'curl .*https://fipe.example.test/health/version' "$MOCK_LOG"
    else
        [[ "$actual" != 0 ]] || { echo "Expected failure: $scenario"; exit 1; }
        if grep -q 'FIPE atualizado:' "$fixture/output.log"; then echo "False success: $scenario"; exit 1; fi
    fi
    case "$scenario" in
        config|build|missing-env|dirty|wrong-revision)
            if grep -q ' up -d ' "$MOCK_LOG"; then echo "Unexpected deployment: $scenario"; exit 1; fi ;;
        up|http|old-image) grep -q ' logs --tail 80 web' "$MOCK_LOG" ;;
    esac
    echo "PASS: $scenario"
done
