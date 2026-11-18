#!/bin/sh
set -u

while :; do
    token=$(tr -d '\r\n' < /run/secrets/duckdns-token 2>/dev/null) || token=''
    case "$token" in
        *[!0123456789abcdefABCDEF-]*|'') echo 'Duck DNS update failed: invalid token.'; sleep 3600; continue ;;
    esac
    case "$token" in
        ????????-????-????-????-????????????) ;;
        *) echo 'Duck DNS update failed: token file is missing or invalid.'; sleep 3600; continue ;;
    esac
    response=$(printf 'url = "https://www.duckdns.org/update?domains=%s&token=%s&ip="\n' "$DUCKDNS_DOMAIN" "$token" |
        curl --config - --ipv4 --silent --show-error --fail --max-time 30 2>/dev/null) || response=''
    unset token
    if [ "$response" = 'OK' ]; then
        echo 'Duck DNS IPv4 update succeeded.'
    else
        echo 'Duck DNS IPv4 update failed; retrying in one hour.'
    fi
    sleep 3600
done
