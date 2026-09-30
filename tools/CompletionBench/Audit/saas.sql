-- The completion audit's extra schema (docs/dev/design/sql-completion-audit-2.md),
-- loaded after scripts/demo 01-06: a typical SaaS app's naming, with the
-- shapes that stress completion — column names many tables share, two FKs
-- from one table to another, a composite FK, enums, a domain, views,
-- functions, a procedure, a quoted mixed-case table, a sequence, comments.
-- The rows are there so pg_stats has something to say. Idempotent.
DROP SCHEMA IF EXISTS saas CASCADE;
CREATE SCHEMA saas;
SET search_path TO saas, public;

CREATE TYPE saas.issue_status AS ENUM ('open', 'in_progress', 'blocked', 'done', 'wont_fix');
CREATE TYPE saas.plan_tier AS ENUM ('free', 'pro', 'business', 'enterprise');
CREATE DOMAIN saas.slug AS text CHECK (VALUE ~ '^[a-z0-9-]+$');

CREATE TABLE saas.accounts (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name        text NOT NULL,
    slug        saas.slug NOT NULL UNIQUE,
    plan        saas.plan_tier NOT NULL DEFAULT 'free',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_at  timestamptz NOT NULL DEFAULT now(),
    deleted_at  timestamptz
);
COMMENT ON TABLE saas.accounts IS 'A paying organisation (tenant).';

CREATE TABLE saas.users (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id    bigint NOT NULL REFERENCES saas.accounts(id),
    email         text NOT NULL UNIQUE,
    display_name  text,
    is_admin      boolean NOT NULL DEFAULT false,
    last_login_at timestamptz,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now()
);
COMMENT ON COLUMN saas.users.last_login_at IS 'Null until the first successful login.';

CREATE TABLE saas.teams (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint NOT NULL REFERENCES saas.accounts(id),
    name       text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.team_members (
    team_id   bigint NOT NULL REFERENCES saas.teams(id),
    user_id   bigint NOT NULL REFERENCES saas.users(id),
    role      text NOT NULL DEFAULT 'member',
    joined_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (team_id, user_id)
);

CREATE TABLE saas.projects (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id  bigint NOT NULL REFERENCES saas.accounts(id),
    team_id     bigint REFERENCES saas.teams(id),
    key         text NOT NULL,
    name        text NOT NULL,
    description text,
    archived    boolean NOT NULL DEFAULT false,
    created_at  timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id, key)
);

CREATE TABLE saas.issues (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id   bigint NOT NULL REFERENCES saas.projects(id),
    number       integer NOT NULL,
    title        text NOT NULL,
    body         text,
    status       saas.issue_status NOT NULL DEFAULT 'open',
    priority     smallint NOT NULL DEFAULT 3,
    reporter_id  bigint NOT NULL REFERENCES saas.users(id),
    assignee_id  bigint REFERENCES saas.users(id),
    due_date     date,
    estimate     interval,
    labels       text[] NOT NULL DEFAULT '{}',
    metadata     jsonb NOT NULL DEFAULT '{}',
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    closed_at    timestamptz,
    UNIQUE (project_id, number)
);
COMMENT ON TABLE saas.issues IS 'Work items; number is per-project.';
COMMENT ON COLUMN saas.issues.priority IS '1 = urgent … 5 = someday.';

