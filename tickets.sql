-- Run this inside your existing `deskflow` database, after users.sql and assets.sql
-- (In Workbench: make sure the schema dropdown / active schema is `deskflow`, then run this file.)

USE deskflow;

CREATE TABLE tickets (
  id                     INT AUTO_INCREMENT PRIMARY KEY,
  ticket_number          VARCHAR(20) NULL UNIQUE,        -- set right after insert, e.g. TK-1024
  subject                VARCHAR(200) NOT NULL,
  description            TEXT NOT NULL,
  category               VARCHAR(50) NOT NULL,           -- e.g. Hardware, Software, Network, Access
  subcategory            VARCHAR(50) NULL,                -- e.g. VPN, Printer, Password Reset
  priority               ENUM('low', 'medium', 'high', 'critical') NOT NULL DEFAULT 'medium',
  status                 ENUM('new', 'assigned', 'in_progress', 'on_hold', 'resolved', 'closed') NOT NULL DEFAULT 'new',
  requester_id           INT NULL,                        -- who filed it
  assigned_technician_id INT NULL,                        -- who's working it
  department             VARCHAR(100) NULL,                -- free text for now (see README - Settings will make this configurable)
  asset_id               INT NULL,                        -- optional link to a real asset record
  due_at                 DATETIME NULL,                    -- SLA deadline, computed from priority when the ticket is filed
  resolved_at            DATETIME NULL,
  closed_at              DATETIME NULL,
  resolution_notes       TEXT NULL,
  created_at             DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at             DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_tickets_requester  FOREIGN KEY (requester_id)           REFERENCES users(id)  ON DELETE SET NULL,
  CONSTRAINT fk_tickets_technician FOREIGN KEY (assigned_technician_id) REFERENCES users(id)  ON DELETE SET NULL,
  CONSTRAINT fk_tickets_asset      FOREIGN KEY (asset_id)               REFERENCES assets(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ticket_comments (
  id          INT AUTO_INCREMENT PRIMARY KEY,
  ticket_id   INT NOT NULL,
  author_id   INT NULL,
  body        TEXT NOT NULL,
  is_internal TINYINT(1) NOT NULL DEFAULT 0,   -- 1 = internal note, never shown to the requester
  created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_comments_ticket FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE,
  CONSTRAINT fk_comments_author FOREIGN KEY (author_id) REFERENCES users(id)  ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ticket_history (
  id            INT AUTO_INCREMENT PRIMARY KEY,
  ticket_id     INT NOT NULL,
  actor_id      INT NULL,
  field_changed VARCHAR(50) NOT NULL,   -- 'status', 'priority', 'assigned_technician', 'asset', 'created', etc.
  old_value     VARCHAR(255) NULL,
  new_value     VARCHAR(255) NULL,
  created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_history_ticket FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE,
  CONSTRAINT fk_history_actor  FOREIGN KEY (actor_id)  REFERENCES users(id)  ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ticket_links (
  id                INT AUTO_INCREMENT PRIMARY KEY,
  ticket_id         INT NOT NULL,
  related_ticket_id INT NOT NULL,
  created_at        DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CONSTRAINT fk_links_ticket  FOREIGN KEY (ticket_id)         REFERENCES tickets(id) ON DELETE CASCADE,
  CONSTRAINT fk_links_related FOREIGN KEY (related_ticket_id) REFERENCES tickets(id) ON DELETE CASCADE,
  UNIQUE KEY uq_ticket_link (ticket_id, related_ticket_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Notes:
-- * ticket_number starts NULL and gets set right after the insert (once we know the auto-increment
--   id), so it can read "TK-1024" instead of a raw id. MySQL allows multiple NULLs in a UNIQUE
--   column, so this doesn't collide while a ticket briefly has no number yet.
-- * requester_id / assigned_technician_id / asset_id all use ON DELETE SET NULL, same reasoning as
--   assets.assigned_to_id - deleting a user or an asset later should never delete ticket history,
--   it should just leave that field blank.
-- * ticket_comments/ticket_history/ticket_links all cascade-delete with their ticket - there's no
--   DELETE endpoint for tickets themselves (they're kept as records), but if that ever changes,
--   their comments/history/links clean up automatically.
