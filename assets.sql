-- Run this inside your existing `deskflow` database, after users.sql
-- (In Workbench: make sure the schema dropdown / active schema is `deskflow`, then run this file.)

USE deskflow;

CREATE TABLE assets (
  id                  INT AUTO_INCREMENT PRIMARY KEY,
  asset_tag           VARCHAR(50)  NOT NULL UNIQUE,   -- e.g. LAPTOP-0042
  name                VARCHAR(150) NOT NULL,           -- e.g. Dell Latitude 5420
  type                VARCHAR(50)  NOT NULL,           -- Laptop, Monitor, Phone, etc.
  serial_number       VARCHAR(100) NULL,
  status              ENUM('available', 'in_use', 'under_repair', 'retired') NOT NULL DEFAULT 'available',
  assigned_to_id      INT NULL,                        -- who currently has it, if anyone
  purchased_at        DATE NULL,
  warranty_expires_at DATE NULL,
  notes               TEXT NULL,
  created_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at          DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_assets_assigned_to FOREIGN KEY (assigned_to_id) REFERENCES users(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ON DELETE SET NULL means: if a user account gets deleted later, any asset that was
-- assigned to them just becomes unassigned instead of the delete failing or orphaning data.
