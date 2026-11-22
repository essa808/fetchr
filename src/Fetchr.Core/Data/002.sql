-- v2: archive states on downloads (waiting for other parts, needs a password) and the browser's
-- request headers (cookies, user agent), DPAPI-encrypted and cleared when the download finishes.
-- SQLite can't change a CHECK constraint in place, so the table is rebuilt (foreign keys are off
-- while this runs; Store checks them afterwards).

create table downloads_v2 (
  id               text primary key,
  url              text not null,
  final_url        text,
  referer          text,
  file_name        text not null,
  save_path        text not null,
  category_id      text references categories(id) on delete set null,
  queue_id         text references queues(id) on delete set null,
  queue_position   integer,
  credential_id    text references site_credentials(id) on delete set null,
  total_size       integer check (total_size is null or total_size >= 0),
  downloaded_bytes integer not null default 0 check (downloaded_bytes >= 0),
  status           text not null default 'queued'
    check (status in ('queued','downloading','paused','completed','error',
                      'extracting','extracted','extract_failed','waiting_parts','needs_password')),
  resumable        integer not null default 0 check (resumable in (0,1)),
  etag             text,
  last_modified    text,
  max_connections  integer not null default 8 check (max_connections between 1 and 32),
  speed_limit_bps  integer check (speed_limit_bps is null or speed_limit_bps > 0),
  expected_sha256  text check (expected_sha256 is null or length(expected_sha256) = 64),
  last_error       text,
  retry_count      integer not null default 0,
  description      text,
  is_removed       integer not null default 0 check (is_removed in (0,1)),
  request_headers_dpapi blob,
  created_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  completed_at     text,
  check (total_size is null or downloaded_bytes <= total_size)
);

insert into downloads_v2 (id, url, final_url, referer, file_name, save_path, category_id, queue_id, queue_position,
  credential_id, total_size, downloaded_bytes, status, resumable, etag, last_modified, max_connections,
  speed_limit_bps, expected_sha256, last_error, retry_count, description, is_removed, created_at, updated_at, completed_at)
select id, url, final_url, referer, file_name, save_path, category_id, queue_id, queue_position,
  credential_id, total_size, downloaded_bytes, status, resumable, etag, last_modified, max_connections,
  speed_limit_bps, expected_sha256, last_error, retry_count, description, is_removed, created_at, updated_at, completed_at
from downloads;

drop table downloads;
alter table downloads_v2 rename to downloads;

create index downloads_category_idx   on downloads (category_id);
create index downloads_queue_idx      on downloads (queue_id, queue_position);
create index downloads_credential_idx on downloads (credential_id);
create index downloads_status_idx     on downloads (status);
create index downloads_created_idx    on downloads (created_at desc);
create unique index downloads_target_uq
  on downloads (lower(save_path), lower(file_name)) where is_removed = 0;

create trigger downloads_touch after update on downloads for each row
  when new.updated_at = old.updated_at
  begin update downloads set updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now') where id = new.id; end;

insert into settings (key, value) values ('show_complete_dialog', 'true'), ('min_capture_size', '0')
  on conflict(key) do nothing;
