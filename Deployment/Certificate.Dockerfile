FROM neilpang/acme.sh:3.1.2@sha256:3c6b40e30e2c0c79863ba6ef5545d93350a040371213058099066ccd85b46aad

COPY --chmod=755 Deployment/renew-certificate.sh /usr/local/bin/renew-certificate
ENTRYPOINT ["/usr/local/bin/renew-certificate"]
