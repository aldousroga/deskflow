-- Run this AFTER sla_policies.sql, against your existing `deskflow` database (the one that
-- already has users.sql/assets.sql/tickets.sql - and probably tickets_seed.sql - applied).
--
-- It upgrades the `tickets` table from the old single "resolve by" deadline to the full two-clock
-- SLA model: a First Response deadline and a Resolution deadline, tracked separately, with the
-- ability to pause both while a ticket is On Hold.
--
-- Safe to run even if you have zero tickets yet - the backfill steps just won't match any rows.
--
-- SAFE TO RE-RUN, start to finish, no matter how far a previous attempt got: every ALTER below
-- checks whether its column already exists before touching anything, so this won't error out with
-- "column already exists" or "unknown column" partway through a second run.

USE deskflow;

-- MySQL Workbench's default "Safe Updates" preference blocks any UPDATE whose WHERE clause
-- doesn't reference a primary/unique key - which every backfill step below intentionally doesn't
-- (they filter by status/timestamp columns, not by id). That's fine here: each UPDATE is guarded
-- by an IS NULL check, so re-running this file never touches an already-backfilled row twice.
-- This turns Safe Updates off for just this session and restores it at the very end.
SET SQL_SAFE_UPDATES = 0;

-- 1. Rename the old single deadline column to resolution_due_at - the "time to first response"
--    and pause/resume columns get added as separate, individually-guarded steps below so a
--    partially-completed previous run can't cause an "unknown column" or "duplicate column" error.
SET @needsRename = (
  SELECT COUNT(*) FROM information_schema.COLUMNS
  WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'due_at'
);
SET @sql = IF(@needsRename > 0,
  'ALTER TABLE tickets CHANGE COLUMN due_at resolution_due_at DATETIME NULL',
  'SELECT "resolution_due_at already exists - skipping rename" AS note');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- 2. Add every other SLA column the engine needs - each one only if it isn't there yet.
--    response_due_at / first_responded_at / response_met  -> the "time to first response" clock
--    resolution_met                                       -> whether the resolution clock was met
--    on_hold_since / total_paused_minutes                 -> pause/resume bookkeeping while On Hold

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'response_due_at');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN response_due_at DATETIME NULL', 'SELECT "response_due_at already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'first_responded_at');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN first_responded_at DATETIME NULL', 'SELECT "first_responded_at already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'response_met');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN response_met TINYINT(1) NULL', 'SELECT "response_met already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'resolution_met');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN resolution_met TINYINT(1) NULL', 'SELECT "resolution_met already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'on_hold_since');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN on_hold_since DATETIME NULL', 'SELECT "on_hold_since already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'tickets' AND column_name = 'total_paused_minutes');
SET @sql = IF(@missing = 0, 'ALTER TABLE tickets ADD COLUMN total_paused_minutes INT NOT NULL DEFAULT 0', 'SELECT "total_paused_minutes already exists" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- 3. Backfill response_due_at / resolution_due_at for any tickets that predate this migration,
--    based on each ticket's priority and the SLA policy for that priority.
UPDATE tickets t
JOIN sla_policies p ON p.priority = t.priority
SET
  t.response_due_at   = DATE_ADD(t.created_at, INTERVAL p.response_minutes MINUTE),
  t.resolution_due_at = DATE_ADD(t.created_at, INTERVAL p.resolution_minutes MINUTE)
WHERE t.response_due_at IS NULL;

-- 4. Backfill first_responded_at/response_met for tickets that are already past "New" - we don't
--    know the exact historical moment they were first touched, so this approximates it as 10
--    minutes after filing, which is enough to produce a believable compliance mix in reports.
UPDATE tickets
SET
  first_responded_at = DATE_ADD(created_at, INTERVAL 10 MINUTE),
  response_met = (DATE_ADD(created_at, INTERVAL 10 MINUTE) <= response_due_at)
WHERE status <> 'new' AND first_responded_at IS NULL;

-- 5. Backfill resolution_met for tickets that already have a resolved_at timestamp.
UPDATE tickets
SET resolution_met = (resolved_at <= resolution_due_at)
WHERE resolved_at IS NOT NULL AND resolution_met IS NULL;

-- 6. Any ticket that's currently sitting On Hold needs its pause clock started, or its SLA
--    countdown will look like it's still running instead of paused.
UPDATE tickets
SET on_hold_since = NOW()
WHERE status = 'on_hold' AND on_hold_since IS NULL;

-- Restore Workbench's default preference now that we're done.
SET SQL_SAFE_UPDATES = 1;
