#!/bin/sh
set -u
umask 077

domain="${DUCKDNS_DOMAIN:-}.duckdns.org"
case "${DUCKDNS_DOMAIN:-}" in
    ''|*[!a-z0-9-]*) echo 'Invalid Duck DNS subdomain.'; exit 1 ;;
esac
case "${ACME_SERVER:-letsencrypt_test}" in
    letsencrypt_test) server=letsencrypt_test ;;
    letsencrypt) server=letsencrypt ;;
    *) echo 'Invalid ACME server.'; exit 1 ;;
esac

mkdir -p /acme.sh /certificates
chmod 700 /acme.sh
chmod 755 /certificates
identity="$domain $server"
if [ -f /acme.sh/deployment-identity ]; then
    if [ "$(cat /acme.sh/deployment-identity)" != "$identity" ]; then
        echo 'ACME volume belongs to a different domain or CA; refusing to continue.'
        exit 1
    fi
elif [ -f "/acme.sh/${domain}_ecc/${domain}.conf" ]; then
    echo 'Existing ACME state has no deployment identity; use a separate volume.'
    exit 1
else
    printf '%s\n' "$identity" > /acme.sh/deployment-identity
fi
if [ -f /certificates/deployment-identity ]; then
    if [ "$(cat /certificates/deployment-identity)" != "$identity" ]; then
        echo 'Certificate volume belongs to a different domain or CA; refusing to continue.'
        exit 1
    fi
elif [ -e /certificates/server.pfx ]; then
    echo 'Existing certificate has no deployment identity; use a separate volume.'
    exit 1
else
    printf '%s\n' "$identity" > /certificates/deployment-identity
    chmod 644 /certificates/deployment-identity
fi
certificate_dir="/acme.sh/${domain}_ecc"
fullchain="$certificate_dir/fullchain.cer"
key="$certificate_dir/${domain}.key"

publish_certificate() {
    [ -s "$fullchain" ] && [ -s "$key" ] || return 1
    new_fingerprint=$(openssl x509 -in "$fullchain" -noout -fingerprint -sha256 2>/dev/null) || return 1
    old_fingerprint=''
    if [ -s /certificates/server.pfx ]; then
        old_fingerprint=$(openssl pkcs12 -in /certificates/server.pfx -passin pass: -clcerts -nokeys 2>/dev/null |
            openssl x509 -noout -fingerprint -sha256 2>/dev/null) || old_fingerprint=''
    fi
    if [ "$new_fingerprint" = "$old_fingerprint" ]; then
        echo 'Certificate is unchanged.'
        return 0
    fi
    temporary="/certificates/server.pfx.new"
    if ! openssl pkcs12 -export -out "$temporary" -inkey "$key" -in "$fullchain" -passout pass:; then
        rm -f "$temporary"
        echo 'Failed to package certificate; the previous certificate remains in place.'
        return 1
    fi
    chmod 440 "$temporary" && chown 1654:1654 "$temporary" && mv -f "$temporary" /certificates/server.pfx || return 1
    echo 'Published a new certificate to the shared volume.'
}

while :; do
    token=$(tr -d '\r\n' < /run/secrets/duckdns-token 2>/dev/null) || token=''
    case "$token" in
        ????????-????-????-????-????????????) ;;
        *) echo 'Duck DNS token is missing or invalid; retrying in one hour.'; sleep 3600; continue ;;
    esac
    case "$token" in
        *[!0123456789abcdefABCDEF-]*) echo 'Duck DNS token is invalid; retrying in one hour.'; sleep 3600; continue ;;
    esac
    DuckDNS_Token="$token"
    export DuckDNS_Token
    unset token
    if [ ! -s "$fullchain" ] || [ ! -s "$key" ]; then
        echo "Requesting a certificate for $domain from $server."
        acme.sh --issue --server "$server" --dns dns_duckdns -d "$domain" --keylength ec-256
        result=$?
    else
        echo 'Checking whether certificate renewal is due.'
        acme.sh --renew --server "$server" -d "$domain" --ecc
        result=$?
    fi
    unset DuckDNS_Token
    if [ "$result" -eq 0 ] || [ "$result" -eq 2 ]; then
        if publish_certificate; then
            sleep 43200
            continue
        fi
        echo 'Certificate publication failed; retrying in one hour.'
    else
        echo 'ACME request failed; retrying in one hour.'
    fi
    sleep 3600
done
