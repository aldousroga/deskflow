-- Run this any time after users.sql (no dependency on assets.sql/tickets.sql). Backs the
-- Settings page: Departments, Locations, Ticket Categories, Asset Categories, Holidays,
-- Business Hours, and the generic app_settings key-value store (Company Info, Numbering
-- Format, Notifications, Email Configuration, Appearance/Theme).
--
-- These lookup tables are deliberately NOT foreign-keyed to tickets/assets - tickets and assets
-- store the chosen department/category/location as plain text, the same way tickets.department
-- already worked before this file existed. That keeps deleting or renaming a lookup entry from
-- ever breaking an existing ticket or asset record.
--
-- Safe to re-run: CREATE TABLE IF NOT EXISTS won't error if a table already exists, and
-- INSERT IGNORE won't error on seed rows that already exist (name/priority/day are unique keys).

USE deskflow;

-- ---------------------------------------------------------------------------
-- Simple name + active-flag lookup tables.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS departments (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  name        VARCHAR(100) NOT NULL UNIQUE,
  is_active   TINYINT(1) NOT NULL DEFAULT 1,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO departments (name) VALUES
  ('IT'), ('HR'), ('Finance'), ('Sales'), ('Operations'), ('Marketing');

CREATE TABLE IF NOT EXISTS locations (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  name        VARCHAR(100) NOT NULL UNIQUE,
  is_active   TINYINT(1) NOT NULL DEFAULT 1,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO locations (name) VALUES
  ('Headquarters'), ('Remote / Work From Home');

CREATE TABLE IF NOT EXISTS ticket_categories (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  name        VARCHAR(100) NOT NULL UNIQUE,
  is_active   TINYINT(1) NOT NULL DEFAULT 1,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Mirrors the options that were previously hardcoded into the ticket create form, so existing
-- tickets keep matching a real dropdown option after this table takes over.
INSERT IGNORE INTO ticket_categories (name) VALUES
  ('Hardware'), ('Software'), ('Network'), ('Account & Access'), ('Email'), ('Other');

CREATE TABLE IF NOT EXISTS asset_categories (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  name        VARCHAR(100) NOT NULL UNIQUE,
  is_active   TINYINT(1) NOT NULL DEFAULT 1,
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Mirrors the options that were previously hardcoded into the asset create form.
INSERT IGNORE INTO asset_categories (name) VALUES
  ('Laptop'), ('Desktop'), ('Monitor'), ('Phone'), ('Tablet'), ('Printer'), ('Server'), ('Networking'), ('Other');

-- ---------------------------------------------------------------------------
-- Holidays - name + date. Empty by default; admins add their own from Settings.
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS holidays (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  name          VARCHAR(150) NOT NULL,
  holiday_date  DATE NOT NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uq_holidays_name_date (name, holiday_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ---------------------------------------------------------------------------
-- Business hours - one fixed row per day of week (0 = Sunday .. 6 = Saturday, matching
-- JavaScript's Date.getDay(), since the frontend is what reads/renders this).
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS business_hours (
  day_of_week  TINYINT NOT NULL PRIMARY KEY,   -- 0=Sun, 1=Mon, ... 6=Sat
  is_open      TINYINT(1) NOT NULL DEFAULT 1,
  open_time    TIME NULL,
  close_time   TIME NULL,
  updated_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Default: Mon-Fri 9am-5pm open, Sat/Sun closed.
INSERT IGNORE INTO business_hours (day_of_week, is_open, open_time, close_time) VALUES
  (0, 0, NULL,     NULL),
  (1, 1, '09:00:00', '17:00:00'),
  (2, 1, '09:00:00', '17:00:00'),
  (3, 1, '09:00:00', '17:00:00'),
  (4, 1, '09:00:00', '17:00:00'),
  (5, 1, '09:00:00', '17:00:00'),
  (6, 0, NULL,     NULL);

-- ---------------------------------------------------------------------------
-- Generic key-value settings store - one JSON blob per settings section. The backend treats
-- the value as an opaque JSON document for every key except "email" (where it strips the
-- stored password out of every response - see Program.cs).
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS app_settings (
  setting_key    VARCHAR(50) NOT NULL PRIMARY KEY,
  setting_value  JSON NOT NULL,
  updated_at     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

INSERT IGNORE INTO app_settings (setting_key, setting_value) VALUES
  ('company', JSON_OBJECT('name', 'DeskFlow', 'supportEmail', '', 'phone', '', 'website', '', 'address', '')),
  ('numbering', JSON_OBJECT('prefix', 'TK-', 'startAt', 1000)),
  ('notifications', JSON_OBJECT('newTicketEmail', false, 'slaBreachEmail', false, 'dailyDigest', false)),
  ('email', JSON_OBJECT('smtpHost', '', 'smtpPort', 587, 'smtpUser', '', 'password', '', 'fromAddress', '', 'useTls', true)),
  ('theme', JSON_OBJECT('textColor', '#111827', 'backgroundColor', '#f5f7fb'));
