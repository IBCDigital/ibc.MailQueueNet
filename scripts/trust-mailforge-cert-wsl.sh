#!/usr/bin/env bash
set -euo pipefail

# Extracts the development TLS certificate from `mailqueuenet.pfx` and trusts it
# in the current WSL distro so that .NET gRPC clients can validate `https://localhost`.
#
# Usage:
#   ./scripts/trust-mailforge-cert-wsl.sh [path/to/mailqueuenet.pfx]
#
# Environment variables:
#   MAILQUEUENET_PFX_PASSWORD  Password for the PFX. If not set, you will be prompted.

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"

pfx_path="${1:-${repo_root}/Certs/mailqueuenet.pfx}"

if [[ ! -f "${pfx_path}" ]]; then
    echo "ERROR: PFX file not found: ${pfx_path}" >&2
    exit 1
fi

pfx_password="${MAILQUEUENET_PFX_PASSWORD:-}"
if [[ -z "${pfx_password}" ]]; then
    read -r -s -p "Enter PFX password for ${pfx_path}: " pfx_password
    echo
fi

if ! command -v openssl >/dev/null 2>&1; then
    echo "ERROR: openssl is required but was not found on PATH." >&2
    exit 1
fi

if ! command -v update-ca-certificates >/dev/null 2>&1; then
    echo "ERROR: update-ca-certificates was not found. This script expects a Debian/Ubuntu-style trust store." >&2
    exit 1
fi

dest_cert="/usr/local/share/ca-certificates/mailforge-localhost.crt"

tmp_dir="$(mktemp -d)"
trap 'rm -rf "${tmp_dir}"' EXIT

tmp_cert="${tmp_dir}/mailforge-localhost.crt"

# Extract leaf certificate (no private key) from the PFX.
openssl pkcs12 \
    -in "${pfx_path}" \
    -clcerts \
    -nokeys \
    -out "${tmp_cert}" \
    -passin "pass:${pfx_password}" \
    >/dev/null 2>&1

echo "Installing certificate into WSL trust store: ${dest_cert}"
sudo cp "${tmp_cert}" "${dest_cert}"

echo "Updating CA certificates..."
sudo update-ca-certificates

echo "Done. Restart MailForge and MailQueueNet.Service for changes to take effect."

