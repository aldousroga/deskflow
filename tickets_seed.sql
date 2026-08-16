-- Sample data for the `tickets` table - 50 rows: 25 linked to a real asset (hardware issues),
-- 25 with no asset link (software/network/account/email/other issues).
--
-- Requires: users.sql, assets.sql, tickets.sql, sla_policies.sql, and sla_migration.sql already
-- run (this needs the new SLA columns - response_due_at, first_responded_at, response_met,
-- resolution_met, on_hold_since - to exist), AND assets_seed.sql already run (the 25 hardware
-- tickets link to those 10 sample assets by asset_tag via subquery, so the exact numeric asset id
-- doesn't matter).
--
-- requester_id and assigned_technician_id are both set to 1, which is your seeded admin account
-- (guaranteed to exist). Swap these for real user ids once you've got more accounts - the app
-- itself never cares who's in these columns as long as the id exists in `users`.
--
-- Every timestamp here (created_at, first_responded_at, resolved_at, closed_at, on_hold_since) is
-- written as an offset from NOW(), deliberately chosen against the SLA windows in sla_policies so
-- the seeded set exercises every state the SLA engine can be in: fresh/healthy tickets, tickets
-- closing in on their deadline ("at risk"), tickets already past due ("breached") in both the
-- response and resolution phases, paused tickets with a frozen countdown, and resolved/closed
-- tickets that landed on both sides of "met" vs. "missed" - so Settings, the ticket list/detail
-- countdown, and the Reports compliance page all have something real to show right after seeding.
-- response_due_at/resolution_due_at aren't set directly - the backfill step at the bottom computes
-- them from created_at + the live sla_policies rules, exactly the way the app itself does.
--
-- Safe to re-run: it clears any previous run of this same sample set (by subject + created
-- window) first... actually simplest and safest is to just clear ALL tickets before reseeding,
-- since this is meant to be your whole sample ticket set. Comment out the DELETE below if you
-- want to keep tickets you've already created through the app.

USE deskflow;

-- Same Safe Updates caveat as sla_migration.sql - the DELETE/UPDATE statements below filter on
-- ticket_number/status/timestamp columns rather than id, which MySQL Workbench's default "Safe
-- Updates" preference blocks. Turned off for this session only, restored at the very end.
SET SQL_SAFE_UPDATES = 0;

DELETE FROM tickets WHERE requester_id = 1 AND ticket_number IS NULL;
-- (matches only unfinished rows from a previous failed run of this script - see the UPDATE
-- at the bottom that assigns ticket_number. Tickets created through the app always have a
-- ticket_number, so this won't touch real data.)

INSERT INTO tickets
  (subject, description, category, subcategory, priority, status,
   requester_id, assigned_technician_id, department, asset_id, resolution_notes,
   created_at, first_responded_at, resolved_at, closed_at, on_hold_since)
VALUES

-- ============ 25 HARDWARE tickets - each linked to a real asset ============

-- Resolution SLA at risk (10 min left of a 4h window)
('Laptop overheating and shutting down', 'Fan runs loud then the laptop powers off after ~20 minutes of use.', 'Hardware', 'Laptop', 'high', 'in_progress',
  1, 1, 'IT', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1001'), NULL,
  NOW() - INTERVAL 230 MINUTE, NOW() - INTERVAL 210 MINUTE, NULL, NULL, NULL),

-- Response SLA healthy, freshly filed
('Keyboard keys not registering', 'The E and R keys need to be pressed hard multiple times to type.', 'Hardware', 'Laptop', 'medium', 'new',
  1, NULL, 'IT', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1001'), NULL,
  NOW() - INTERVAL 15 MINUTE, NULL, NULL, NULL, NULL),

-- Both clocks met
('Battery drains within an hour', 'Fully charged battery is down to 10% after about 50 minutes unplugged.', 'Hardware', 'Laptop', 'low', 'resolved',
  1, 1, 'IT', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1001'), 'Replaced battery - old one was swollen and past its rated cycle count.',
  NOW() - INTERVAL 30 HOUR, NOW() - INTERVAL 29 HOUR, NOW() - INTERVAL 10 HOUR, NULL, NULL),

-- Resolution SLA healthy
('Trackpad unresponsive', 'Trackpad stops responding randomly, external mouse works fine as a workaround.', 'Hardware', 'Laptop', 'medium', 'assigned',
  1, 1, 'Engineering', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1002'), NULL,
  NOW() - INTERVAL 120 MINUTE, NOW() - INTERVAL 70 MINUTE, NULL, NULL, NULL),

-- Resolution SLA breached (still open)
('Screen flickering intermittently', 'Screen flickers for a few seconds every 10-15 minutes, worse on battery power.', 'Hardware', 'Laptop', 'high', 'in_progress',
  1, 1, 'Engineering', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1002'), NULL,
  NOW() - INTERVAL 300 MINUTE, NOW() - INTERVAL 275 MINUTE, NULL, NULL, NULL),

-- Response SLA at risk (critical, ~2 min left of a 15 min window)
('Laptop won''t power on', 'No lights, no fan noise, nothing happens when holding the power button.', 'Hardware', 'Laptop', 'critical', 'new',
  1, NULL, 'Engineering', (SELECT id FROM assets WHERE asset_tag = 'LAPTOP-1002'), NULL,
  NOW() - INTERVAL 13 MINUTE, NULL, NULL, NULL, NULL),

-- On Hold - resolution clock paused with time still left
('Desktop randomly restarts', 'Restarts without warning, 3-4 times a day, no error message shown.', 'Hardware', 'Desktop', 'high', 'on_hold',
  1, 1, 'Operations', (SELECT id FROM assets WHERE asset_tag = 'DESK-2001'), NULL,
  NOW() - INTERVAL 180 MINUTE, NOW() - INTERVAL 160 MINUTE, NULL, NULL, NOW() - INTERVAL 30 MINUTE),

-- Both clocks missed
('USB ports not working', 'None of the 4 rear USB ports detect any device, front ports work fine.', 'Hardware', 'Desktop', 'low', 'closed',
  1, 1, 'Operations', (SELECT id FROM assets WHERE asset_tag = 'DESK-2001'), 'Replaced the rear USB hub board - internal connector had come loose.',
  NOW() - INTERVAL 3 DAY, NOW() - INTERVAL 4020 MINUTE, NOW() - INTERVAL 2320 MINUTE, NOW() - INTERVAL 2260 MINUTE, NULL),

-- Response SLA healthy, freshly filed
('Monitor has dead pixels', 'Noticed a small cluster of dead pixels in the top-left corner.', 'Hardware', 'Monitor', 'low', 'new',
  1, NULL, 'Sales', (SELECT id FROM assets WHERE asset_tag = 'MON-3001'), NULL,
  NOW() - INTERVAL 20 MINUTE, NULL, NULL, NULL, NULL),

-- Both clocks met
('Monitor won''t turn on', 'Power light doesn''t come on at all, tried a different outlet already.', 'Hardware', 'Monitor', 'medium', 'resolved',
  1, 1, 'Sales', (SELECT id FROM assets WHERE asset_tag = 'MON-3001'), 'Power cable was faulty - swapped for a spare and it powered right up.',
  NOW() - INTERVAL 600 MINUTE, NOW() - INTERVAL 540 MINUTE, NOW() - INTERVAL 200 MINUTE, NULL, NULL),

-- Resolution SLA healthy
('Screen color looks distorted', 'Colors have a strong red tint compared to other monitors on the same desk.', 'Hardware', 'Monitor', 'medium', 'assigned',
  1, 1, 'Finance', (SELECT id FROM assets WHERE asset_tag = 'MON-3002'), NULL,
  NOW() - INTERVAL 60 MINUTE, NOW() - INTERVAL 20 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('Monitor stand is broken', 'Stand no longer holds the tilt angle, screen keeps drooping forward.', 'Hardware', 'Monitor', 'low', 'closed',
  1, 1, 'Finance', (SELECT id FROM assets WHERE asset_tag = 'MON-3002'), 'Replaced the stand assembly with a spare from stock.',
  NOW() - INTERVAL 1200 MINUTE, NOW() - INTERVAL 1050 MINUTE, NOW() - INTERVAL 200 MINUTE, NOW() - INTERVAL 140 MINUTE, NULL),

-- Resolution SLA at risk
('Flickering at high refresh rate', 'Flickers noticeably when refresh rate is set above 100Hz.', 'Hardware', 'Monitor', 'medium', 'in_progress',
  1, 1, 'Finance', (SELECT id FROM assets WHERE asset_tag = 'MON-3002'), NULL,
  NOW() - INTERVAL 420 MINUTE, NOW() - INTERVAL 330 MINUTE, NULL, NULL, NULL),

-- Response SLA healthy, freshly filed
('Phone screen cracked further', 'Existing crack has spread and now the touch response is affected.', 'Hardware', 'Phone', 'high', 'new',
  1, NULL, 'Sales', (SELECT id FROM assets WHERE asset_tag = 'PHN-4001'), NULL,
  NOW() - INTERVAL 5 MINUTE, NULL, NULL, NULL, NULL),

-- Resolution SLA breached (still open)
('Phone battery not charging', 'Plugged in overnight and battery percentage didn''t move at all.', 'Hardware', 'Phone', 'high', 'in_progress',
  1, 1, 'Sales', (SELECT id FROM assets WHERE asset_tag = 'PHN-4001'), NULL,
  NOW() - INTERVAL 360 MINUTE, NOW() - INTERVAL 345 MINUTE, NULL, NULL, NULL),

-- Resolution SLA missed
('Camera not focusing', 'Photos come out consistently blurry regardless of lighting or distance.', 'Hardware', 'Phone', 'low', 'resolved',
  1, 1, 'Sales', (SELECT id FROM assets WHERE asset_tag = 'PHN-4001'), 'A pending iOS update included a camera driver fix - issue resolved after updating.',
  NOW() - INTERVAL 2 DAY, NOW() - INTERVAL 2680 MINUTE, NOW() - INTERVAL 880 MINUTE, NULL, NULL),

-- On Hold - resolution clock paused with time still left
('Printer jamming constantly', 'Paper jams on almost every print job from tray 2 specifically.', 'Hardware', 'Printer', 'medium', 'on_hold',
  1, 1, 'Operations', (SELECT id FROM assets WHERE asset_tag = 'PRN-5001'), NULL,
  NOW() - INTERVAL 300 MINUTE, NOW() - INTERVAL 240 MINUTE, NULL, NULL, NOW() - INTERVAL 45 MINUTE),

-- Response SLA healthy, freshly filed
('Print quality is streaky', 'Vertical streaks appear on every page, replaced the toner already.', 'Hardware', 'Printer', 'low', 'new',
  1, NULL, 'Operations', (SELECT id FROM assets WHERE asset_tag = 'PRN-5001'), NULL,
  NOW() - INTERVAL 8 MINUTE, NULL, NULL, NULL, NULL),

-- Both clocks missed
('Printer offline, won''t reconnect', 'Shows offline in the print queue, network cable and restart didn''t help.', 'Hardware', 'Printer', 'high', 'closed',
  1, 1, 'Operations', (SELECT id FROM assets WHERE asset_tag = 'PRN-5001'), 'Replaced the printer''s network interface card - old one had failed.',
  NOW() - INTERVAL 2 DAY, NOW() - INTERVAL 2480 MINUTE, NOW() - INTERVAL 380 MINUTE, NOW() - INTERVAL 280 MINUTE, NULL),

-- Resolution SLA breached (still open, critical)
('Server running out of disk space', 'Down to 4% free space on the main array, backups are starting to fail.', 'Hardware', 'Server', 'critical', 'in_progress',
  1, 1, 'IT', (SELECT id FROM assets WHERE asset_tag = 'SRV-6001'), NULL,
  NOW() - INTERVAL 180 MINUTE, NOW() - INTERVAL 170 MINUTE, NULL, NULL, NULL),

-- Resolution SLA healthy
('Server fan making loud noise', 'One of the rack fans is grinding loudly, others sound normal.', 'Hardware', 'Server', 'medium', 'assigned',
  1, 1, 'IT', (SELECT id FROM assets WHERE asset_tag = 'SRV-6001'), NULL,
  NOW() - INTERVAL 60 MINUTE, NOW() - INTERVAL 15 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('Switch port not passing traffic', 'Port 14 shows link but no traffic gets through to that workstation.', 'Hardware', 'Networking', 'high', 'resolved',
  1, 1, 'IT', (SELECT id FROM assets WHERE asset_tag = 'NET-7001'), 'Port module had failed - moved the connection to a spare port and flagged the module for replacement.',
  NOW() - INTERVAL 300 MINUTE, NOW() - INTERVAL 280 MINUTE, NOW() - INTERVAL 100 MINUTE, NULL, NULL),

-- Response SLA at risk
('Intermittent packet loss on switch', 'Users on that switch report dropped video calls a few times a day.', 'Hardware', 'Networking', 'medium', 'new',
  1, NULL, 'IT', (SELECT id FROM assets WHERE asset_tag = 'NET-7001'), NULL,
  NOW() - INTERVAL 105 MINUTE, NULL, NULL, NULL, NULL),

-- Response SLA breached (still New, nobody's grabbed it yet)
('Tablet battery swelling', 'Back cover is visibly bulging - device should be pulled from use immediately.', 'Hardware', 'Tablet', 'critical', 'new',
  1, NULL, 'Facilities', (SELECT id FROM assets WHERE asset_tag = 'TAB-8001'), NULL,
  NOW() - INTERVAL 25 MINUTE, NULL, NULL, NULL, NULL),

-- Both clocks met
('Touch screen unresponsive in corner', 'Bottom-right corner of the screen doesn''t register touches anymore.', 'Hardware', 'Tablet', 'low', 'closed',
  1, 1, 'Facilities', (SELECT id FROM assets WHERE asset_tag = 'TAB-8001'), 'Device is past its useful life given the battery issue too - retired and replaced with a new unit.',
  NOW() - INTERVAL 1200 MINUTE, NOW() - INTERVAL 1100 MINUTE, NOW() - INTERVAL 100 MINUTE, NOW() - INTERVAL 40 MINUTE, NULL),

-- ============ 25 NON-HARDWARE tickets - no asset link ============

-- Response SLA healthy, freshly filed
('Cannot install Adobe Photoshop', 'Installer fails at 80% with a generic error code.', 'Software', 'Installation', 'medium', 'new',
  1, NULL, 'Marketing', NULL, NULL,
  NOW() - INTERVAL 10 MINUTE, NULL, NULL, NULL, NULL),

-- Resolution SLA healthy
('Excel crashes when opening large files', 'Any spreadsheet over ~20MB crashes Excel within a few seconds of opening.', 'Software', 'Office Suite', 'medium', 'in_progress',
  1, 1, 'Finance', NULL, NULL,
  NOW() - INTERVAL 120 MINUTE, NOW() - INTERVAL 40 MINUTE, NULL, NULL, NULL),

-- Resolution SLA at risk
('Software license has expired', 'Design tool shows a license expired banner and blocks exporting files.', 'Software', 'Licensing', 'high', 'assigned',
  1, 1, 'Marketing', NULL, NULL,
  NOW() - INTERVAL 220 MINUTE, NOW() - INTERVAL 195 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('App update broke our workflow', 'The latest update moved the export button and removed batch processing.', 'Software', 'Updates', 'low', 'resolved',
  1, 1, 'Operations', NULL, 'Rolled the app back to the previous version until the vendor restores batch processing.',
  NOW() - INTERVAL 1200 MINUTE, NOW() - INTERVAL 1050 MINUTE, NOW() - INTERVAL 50 MINUTE, NULL, NULL),

-- Response SLA healthy, freshly filed
('Need Zoom installed on new workstation', 'New hire starts Monday and needs Zoom set up before then.', 'Software', 'Installation', 'low', 'new',
  1, NULL, 'HR', NULL, NULL,
  NOW() - INTERVAL 30 MINUTE, NULL, NULL, NULL, NULL),

-- Resolution SLA breached (still open)
('VPN keeps disconnecting', 'Drops the connection every 10-15 minutes, has to be manually reconnected.', 'Network', 'VPN', 'high', 'in_progress',
  1, 1, 'Sales', NULL, NULL,
  NOW() - INTERVAL 300 MINUTE, NOW() - INTERVAL 280 MINUTE, NULL, NULL, NULL),

-- Response SLA healthy, freshly filed
('Cannot connect to office WiFi', 'Laptop sees the network but authentication fails every time.', 'Network', 'WiFi', 'medium', 'new',
  1, NULL, 'Sales', NULL, NULL,
  NOW() - INTERVAL 25 MINUTE, NULL, NULL, NULL, NULL),

-- On Hold - resolution clock paused with time still left
('Slow internet speeds in Marketing', 'Whole team reports pages taking 10+ seconds to load since this morning.', 'Network', 'Bandwidth', 'medium', 'on_hold',
  1, 1, 'Marketing', NULL, NULL,
  NOW() - INTERVAL 240 MINUTE, NOW() - INTERVAL 180 MINUTE, NULL, NULL, NOW() - INTERVAL 20 MINUTE),

-- Resolution SLA healthy
('Shared drive not accessible', 'Getting "access denied" on the Finance shared drive since the weekend.', 'Network', 'File Shares', 'high', 'assigned',
  1, 1, 'Finance', NULL, NULL,
  NOW() - INTERVAL 60 MINUTE, NOW() - INTERVAL 35 MINUTE, NULL, NULL, NULL),

-- Resolution SLA breached (still open, critical)
('DNS resolution failing intermittently', 'Internal sites randomly fail to resolve, external sites are unaffected.', 'Network', 'DNS', 'critical', 'in_progress',
  1, 1, 'IT', NULL, NULL,
  NOW() - INTERVAL 150 MINUTE, NOW() - INTERVAL 140 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('Locked out of account after failed logins', 'Account locked after mistyping the password a few times.', 'Account & Access', 'Account Lockout', 'high', 'resolved',
  1, 1, 'HR', NULL, 'Unlocked the account and had the user reset their password via the self-service portal.',
  NOW() - INTERVAL 180 MINUTE, NOW() - INTERVAL 165 MINUTE, NOW() - INTERVAL 30 MINUTE, NULL, NULL),

-- Both clocks met
('Need access to Finance shared folder', 'Was just moved into the Finance team and needs folder access set up.', 'Account & Access', 'Permissions', 'medium', 'closed',
  1, 1, 'Finance', NULL, 'Added the user to the Finance security group - access confirmed working.',
  NOW() - INTERVAL 600 MINUTE, NOW() - INTERVAL 510 MINUTE, NOW() - INTERVAL 200 MINUTE, NOW() - INTERVAL 140 MINUTE, NULL),

-- Response SLA at risk (critical, ~3 min left of a 15 min window)
('Two-factor authentication not working', 'Authenticator app codes are rejected as invalid every time.', 'Account & Access', '2FA', 'critical', 'new',
  1, NULL, 'Legal', NULL, NULL,
  NOW() - INTERVAL 12 MINUTE, NULL, NULL, NULL, NULL),

-- Resolution SLA healthy
('New employee needs account setup', 'Starting next Monday, needs email, VPN, and CRM accounts provisioned.', 'Account & Access', 'Onboarding', 'medium', 'assigned',
  1, 1, 'HR', NULL, NULL,
  NOW() - INTERVAL 60 MINUTE, NOW() - INTERVAL 15 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('Password reset request', 'Forgot password and the self-service reset link isn''t arriving by email.', 'Account & Access', 'Password Reset', 'low', 'resolved',
  1, 1, 'Support', NULL, 'Reset the password manually via the admin console since the reset email was landing in spam.',
  NOW() - INTERVAL 180 MINUTE, NOW() - INTERVAL 150 MINUTE, NOW() - INTERVAL 30 MINUTE, NULL, NULL),

-- Resolution SLA at risk
('Cannot access CRM system', 'Gets a 403 error page immediately after logging in to the CRM.', 'Account & Access', 'Permissions', 'high', 'in_progress',
  1, 1, 'Sales', NULL, NULL,
  NOW() - INTERVAL 220 MINUTE, NOW() - INTERVAL 200 MINUTE, NULL, NULL, NULL),

-- Response SLA healthy, freshly filed
('Outlook not syncing emails', 'New emails aren''t showing up unless Outlook is fully restarted.', 'Email', 'Sync', 'medium', 'new',
  1, NULL, 'Operations', NULL, NULL,
  NOW() - INTERVAL 18 MINUTE, NULL, NULL, NULL, NULL),

-- Both clocks met
('Emails going to spam folder', 'Customer replies keep landing in spam instead of the inbox.', 'Email', 'Spam Filter', 'low', 'closed',
  1, 1, 'Sales', NULL, 'Whitelisted the affected sender domain in the spam filter settings.',
  NOW() - INTERVAL 1200 MINUTE, NOW() - INTERVAL 1000 MINUTE, NOW() - INTERVAL 100 MINUTE, NOW() - INTERVAL 20 MINUTE, NULL),

-- Resolution SLA healthy
('Cannot send emails with large attachments', 'Anything over 10MB bounces back with a size limit error.', 'Email', 'Attachments', 'medium', 'assigned',
  1, 1, 'Marketing', NULL, NULL,
  NOW() - INTERVAL 90 MINUTE, NOW() - INTERVAL 30 MINUTE, NULL, NULL, NULL),

-- Resolution SLA breached (still open, critical security incident)
('Email account compromised - suspicious activity', 'Sent items show emails the user never sent, several this morning.', 'Email', 'Security', 'critical', 'in_progress',
  1, 1, 'Legal', NULL, NULL,
  NOW() - INTERVAL 150 MINUTE, NOW() - INTERVAL 142 MINUTE, NULL, NULL, NULL),

-- Both clocks met
('Mailbox full, cannot receive email', 'Bouncing incoming mail with a "mailbox full" error.', 'Email', 'Storage', 'high', 'resolved',
  1, 1, 'Finance', NULL, 'Increased the mailbox storage quota and had the user archive old attachments.',
  NOW() - INTERVAL 240 MINUTE, NOW() - INTERVAL 220 MINUTE, NOW() - INTERVAL 60 MINUTE, NULL, NULL),

-- Response SLA healthy, freshly filed
('New employee onboarding checklist request', 'Manager is asking for the current IT onboarding checklist template.', 'Other', 'Documentation', 'low', 'new',
  1, NULL, 'HR', NULL, NULL,
  NOW() - INTERVAL 12 MINUTE, NULL, NULL, NULL, NULL),

-- On Hold - resolution clock paused with time still left
('Conference room booking system down', 'The room booking screen outside Conference Room B shows a blank page.', 'Other', 'Facilities Tech', 'medium', 'on_hold',
  1, 1, 'Facilities', NULL, NULL,
  NOW() - INTERVAL 180 MINUTE, NOW() - INTERVAL 130 MINUTE, NULL, NULL, NOW() - INTERVAL 15 MINUTE),

-- Both clocks met
('General IT policy question', 'Asking whether personal phones are allowed to connect to the guest WiFi.', 'Other', 'Policy', 'low', 'closed',
  1, 1, 'Legal', NULL, 'Answered directly - personal devices are permitted on the guest network only, per current policy.',
  NOW() - INTERVAL 300 MINUTE, NOW() - INTERVAL 260 MINUTE, NOW() - INTERVAL 100 MINUTE, NOW() - INTERVAL 70 MINUTE, NULL),

-- Response SLA healthy, freshly filed
('Request for software training session', 'Team would like a refresher session on the new project management tool.', 'Other', 'Training', 'low', 'new',
  1, NULL, 'Operations', NULL, NULL,
  NOW() - INTERVAL 5 MINUTE, NULL, NULL, NULL, NULL);

-- Assign ticket numbers the same way the app does (TK-1000+id), for any row this script just
-- inserted that doesn't have one yet.
UPDATE tickets SET ticket_number = CONCAT('TK-', 1000 + id) WHERE ticket_number IS NULL;

-- Backfill the two SLA due dates from each ticket's priority and the live sla_policies rules -
-- the exact same formula Program.cs uses when a ticket is filed (created_at + that priority's
-- allotted minutes). Only touches the rows this script just inserted (existing tickets already
-- have these set from sla_migration.sql).
UPDATE tickets t
JOIN sla_policies p ON p.priority = t.priority
SET
  t.response_due_at   = DATE_ADD(t.created_at, INTERVAL p.response_minutes MINUTE),
  t.resolution_due_at = DATE_ADD(t.created_at, INTERVAL p.resolution_minutes MINUTE)
WHERE t.response_due_at IS NULL;

-- Lock in response_met / resolution_met from the real timestamps above vs. those due dates -
-- same one-time-fact semantics the app uses, computed here instead of guessed by hand.
UPDATE tickets
SET response_met = (first_responded_at <= response_due_at)
WHERE first_responded_at IS NOT NULL AND response_met IS NULL;

UPDATE tickets
SET resolution_met = (resolved_at <= resolution_due_at)
WHERE resolved_at IS NOT NULL AND resolution_met IS NULL;

-- Restore Workbench's default preference now that we're done.
SET SQL_SAFE_UPDATES = 1;
