-- Run this inside your existing `deskflow` database
-- (In Workbench: make sure the schema dropdown / active schema is `deskflow`, then run this file.)

USE deskflow;

CREATE TABLE users (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  username      VARCHAR(50)  NOT NULL UNIQUE,
  email         VARCHAR(100) NOT NULL UNIQUE,
  password_hash VARCHAR(255) NOT NULL,          -- bcrypt hash, never store plain-text passwords
  full_name     VARCHAR(100) NOT NULL,
  role          ENUM('admin', 'agent', 'requester') NOT NULL DEFAULT 'requester',
  is_active     TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- No manual INSERT needed here — the C# API seeds a default admin account
-- (username: admin / password: Admin@123) the first time it runs against an empty table.
-- Change that password immediately after your first login.
