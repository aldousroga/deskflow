-- Sample data for the `assets` table - 10 rows covering all 4 statuses.
-- Run this after assets.sql (and after your first app run, so the seeded admin user with id 1
-- already exists - a couple of rows below assign to that admin).
-- Safe to re-run: it clears existing sample rows by asset_tag first, then re-inserts.

USE deskflow;

DELETE FROM assets WHERE asset_tag IN (
  'LAPTOP-1001', 'LAPTOP-1002', 'DESK-2001', 'MON-3001', 'MON-3002',
  'PHN-4001', 'PRN-5001', 'SRV-6001', 'NET-7001', 'TAB-8001'
);

INSERT INTO assets (asset_tag, name, type, serial_number, status, assigned_to_id, purchased_at, warranty_expires_at, notes)
VALUES
  ('LAPTOP-1001', 'Dell Latitude 5440',      'Laptop',     'SN-A10023', 'in_use',       1,    '2025-02-10', '2028-02-10', 'Assigned to IT admin'),
  ('LAPTOP-1002', 'MacBook Pro 14"',          'Laptop',     'SN-A10099', 'in_use',       NULL, '2025-05-20', '2028-05-20', 'Awaiting reassignment in HR'),
  ('DESK-2001',   'HP EliteDesk 800',         'Desktop',    'SN-B20044', 'available',    NULL, '2024-11-01', '2027-11-01', 'Spare desktop in IT closet'),
  ('MON-3001',    'Dell UltraSharp 24"',      'Monitor',    'SN-C30011', 'available',    NULL, '2024-09-15', '2027-09-15', NULL),
  ('MON-3002',    'LG 27" 4K',                'Monitor',    'SN-C30078', 'in_use',       1,    '2025-01-05', '2028-01-05', NULL),
  ('PHN-4001',    'iPhone 14',                'Phone',      'SN-D40012', 'under_repair', NULL, '2023-08-30', '2025-08-30', 'Cracked screen, sent for repair 8/10'),
  ('PRN-5001',    'Canon imageCLASS MF445dw', 'Printer',    'SN-E50003', 'under_repair', NULL, '2022-03-12', NULL,         'Paper feed jamming intermittently'),
  ('SRV-6001',    'Dell PowerEdge R750',      'Server',     'SN-F60001', 'in_use',       NULL, '2023-06-01', '2026-06-01', 'Primary file server, rack 2'),
  ('NET-7001',    'Cisco Catalyst 9200',      'Networking', 'SN-G70009', 'retired',      NULL, '2019-04-18', '2022-04-18', 'Decommissioned, replaced by NET-7002'),
  ('TAB-8001',    'iPad Air (5th gen)',       'Tablet',     'SN-H80015', 'retired',      NULL, '2021-10-01', '2023-10-01', 'Battery no longer holds charge');
