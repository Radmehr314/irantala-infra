#!/bin/sh
# Daily MongoDB backup, run by root's cron at 03:30 UTC. Keeps 7 days in /opt/irantala/backups.
set -e
. /opt/irantala/infra/.env
STAMP=$(date +%Y%m%d-%H%M)
DIR=/opt/irantala/backups
mkdir -p "$DIR"
docker exec mongo mongodump --quiet -u "$MONGO_ROOT_USER" -p "$MONGO_ROOT_PASSWORD" --authenticationDatabase admin --gzip --archive > "$DIR/.mongo-$STAMP.tmp"
mv "$DIR/.mongo-$STAMP.tmp" "$DIR/mongo-$STAMP.gz"
find "$DIR" -name 'mongo-*.gz' -mtime +7 -delete
