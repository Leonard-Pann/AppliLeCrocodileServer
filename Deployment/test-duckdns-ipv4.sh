#!/bin/sh
set -eu

test_directory=$(mktemp -d)
trap 'rm -rf "$test_directory"' EXIT
mkdir -p /run/secrets
cat > "$test_directory/curl" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" > "$TEST_DIRECTORY/arguments"
cat > "$TEST_DIRECTORY/config"
printf '%s\n' "${MOCK_RESPONSE:-OK}"
EOF
cat > "$test_directory/sleep" <<'EOF'
#!/bin/sh
printf '%s\n' "$1" > "$TEST_DIRECTORY/delay"
kill -TERM "$PPID"
EOF
chmod 755 "$test_directory/curl" "$test_directory/sleep"
export TEST_DIRECTORY="$test_directory" PATH="$test_directory:$PATH" DUCKDNS_DOMAIN=example

printf '%s\n' '00000000-0000-0000-0000-000000000000' > /run/secrets/duckdns-token
output=$(/usr/local/bin/update-duckdns 2>/dev/null || true)
case "$output" in *'Duck DNS IPv4 update succeeded.'*) ;; *) echo 'Valid update was not reported.'; exit 1 ;; esac
grep -q -- '--ipv4' "$test_directory/arguments"
grep -q 'domains=example.*&ip=' "$test_directory/config"
test "$(cat "$test_directory/delay")" = 3600

export MOCK_RESPONSE=KO
output=$(/usr/local/bin/update-duckdns 2>/dev/null || true)
case "$output" in *'update failed; retrying in one hour.'*) ;; *) echo 'API failure was not reported.'; exit 1 ;; esac
test "$(cat "$test_directory/delay")" = 3600

rm -f "$test_directory/config"
printf '%s\n' invalid > /run/secrets/duckdns-token
output=$(/usr/local/bin/update-duckdns 2>/dev/null || true)
case "$output" in *'invalid token.'*) ;; *) echo 'Invalid token was not rejected.'; exit 1 ;; esac
test ! -e "$test_directory/config"
echo 'Duck DNS IPv4 updater checks passed.'
