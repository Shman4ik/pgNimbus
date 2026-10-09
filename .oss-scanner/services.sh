#!/bin/sh
# Starts the two servers pgNimbus's live tests talk to inside the OSS Scanner image (see Dockerfile beside this file),
# and exports the variables that turn those tests on. Source it, so the exports stay in your shell:
#
#   . .oss-scanner/services.sh
#
# Running it again is harmless: a server that is already up is left alone. POSIX sh, because the Dockerfile's RUN
# steps source it from /bin/sh.

if ! pg_ctlcluster 17 main status >/dev/null 2>&1; then
    pg_ctlcluster 17 main start
fi

if ! pgrep -x sshd >/dev/null 2>&1; then
    mkdir -p /run/sshd
    /usr/sbin/sshd
fi

# PostgreSQL 17 on 5432, user and password postgres (what CI's service container uses).
export PGNIMBUS_TEST_CONN="Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres"
# sshd on 22 with password auth and TCP forwarding, for SshTunnelHostKeyLiveTests; Target is the Postgres above.
export PGNIMBUS_TEST_SSH="Host=127.0.0.1;Port=22;Username=tunnel;Password=tunnel;Target=127.0.0.1:5432"
