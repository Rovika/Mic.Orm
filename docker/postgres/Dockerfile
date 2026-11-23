# The PostGIS image ships no pgvector: `postgresql-17-pgvector` is a separate package, installed here on top of the
# same image the compose file used before, so the data volume and the versions of PostgreSQL and PostGIS stay as
# they were. Built by `docker compose build postgres`, or by `up` when the image is missing.
#
# The image is on Debian 11, which PostgreSQL's apt repository no longer serves; its archive does, and the package
# list of the image is pointed there. The version is pinned: the archive does not move.
FROM postgis/postgis:17-3.5

RUN echo "deb [ signed-by=/usr/local/share/keyrings/postgres.gpg.asc ]" \
         "https://apt-archive.postgresql.org/pub/repos/apt bullseye-pgdg-archive main" \
      > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-17-pgvector=0.8.6-1.pgdg11+1 \
 && apt-get clean && rm -rf /var/lib/apt/lists/*
