-- Run this AFTER assets.sql (and after sla_migration.sql if you've already applied that), against
-- your existing `deskflow` database.
--
-- Adds two nullable columns to `assets` - department and location - so assets can be tagged the
-- same way tickets already are. This is what makes the "Assets by Department" and "Assets by
-- Location" reports on the Reports page real instead of "not tracked yet".
--
-- SAFE TO RE-RUN, start to finish: each ALTER checks whether its column already exists first.

USE deskflow;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'assets' AND column_name = 'department');
SET @sql = IF(@missing = 0, 'ALTER TABLE assets ADD COLUMN department VARCHAR(100) NULL AFTER assigned_to_id', 'SELECT "department already exists on assets - skipping" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @missing = (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE table_schema = DATABASE() AND table_name = 'assets' AND column_name = 'location');
SET @sql = IF(@missing = 0, 'ALTER TABLE assets ADD COLUMN location VARCHAR(100) NULL AFTER department', 'SELECT "location already exists on assets - skipping" AS note');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
