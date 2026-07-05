# RestoreGuard container: audit your lab without installing anything.
# The tool connects OUT over SSH, so mount your SSH config/keys read-only, and
# mount a working directory holding restoreguard.json (+ suppressions.json) —
# /work is the container's cwd, so the config is picked up exactly like on a host:
#
#   docker run --rm -v ~/.ssh:/root/.ssh:ro -v $PWD:/work restoreguard doctor
#   docker run --rm -v ~/.ssh:/root/.ssh:ro -v $PWD:/work restoreguard audit --json
#   # custom config name: ... restoreguard audit -c /work/other-lab.json
#
# Multi-arch: the build stage always runs natively and cross-publishes for the
# target platform (dotnet self-contained publish needs no target-native tooling).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN rid=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
    && dotnet publish src/RestoreGuard.Cli -c Release -r "$rid" --self-contained \
       -p:PublishSingleFile=true -o /out

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends openssh-client ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/RestoreGuard.Cli /usr/bin/restoreguard
WORKDIR /work
ENTRYPOINT ["restoreguard"]
CMD ["help"]