CREATE TABLE saas.comments (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issue_id   bigint NOT NULL REFERENCES saas.issues(id) ON DELETE CASCADE,
    author_id  bigint NOT NULL REFERENCES saas.users(id),
    body       text NOT NULL,
    edited     boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.attachments (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    comment_id   bigint NOT NULL REFERENCES saas.comments(id) ON DELETE CASCADE,
    file_name    text NOT NULL,
    content_type text NOT NULL,
    size_bytes   bigint NOT NULL,
    storage_key  text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.labels (
    project_id bigint NOT NULL REFERENCES saas.projects(id),
    name       text NOT NULL,
    color      text NOT NULL DEFAULT '#888888',
    PRIMARY KEY (project_id, name)
);

CREATE TABLE saas.issue_labels (
    issue_id   bigint NOT NULL REFERENCES saas.issues(id) ON DELETE CASCADE,
    project_id bigint NOT NULL,
    label_name text NOT NULL,
    PRIMARY KEY (issue_id, label_name),
    FOREIGN KEY (project_id, label_name) REFERENCES saas.labels(project_id, name)
);

CREATE TABLE saas.plans (
    id            integer PRIMARY KEY,
    tier          saas.plan_tier NOT NULL UNIQUE,
    monthly_price numeric(10,2) NOT NULL,
    seat_limit    integer
);

CREATE TABLE saas.subscriptions (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id  bigint NOT NULL REFERENCES saas.accounts(id),
    plan_id     integer NOT NULL REFERENCES saas.plans(id),
    status      text NOT NULL DEFAULT 'active',
    started_at  timestamptz NOT NULL DEFAULT now(),
    ends_at     timestamptz,
    seats       integer NOT NULL DEFAULT 1
);

CREATE TABLE saas.invoices (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    subscription_id bigint NOT NULL REFERENCES saas.subscriptions(id),
    number          text NOT NULL UNIQUE,
    issued_at       date NOT NULL,
    due_at          date NOT NULL,
    paid_at         timestamptz,
    total           numeric(12,2) NOT NULL,
    currency        char(3) NOT NULL DEFAULT 'EUR'
);

CREATE TABLE saas.invoice_lines (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_id  bigint NOT NULL REFERENCES saas.invoices(id) ON DELETE CASCADE,
    description text NOT NULL,
    quantity    integer NOT NULL DEFAULT 1,
    unit_price  numeric(12,2) NOT NULL
);

CREATE TABLE saas.payments (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    invoice_id  bigint NOT NULL REFERENCES saas.invoices(id),
    amount      numeric(12,2) NOT NULL,
    method      text NOT NULL,
    provider_ref text,
    received_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.api_keys (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id   bigint NOT NULL REFERENCES saas.accounts(id),
    created_by   bigint NOT NULL REFERENCES saas.users(id),
    prefix       text NOT NULL,
    hashed_key   bytea NOT NULL,
    last_used_at timestamptz,
    revoked_at   timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.sessions (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     bigint NOT NULL REFERENCES saas.users(id),
    ip          inet,
    user_agent  text,
    created_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL
);

CREATE TABLE saas.audit_events (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id  bigint NOT NULL REFERENCES saas.accounts(id),
    actor_id    bigint REFERENCES saas.users(id),
    action      text NOT NULL,
    target_type text NOT NULL,
    target_id   bigint,
    payload     jsonb NOT NULL DEFAULT '{}',
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.webhooks (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id bigint NOT NULL REFERENCES saas.accounts(id),
    url        text NOT NULL,
    events     text[] NOT NULL,
    secret     text NOT NULL,
    active     boolean NOT NULL DEFAULT true
);

CREATE TABLE saas.notifications (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id    bigint NOT NULL REFERENCES saas.users(id),
    issue_id   bigint REFERENCES saas.issues(id),
    kind       text NOT NULL,
    read_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas.feature_flags (
    key        text PRIMARY KEY,
    enabled    boolean NOT NULL DEFAULT false,
    rollout    numeric(5,2) NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE saas."TimeEntries" (
    "Id"        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "IssueId"   bigint NOT NULL REFERENCES saas.issues(id),
    "UserId"    bigint NOT NULL REFERENCES saas.users(id),
    "Minutes"   integer NOT NULL,
    "Billable"  boolean NOT NULL DEFAULT true,
    "Logged At" timestamptz NOT NULL DEFAULT now()
);

CREATE SEQUENCE saas.invoice_number_seq START 1000;

CREATE VIEW saas.open_issues AS
    SELECT i.id, i.project_id, i.number, i.title, i.status, i.assignee_id, i.created_at
    FROM saas.issues i
    WHERE i.status IN ('open', 'in_progress', 'blocked');

CREATE VIEW saas.account_seats AS
    SELECT a.id AS account_id, a.name, count(u.id) AS seats_used
    FROM saas.accounts a
    LEFT JOIN saas.users u ON u.account_id = a.id
    GROUP BY a.id, a.name;

CREATE FUNCTION saas.issue_key(p_project_id bigint, p_number integer) RETURNS text
    LANGUAGE sql STABLE AS
    $$ SELECT p.key || '-' || p_number FROM saas.projects p WHERE p.id = p_project_id $$;

CREATE FUNCTION saas.account_mrr(p_account_id bigint, p_at date DEFAULT current_date) RETURNS numeric
    LANGUAGE sql STABLE AS
    $$ SELECT coalesce(sum(pl.monthly_price * s.seats), 0)
       FROM saas.subscriptions s JOIN saas.plans pl ON pl.id = s.plan_id
       WHERE s.account_id = p_account_id AND s.started_at::date <= p_at AND (s.ends_at IS NULL OR s.ends_at::date > p_at) $$;

CREATE PROCEDURE saas.archive_project(p_project_id bigint)
    LANGUAGE sql AS
    $$ UPDATE saas.projects SET archived = true WHERE id = p_project_id $$;

-- Some data so pg_stats has most-common values to look at.
INSERT INTO saas.accounts (name, slug, plan)
SELECT 'Account ' || g, 'account-' || g, (ARRAY['free','pro','business','enterprise'])[1 + g % 4]::saas.plan_tier
FROM generate_series(1, 200) g;

INSERT INTO saas.users (account_id, email, display_name, is_admin)
SELECT 1 + g % 200, 'user' || g || '@example.com', 'User ' || g, g % 17 = 0
FROM generate_series(1, 3000) g;

INSERT INTO saas.teams (account_id, name) SELECT 1 + g % 200, 'Team ' || g FROM generate_series(1, 400) g;

INSERT INTO saas.projects (account_id, team_id, key, name)
SELECT 1 + g % 200, 1 + g % 400, 'P' || g, 'Project ' || g FROM generate_series(1, 800) g;

INSERT INTO saas.issues (project_id, number, title, status, priority, reporter_id, assignee_id)
SELECT 1 + g % 800, g, 'Issue ' || g,
       (ARRAY['open','in_progress','blocked','done','wont_fix'])[1 + (g * 7) % 5]::saas.issue_status,
       1 + g % 5, 1 + g % 3000, CASE WHEN g % 3 = 0 THEN NULL ELSE 1 + (g * 13) % 3000 END
FROM generate_series(1, 20000) g;

INSERT INTO saas.plans VALUES (1, 'free', 0, 3), (2, 'pro', 12, NULL), (3, 'business', 25, NULL), (4, 'enterprise', 60, NULL);

INSERT INTO saas.subscriptions (account_id, plan_id, status, seats)
SELECT 1 + g % 200, 1 + g % 4, (ARRAY['active','active','active','past_due','canceled'])[1 + g % 5], 1 + g % 40
FROM generate_series(1, 400) g;

ANALYZE;
