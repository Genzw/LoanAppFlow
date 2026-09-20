#!/bin/sh
set -eu
port="${PORT:-8080}"
case "$port" in
  ''|*[!0-9]*|0*) echo 'PORT must be an integer between 1024 and 65535.' >&2; exit 1 ;;
esac
if [ "${#port}" -gt 5 ] || [ "$port" -lt 1024 ] || [ "$port" -gt 65535 ]; then
  echo 'PORT must be an integer between 1024 and 65535.' >&2
  exit 1
fi
export ASPNETCORE_URLS="http://0.0.0.0:$port"
exec dotnet "$@"
