# The image Anthropic's OSS Scanner audits pgNimbus in (https://github.com/anthropics/oss-scanner).
#
# The scanner builds this with network access and then audits the image with none, so everything a build, a test run
# or a reproducer needs is fetched here: every NuGet package the solution restores, PostgreSQL 17 (what CI tests
# against) and an SSH server for the tunnel's live tests. `. .oss-scanner/services.sh` starts both servers and exports
# the variables that switch the live tests on. threat_model.md beside this file tells the scanner what to look at.
#
# Nothing in CI builds this. To check it after a change, from the repository root (wslc works the same as docker):
#
#   docker build -f .oss-scanner/Dockerfile -t pgnimbus-scan .
#   docker run --rm -it --network=none pgnimbus-scan bash
#   . .oss-scanner/services.sh && dotnet test --project tests/PgNimbus.Core.Tests -c Release --no-build
#
# The SDK tag floats on purpose: global.json takes any 10.0 SDK from 10.0.100 up, and this image ships nowhere.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# PostgreSQL from the PGDG repository, since Ubuntu 24.04's own is 16. libfontconfig1 is what Skia needs for the
# headless UI tests and the screenshot harness; procps is for pgrep in services.sh.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg postgresql-common \
      openssh-server libfontconfig1 procps \
 && /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y \
 && apt-get install -y --no-install-recommends postgresql-17 \
 && rm -rf /var/lib/apt/lists/*

# Throwaway passwords the live tests log in with, as in CI. The audit runs with no network at all.
RUN pg_ctlcluster 17 main start \
 && su postgres -c "psql -q -c \"ALTER USER postgres PASSWORD 'postgres'\"" \
 && pg_ctlcluster 17 main stop \
 && useradd --create-home --shell /bin/bash tunnel \
 && echo 'tunnel:tunnel' | chpasswd

COPY . /src
WORKDIR /src

# Every project in the solution: the app, the tests, the benchmark and the tools. That leaves every package an offline
# rebuild needs in the NuGet cache. Release, as CI builds it (portable pdbs are on by default).
RUN dotnet build PgNimbus.slnx -c Release

# The two suites CI runs, with the live tests on. A failing test is reported, not fatal: the image is still useful.
RUN . .oss-scanner/services.sh \
 && (dotnet test --project tests/PgNimbus.Core.Tests -c Release --no-build \
       || echo "WARNING: PgNimbus.Core.Tests reported failures") \
 && (dotnet test --project tests/PgNimbus.App.Tests -c Release --no-build \
       || echo "WARNING: PgNimbus.App.Tests reported failures"); \
    pg_ctlcluster 17 main stop || true
