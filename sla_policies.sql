-- Run this in your `deskflow` database any time after assets.sql (no dependency on tickets.sql -
-- this table stands alone, tickets just look it up by priority at runtime).
--
-- These are the rules administrators can tune later from Settings -> SLA Rules in the app -
-- this file just seeds the defaults you gave us.
--
-- Safe to re-run: CREATE TABLE IF NOT EXISTS won't error if it's already there, and INSERT IGNORE
-- won't error on rows that already exist (priority is the primary key).

USE deskflow;

CREATE TABLE IF NOT EXISTS sla_policies (
  priority            ENUM('low', 'medium', 'high', 'critical') NOT NULL PRIMARY KEY,
  response_minutes    INT NOT NULL,   -- time to FIRST RESPONSE, e.g. 15 for Critical
  resolution_minutes  INT NOT NULL,   -- time to RESOLUTION, e.g. 120 for Critical (2 hours)
  updated_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO sla_policies (priority, response_minutes, resolution_minutes) VALUES
  ('critical', 15,  120),   -- 15 min response / 2 hours resolution
  ('high',     30,  240),   -- 30 min response / 4 hours resolution
  ('medium',   120, 480),   -- 2 hour response  / 8 hours resolution
  ('low',      240, 1440);  -- 4 hour response  / 24 hours resolution
