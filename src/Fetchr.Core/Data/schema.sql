-- Fetchr local database (SQLite 3.38+)
-- File: %LOCALAPPDATA%\Fetchr\fetchr.db  — access is the Windows user's own profile ACL.
-- IDs: UUID text generated in C#. Times: ISO-8601 UTC text. Sizes: bytes as INTEGER.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;

create table categories (
  id           text primary key,
  name         text not null,
  extensions   text not null default '',          -- space-separated: "zip rar 7z"
  save_folder  text not null,
  built_in     integer not null default 0 check (built_in in (0,1)),
  sort_order   integer not null default 0,
  created_at   text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at   text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);
create unique index categories_name_uq on categories (name);

create table queues (
  id            text primary key,
  name          text not null,
  max_parallel  integer not null default 3 check (max_parallel between 1 and 16),
  start_time    text,                               -- local wall-clock "HH:MM", not UTC (DST-safe)
  stop_time     text,
  days_of_week  integer not null default 0,         -- bitmask: Sun=1 .. Sat=64; 0 = manual only
  on_finish     text not null default 'none'
    check (on_finish in ('none','exit','sleep','hibernate','shutdown')),
  is_running    integer not null default 0 check (is_running in (0,1)),
  created_at    text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at    text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);
create unique index queues_name_uq on queues (name);

create table site_credentials (
  id             text primary key,
  host_pattern   text not null,                     -- "files.example.com" or "*.example.com"
  username       text not null,
  password_dpapi blob not null,                     -- ProtectedData, CurrentUser scope
  created_at     text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at     text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);
create unique index site_credentials_host_user_uq on site_credentials (host_pattern, username);

create table downloads (
  id               text primary key,
  url              text not null,
  final_url        text,                            -- after redirects
  referer          text,
  file_name        text not null,
  save_path        text not null,                   -- folder
  category_id      text references categories(id) on delete set null,
  queue_id         text references queues(id) on delete set null,
  queue_position   integer,
  credential_id    text references site_credentials(id) on delete set null,
  total_size       integer check (total_size is null or total_size >= 0),  -- null = unknown
  downloaded_bytes integer not null default 0 check (downloaded_bytes >= 0),
  status           text not null default 'queued'
    check (status in ('queued','downloading','paused','completed','error',
                      'extracting','extracted','extract_failed')),
  resumable        integer not null default 0 check (resumable in (0,1)),
  etag             text,
  last_modified    text,
  max_connections  integer not null default 8 check (max_connections between 1 and 32),
  speed_limit_bps  integer check (speed_limit_bps is null or speed_limit_bps > 0),
  expected_sha256  text check (expected_sha256 is null or length(expected_sha256) = 64),
  last_error       text,
  retry_count      integer not null default 0,
  description      text,
  is_removed       integer not null default 0 check (is_removed in (0,1)),  -- soft delete from list
  created_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  completed_at     text,
  check (total_size is null or downloaded_bytes <= total_size)
);
create index downloads_category_idx   on downloads (category_id);
create index downloads_queue_idx      on downloads (queue_id, queue_position);
create index downloads_credential_idx on downloads (credential_id);
create index downloads_status_idx     on downloads (status);
create index downloads_created_idx    on downloads (created_at desc);
-- Two live downloads may never write the same file.
create unique index downloads_target_uq
  on downloads (lower(save_path), lower(file_name)) where is_removed = 0;

create table segments (
  id            text primary key,
  download_id   text not null references downloads(id) on delete cascade,
  start_byte    integer not null check (start_byte >= 0),
  end_byte      integer not null,                  -- inclusive
  current_byte  integer not null,                  -- next byte to write
  status        text not null default 'pending'
    check (status in ('pending','active','done','error')),
  created_at    text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at    text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  check (end_byte >= start_byte),
  check (current_byte between start_byte and end_byte + 1)
);
create index segments_download_idx on segments (download_id, start_byte);
create unique index segments_start_uq on segments (download_id, start_byte);

create table extract_jobs (
  id               text primary key,
  download_id      text not null references downloads(id) on delete cascade,  -- first part
  archive_set_key  text not null,                  -- lower(folder) + '|' + base name without part suffix
  archive_format   text not null
    check (archive_format in ('zip','rar','7z','tar','gz','xz','bz2','iso','other')),
  parts_expected   integer,                        -- null = single file / unknown
  target_path      text not null,
  status           text not null default 'waiting_parts'
    check (status in ('waiting_parts','queued','running','needs_password','done','failed','cancelled')),
  delete_after     integer not null default 0 check (delete_after in (0,1)),  -- to Recycle Bin only
  progress_pct     integer not null default 0 check (progress_pct between 0 and 100),
  error            text,
  created_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at       text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  finished_at      text
);
create index extract_jobs_download_idx on extract_jobs (download_id);
create index extract_jobs_status_idx   on extract_jobs (status);
-- One multi-part archive = one job (excluding finished/cancelled history).
create unique index extract_jobs_set_uq
  on extract_jobs (archive_set_key) where status not in ('done','failed','cancelled');

create table archive_passwords (
  id             text primary key,
  label          text,
  password_dpapi blob not null,
  use_count      integer not null default 0,
  last_used_at   text,
  created_at     text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now')),
  updated_at     text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);
create index archive_passwords_use_idx on archive_passwords (use_count desc);

create table settings (
  key         text primary key,
  value       text not null,                       -- JSON
  updated_at  text not null default (strftime('%Y-%m-%dT%H:%M:%SZ','now'))
);

-- Keep updated_at honest.
create trigger downloads_touch after update on downloads for each row
  when new.updated_at = old.updated_at
  begin update downloads set updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now') where id = new.id; end;
create trigger segments_touch after update on segments for each row
  when new.updated_at = old.updated_at
  begin update segments set updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now') where id = new.id; end;
create trigger extract_jobs_touch after update on extract_jobs for each row
  when new.updated_at = old.updated_at
  begin update extract_jobs set updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now') where id = new.id; end;

-- Seed: built-in categories (folders resolved at first run under the user's Downloads).
insert into categories (id, name, extensions, save_folder, built_in, sort_order) values
  ('00000000-0000-0000-0000-000000000001','Compressed','zip rar 7z tar gz tgz xz bz2 iso 001','%DOWNLOADS%\Compressed',1,1),
  ('00000000-0000-0000-0000-000000000002','Programs','exe msi msix appx','%DOWNLOADS%\Programs',1,2),
  ('00000000-0000-0000-0000-000000000003','Video','mp4 mkv avi mov webm m4v','%DOWNLOADS%\Video',1,3),
  ('00000000-0000-0000-0000-000000000004','Music','mp3 m4a flac wav ogg aac','%DOWNLOADS%\Music',1,4),
  ('00000000-0000-0000-0000-000000000005','Documents','pdf doc docx xls xlsx ppt pptx txt epub','%DOWNLOADS%\Documents',1,5),
  ('00000000-0000-0000-0000-000000000006','General','','%DOWNLOADS%',1,99);

insert into queues (id, name, max_parallel) values
  ('00000000-0000-0000-0000-0000000000a1','Main queue',3);

insert into settings (key, value) values
  ('default_connections','8'),
  ('global_speed_limit_bps','null'),
  ('monitor_clipboard','true'),
  ('auto_extract','true'),
  ('extract_target','"subfolder"'),          -- same | subfolder | custom
  ('extract_custom_path','null'),
  ('delete_archive_after_extract','false'),
  ('extract_ratio_limit','200'),
  ('add_mark_of_the_web','true'),
  ('ui_language','"ar"'),
  ('theme','"system"');
