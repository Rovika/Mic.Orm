# The stock image ships no Full-Text Search: `mssql-server-fts` is a separate package, installed here on top of the
# same image the compose file used before. Built by `docker compose --profile heavy build sqlserver`, or by `up` when
# the image is missing; the package comes from Microsoft's apt repository for the image's Ubuntu (22.04, jammy).
#
# SQL Server 2025, for its vector type. The tag is pinned to the last one whose layers are gzip: from CU10-GDR1 on
# they are zstd, which Docker Engine reads from version 23 only.
FROM mcr.microsoft.com/mssql/server:2025-CU10-ubuntu-22.04

USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg2 ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft.gpg] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2025 jammy main" \
      > /etc/apt/sources.list.d/mssql-server-2025.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends mssql-server-fts \
 && apt-get clean && rm -rf /var/lib/apt/lists/*
USER mssql
