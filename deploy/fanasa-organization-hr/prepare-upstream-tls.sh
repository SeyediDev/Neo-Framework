#!/usr/bin/env bash
set -Eeuo pipefail
# Creates a separate, local integration CA. It never reads an existing CA private key
# or changes machine/browser trust stores. Activation remains a separate operation.
identity_dir=/etc/fanasa-organization-hr
edge_dir=/opt/fanasa-gateway/certs
test -d "$edge_dir"
getent group fanasa-access >/dev/null
install -d -m 0750 -o root -g fanasa-access "$identity_dir"
if test -e "$identity_dir/server.crt"; then
    test -s "$identity_dir/server.key"
    test -s "$edge_dir/organization-hr-ca.crt"
    test -s "$edge_dir/organization-hr-gateway.crt"
    test -s "$edge_dir/organization-hr-gateway.key"
    openssl x509 -in "$identity_dir/server.crt" -checkend 604800 -noout
    openssl x509 -in "$edge_dir/organization-hr-gateway.crt" -checkend 604800 -noout
    printf 'Existing dedicated HR certificates retained.\n'
    exit 0
fi
for file in ca.key ca.crt server.key gateway.key gateway.crt; do
    test ! -e "$identity_dir/$file" || { printf 'Partial HR TLS state; inspect without overwriting.\n' >&2; exit 1; }
done
umask 077
openssl req -new -x509 -newkey rsa:3072 -nodes -days 365 -sha256 \
    -subj '/CN=Fanasa Organization HR Development Integration CA' \
    -keyout "$identity_dir/ca.key" -out "$identity_dir/ca.crt" \
    -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
    -addext 'keyUsage=critical,keyCertSign,cRLSign' >/dev/null 2>&1
openssl req -new -newkey rsa:3072 -nodes -sha256 \
    -subj '/CN=organization-hr-upstream.fanasa.net.local' \
    -keyout "$identity_dir/server.key" -out "$identity_dir/server.csr" >/dev/null 2>&1
printf '%s\n' 'basicConstraints=critical,CA:FALSE' 'keyUsage=critical,digitalSignature,keyEncipherment' \
    'extendedKeyUsage=serverAuth' 'subjectAltName=DNS:organization-hr-upstream.fanasa.net.local' > "$identity_dir/server.ext"
openssl x509 -req -sha256 -days 90 -in "$identity_dir/server.csr" \
    -CA "$identity_dir/ca.crt" -CAkey "$identity_dir/ca.key" -CAcreateserial \
    -extfile "$identity_dir/server.ext" -out "$identity_dir/server.crt" >/dev/null 2>&1
openssl req -new -newkey rsa:3072 -nodes -sha256 \
    -subj '/CN=Fanasa HR Development Gateway' \
    -keyout "$identity_dir/gateway.key" -out "$identity_dir/gateway.csr" >/dev/null 2>&1
printf '%s\n' 'basicConstraints=critical,CA:FALSE' 'keyUsage=critical,digitalSignature' \
    'extendedKeyUsage=clientAuth' > "$identity_dir/gateway.ext"
openssl x509 -req -sha256 -days 90 -in "$identity_dir/gateway.csr" \
    -CA "$identity_dir/ca.crt" -CAkey "$identity_dir/ca.key" -CAserial "$identity_dir/ca.srl" \
    -extfile "$identity_dir/gateway.ext" -out "$identity_dir/gateway.crt" >/dev/null 2>&1
openssl verify -CAfile "$identity_dir/ca.crt" -purpose sslserver "$identity_dir/server.crt"
openssl verify -CAfile "$identity_dir/ca.crt" -purpose sslclient "$identity_dir/gateway.crt"
chown root:fanasa-access "$identity_dir/server.key" "$identity_dir/server.crt"
chmod 0640 "$identity_dir/server.key" "$identity_dir/server.crt"
install -m 0644 "$identity_dir/ca.crt" "$edge_dir/organization-hr-ca.crt"
install -m 0644 "$identity_dir/gateway.crt" "$edge_dir/organization-hr-gateway.crt"
install -m 0600 "$identity_dir/gateway.key" "$edge_dir/organization-hr-gateway.key"
printf 'Dedicated HR TLS certificates prepared; no route or service activated.\n'
