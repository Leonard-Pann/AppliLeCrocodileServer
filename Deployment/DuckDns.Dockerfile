FROM curlimages/curl:8.17.0
COPY --chmod=755 Deployment/update-duckdns.sh /usr/local/bin/update-duckdns
ENTRYPOINT ["/usr/local/bin/update-duckdns"]
