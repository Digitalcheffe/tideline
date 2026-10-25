CREATE TABLE settings (
    key         VARCHAR(100) PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  VARCHAR(32) NOT NULL
);

-- ---------------------------------------------------------------- site users and security
CREATE TABLE users (
    id              INTEGER PRIMARY KEY,
    email           VARCHAR(254) NOT NULL UNIQUE,
    display_name    VARCHAR(100) NOT NULL,
    password_hash   VARCHAR(255) NOT NULL,                 -- ASP.NET Core PasswordHasher (PBKDF2) output
    role            VARCHAR(16)  NOT NULL CHECK (role IN ('owner','member','viewer')),
    is_active       INTEGER NOT NULL DEFAULT 1,
    mfa_enrolled_at VARCHAR(32),                           -- NULL = must enroll before anything else
    failed_logins   INTEGER NOT NULL DEFAULT 0,
    locked_until    VARCHAR(32),
    created_at      VARCHAR(32) NOT NULL,
    last_login_at   VARCHAR(32)
);

CREATE TABLE user_mfa (
    id              INTEGER PRIMARY KEY,
    user_id         INTEGER NOT NULL REFERENCES users(id),
    kind            VARCHAR(16) NOT NULL CHECK (kind IN ('totp','passkey')),
    label           VARCHAR(100) NOT NULL,
    totp_secret_enc TEXT,                                  -- Data Protection encrypted, kind = totp
    credential_id   VARCHAR(512),                          -- base64url, kind = passkey
    public_key      TEXT,                                  -- COSE key base64, kind = passkey
    sign_count      INTEGER NOT NULL DEFAULT 0,
    created_at      VARCHAR(32) NOT NULL,
    last_used_at    VARCHAR(32),
    revoked_at      VARCHAR(32)
);
CREATE INDEX ix_user_mfa_user ON user_mfa(user_id);
CREATE UNIQUE INDEX ux_user_mfa_credential ON user_mfa(credential_id);

CREATE TABLE recovery_codes (
    id          INTEGER PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    code_hash   VARCHAR(255) NOT NULL,
    created_at  VARCHAR(32) NOT NULL,
    used_at     VARCHAR(32)
);
CREATE INDEX ix_recovery_codes_user ON recovery_codes(user_id);

CREATE TABLE sessions (
    id              INTEGER PRIMARY KEY,
    token_hash      VARCHAR(64) NOT NULL UNIQUE,           -- SHA-256 hex of the cookie value
    user_id         INTEGER NOT NULL REFERENCES users(id),
    created_at      VARCHAR(32) NOT NULL,
    last_seen_at    VARCHAR(32) NOT NULL,
    expires_at      VARCHAR(32) NOT NULL,
    mfa_passed_at   VARCHAR(32),                           -- NULL = password ok, second factor pending
    ip              VARCHAR(45),
    user_agent      VARCHAR(400),
    revoked_at      VARCHAR(32)
);
CREATE INDEX ix_sessions_user ON sessions(user_id);

CREATE TABLE api_tokens (
    id          INTEGER PRIMARY KEY,
    name        VARCHAR(100) NOT NULL,
    token_hash  VARCHAR(64) NOT NULL UNIQUE,
    prefix      VARCHAR(12) NOT NULL,                      -- first chars shown in UI, e.g. 'tdl_7f3a'
    scope       VARCHAR(16) NOT NULL CHECK (scope IN ('read','write')),
    created_by  INTEGER NOT NULL REFERENCES users(id),
    created_at  VARCHAR(32) NOT NULL,
    last_used_at VARCHAR(32),
    expires_at  VARCHAR(32),
    revoked_at  VARCHAR(32)
);

-- ---------------------------------------------------------------- household people (retirement, ownership)
CREATE TABLE people (
    id                      INTEGER PRIMARY KEY,
    name                    VARCHAR(100) NOT NULL UNIQUE,
    user_id                 INTEGER REFERENCES users(id),
    birth_on                VARCHAR(10),
    retire_age              INTEGER,
    social_security_annual_today_cents INTEGER,            -- estimate from the SSA statement, today's dollars
    is_active               INTEGER NOT NULL DEFAULT 1
);

-- ---------------------------------------------------------------- accounts
CREATE TABLE accounts (
    id                  INTEGER PRIMARY KEY,
    name                VARCHAR(100) NOT NULL UNIQUE,
    kind                VARCHAR(16) NOT NULL CHECK (kind IN ('checking','savings','credit_card','loan','asset','retirement')),
    institution         VARCHAR(100),
    bank_number         VARCHAR(8),                        -- last 4, matches CSV file names acct_XXXX_
    owner_person_id     INTEGER REFERENCES people(id),
    opening_balance_cents INTEGER,                         -- money accounts only; NULL = not known yet
    opening_on          VARCHAR(10),
    is_in_spread        INTEGER NOT NULL DEFAULT 1,        -- 0 for loans, assets, retirement
    is_closed           INTEGER NOT NULL DEFAULT 0,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    simplefin_account_id VARCHAR(100),
    csv_until_on        VARCHAR(10),                       -- cutover: CSV rows after this date are refused
    simplefin_from_on   VARCHAR(10),                       -- cutover: SimpleFIN rows before this date are ignored
    notes               TEXT,
    created_at          VARCHAR(32) NOT NULL
);
CREATE UNIQUE INDEX ux_accounts_bank_number ON accounts(institution, bank_number);

CREATE TABLE card_terms (
    account_id      INTEGER PRIMARY KEY REFERENCES accounts(id),
    is_pay_in_full  INTEGER NOT NULL DEFAULT 1,
    statement_day   INTEGER CHECK (statement_day BETWEEN 1 AND 31),
    due_day         INTEGER CHECK (due_day BETWEEN 1 AND 31),
    credit_limit_cents INTEGER
);

CREATE TABLE loan_terms (
    account_id          INTEGER PRIMARY KEY REFERENCES accounts(id),
    loan_type           VARCHAR(16) NOT NULL CHECK (loan_type IN ('mortgage','auto','student','personal','family','other')),
    rate_bps            INTEGER,
    payment_cents       INTEGER,                           -- principal + interest part of the monthly payment
    escrow_cents        INTEGER,                           -- part of the payment that is not P&I
    payment_day         INTEGER,
    original_balance_cents INTEGER,
    start_on            VARCHAR(10),
    term_months         INTEGER,
    payment_category_id INTEGER REFERENCES categories(id), -- ledger lines in this category are this loan's payments
    secured_asset_id    INTEGER REFERENCES accounts(id)    -- the house or car it is secured by (equity)
);

CREATE TABLE retirement_terms (
    account_id                  INTEGER PRIMARY KEY REFERENCES accounts(id),
    account_type                VARCHAR(16) NOT NULL CHECK (account_type IN ('401k','403b','457','ira','roth_ira','pension','hsa','brokerage','other')),
    employee_contribution_monthly_cents INTEGER NOT NULL DEFAULT 0,
    employer_contribution_monthly_cents INTEGER NOT NULL DEFAULT 0,
    is_included_in_goal         INTEGER NOT NULL DEFAULT 1,
    pension_annual_at_retirement_cents INTEGER             -- account_type = pension only
);

CREATE TABLE balance_snapshots (
    id          INTEGER PRIMARY KEY,
    account_id  INTEGER NOT NULL REFERENCES accounts(id),
    as_of_on    VARCHAR(10) NOT NULL,
    balance_cents INTEGER NOT NULL,                        -- loans: amount owed as a positive number
    source      VARCHAR(16) NOT NULL CHECK (source IN ('bank','statement','estimate','computed')),
    note        VARCHAR(400),
    created_by  INTEGER REFERENCES users(id),              -- NULL = system
    created_at  VARCHAR(32) NOT NULL,
    UNIQUE (account_id, as_of_on, source)
);
CREATE INDEX ix_balance_snapshots_account_date ON balance_snapshots(account_id, as_of_on);

-- ---------------------------------------------------------------- categories and tags
CREATE TABLE categories (
    id              INTEGER PRIMARY KEY,
    name            VARCHAR(100) NOT NULL UNIQUE,
    default_type    VARCHAR(16) NOT NULL CHECK (default_type IN ('income','bill','required','extra','one_time','savings')),
    is_archived     INTEGER NOT NULL DEFAULT 0,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    merged_into_id  INTEGER REFERENCES categories(id)
);

CREATE TABLE tags (
    id      INTEGER PRIMARY KEY,
    name    VARCHAR(50) NOT NULL UNIQUE,                   -- lower-case, kebab: one-time, kids, review
    color   VARCHAR(16)
);

-- ---------------------------------------------------------------- imports
CREATE TABLE import_batches (
    id              INTEGER PRIMARY KEY,
    source          VARCHAR(16) NOT NULL CHECK (source IN ('csv','simplefin','manual')),
    file_name       VARCHAR(260),
    file_sha256     VARCHAR(64),
    account_id      INTEGER REFERENCES accounts(id),
    started_at      VARCHAR(32) NOT NULL,
    finished_at     VARCHAR(32),
    status          VARCHAR(16) NOT NULL CHECK (status IN ('running','done','failed')),
    rows_read       INTEGER NOT NULL DEFAULT 0,
    rows_inserted   INTEGER NOT NULL DEFAULT 0,
    rows_existing   INTEGER NOT NULL DEFAULT 0,
    rows_review     INTEGER NOT NULL DEFAULT 0,
    error           TEXT
);
CREATE INDEX ix_import_batches_sha ON import_batches(file_sha256);

-- ---------------------------------------------------------------- recurring (bills and subscriptions)
CREATE TABLE recurring (
    id                  INTEGER PRIMARY KEY,
    name                VARCHAR(100) NOT NULL UNIQUE,
    category_id         INTEGER NOT NULL REFERENCES categories(id),
    expected_min_cents  INTEGER NOT NULL,
    expected_max_cents  INTEGER NOT NULL,
    frequency           VARCHAR(16) NOT NULL CHECK (frequency IN ('monthly','yearly')),
    every_n             INTEGER NOT NULL DEFAULT 1,        -- monthly + every_n 2 = every other month
    due_day             INTEGER NOT NULL CHECK (due_day BETWEEN 1 AND 31),
    first_due_on        VARCHAR(10) NOT NULL,
    grace_days          INTEGER NOT NULL DEFAULT 5,
    account_id          INTEGER REFERENCES accounts(id),   -- expected paying account, optional
    is_active           INTEGER NOT NULL DEFAULT 1,
    ended_on            VARCHAR(10),
    CHECK (expected_min_cents <= expected_max_cents)
);

-- ---------------------------------------------------------------- plans
CREATE TABLE plans (
    id                  INTEGER PRIMARY KEY,
    name                VARCHAR(100) NOT NULL UNIQUE,
    target_cents        INTEGER NOT NULL,
    start_on            VARCHAR(10) NOT NULL,
    target_on           VARCHAR(10) NOT NULL,
    start_amount_cents  INTEGER NOT NULL DEFAULT 0,
    funding_account_id  INTEGER REFERENCES accounts(id),
    progress_mode       VARCHAR(24) NOT NULL CHECK (progress_mode IN ('account_balance','linked_lines')),
    is_trackable        INTEGER NOT NULL DEFAULT 1,
    status              VARCHAR(16) NOT NULL CHECK (status IN ('active','done','cancelled')),
    notes               TEXT,
    created_at          VARCHAR(32) NOT NULL
);

CREATE TABLE plan_checkpoints (                            -- one row per plan per month end, written by the evaluator
    plan_id         INTEGER NOT NULL REFERENCES plans(id),
    month           VARCHAR(7) NOT NULL,                   -- 'YYYY-MM'
    expected_cents  INTEGER NOT NULL,
    actual_cents    INTEGER NOT NULL,
    status          VARCHAR(16) NOT NULL CHECK (status IN ('on_track','wavered','behind','done')),
    PRIMARY KEY (plan_id, month)
);

-- ---------------------------------------------------------------- the ledger
CREATE TABLE moves (
    id                  INTEGER PRIMARY KEY,
    counts_as           VARCHAR(16) NOT NULL CHECK (counts_as IN ('none','savings')),
    created_at          VARCHAR(32) NOT NULL
);

CREATE TABLE transactions (
    id                  INTEGER PRIMARY KEY,
    account_id          INTEGER NOT NULL REFERENCES accounts(id),
    -- bank fields: written once at import, never updated (enforced in repository + trigger)
    posted_on           VARCHAR(10) NOT NULL,
    amount_cents        INTEGER NOT NULL,
    original_description VARCHAR(500) NOT NULL,
    bank_balance_cents  INTEGER,                           -- Truist daily posted balance when given
    check_number        VARCHAR(20),
    external_id         VARCHAR(100) NOT NULL UNIQUE,      -- 'truist:<sha1[:20]>', 'sfin:<conn>:<id>', 'manual:<uuid>'
    source              VARCHAR(16) NOT NULL CHECK (source IN ('csv','simplefin','manual')),
    import_batch_id     INTEGER REFERENCES import_batches(id),
    raw_json            TEXT,                              -- the original CSV row or SimpleFIN object, for drill to source
    -- our fields: editable, audited
    payee               VARCHAR(100),
    category_id         INTEGER REFERENCES categories(id),
    type                VARCHAR(16) CHECK (type IN ('income','bill','required','extra','one_time','savings')),
    move_id             INTEGER REFERENCES moves(id),
    recurring_id        INTEGER REFERENCES recurring(id),
    plan_id             INTEGER REFERENCES plans(id),
    asset_account_id    INTEGER REFERENCES accounts(id),   -- reserved for asset ledgers (deferred)
    rule_id             INTEGER REFERENCES rules(id),      -- rule that last set category/type, NULL = by hand
    is_manual_override  INTEGER NOT NULL DEFAULT 0,        -- 1 = a person set it; rules never overwrite
    is_pending          INTEGER NOT NULL DEFAULT 0,
    notes               TEXT,
    created_at          VARCHAR(32) NOT NULL,
    updated_at          VARCHAR(32) NOT NULL,
    -- a line is either a move or has a type
    CHECK ((move_id IS NULL AND type IS NOT NULL) OR (move_id IS NOT NULL))
);
CREATE INDEX ix_tx_account_date ON transactions(account_id, posted_on);
CREATE INDEX ix_tx_date ON transactions(posted_on);
CREATE INDEX ix_tx_type_date ON transactions(type, posted_on);
CREATE INDEX ix_tx_category_date ON transactions(category_id, posted_on);
CREATE INDEX ix_tx_recurring ON transactions(recurring_id);
CREATE INDEX ix_tx_move ON transactions(move_id);
CREATE INDEX ix_tx_plan ON transactions(plan_id);

CREATE TABLE transaction_tags (
    transaction_id  INTEGER NOT NULL REFERENCES transactions(id),
    tag_id          INTEGER NOT NULL REFERENCES tags(id),
    PRIMARY KEY (transaction_id, tag_id)
);
CREATE INDEX ix_transaction_tags_tag ON transaction_tags(tag_id);

CREATE TABLE split_lines (
    id              INTEGER PRIMARY KEY,
    transaction_id  INTEGER NOT NULL REFERENCES transactions(id),
    amount_cents    INTEGER NOT NULL,                      -- same sign as the parent
    category_id     INTEGER NOT NULL REFERENCES categories(id),
    type            VARCHAR(16) NOT NULL CHECK (type IN ('income','bill','required','extra','one_time','savings')),
    note            VARCHAR(400),
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      VARCHAR(32) NOT NULL
);
CREATE INDEX ix_split_lines_tx ON split_lines(transaction_id);
CREATE INDEX ix_split_lines_category ON split_lines(category_id);

CREATE TABLE split_line_tags (
    split_line_id   INTEGER NOT NULL REFERENCES split_lines(id),
    tag_id          INTEGER NOT NULL REFERENCES tags(id),
    PRIMARY KEY (split_line_id, tag_id)
);

-- ---------------------------------------------------------------- rules
CREATE TABLE rules (
    id              INTEGER PRIMARY KEY,
    name            VARCHAR(150) NOT NULL UNIQUE,
    priority        INTEGER NOT NULL,                      -- ascending; first match wins; gaps of 10
    is_active       INTEGER NOT NULL DEFAULT 1,
    set_category_id INTEGER REFERENCES categories(id),
    set_type        VARCHAR(16) CHECK (set_type IN ('income','bill','required','extra','one_time','savings')),
    set_payee       VARCHAR(100),
    set_recurring_id INTEGER REFERENCES recurring(id),
    set_plan_id     INTEGER REFERENCES plans(id),
    created_at      VARCHAR(32) NOT NULL,
    updated_at      VARCHAR(32) NOT NULL
);
CREATE INDEX ix_rules_priority ON rules(priority);

CREATE TABLE rule_conditions (
    id          INTEGER PRIMARY KEY,
    rule_id     INTEGER NOT NULL REFERENCES rules(id),
    field       VARCHAR(24) NOT NULL CHECK (field IN ('description','amount','account','account_kind','direction')),
    op          VARCHAR(24) NOT NULL CHECK (op IN ('contains_any','equals_any','starts_with','gte','lte','is','in')),
    value       TEXT NOT NULL                              -- JSON: ["SPOTIFY"] | 900 | "1234" | ["checking","savings"] | "in"
);
CREATE INDEX ix_rule_conditions_rule ON rule_conditions(rule_id);

CREATE TABLE rule_tags (
    rule_id INTEGER NOT NULL REFERENCES rules(id),
    tag_id  INTEGER NOT NULL REFERENCES tags(id),
    PRIMARY KEY (rule_id, tag_id)
);

CREATE TABLE move_patterns (                               -- how to recognise the two sides of a move
    id              INTEGER PRIMARY KEY,
    from_account_id INTEGER NOT NULL REFERENCES accounts(id),
    from_contains   VARCHAR(100) NOT NULL,
    to_account_id   INTEGER NOT NULL REFERENCES accounts(id),
    to_contains     VARCHAR(100) NOT NULL,
    max_days_apart  INTEGER NOT NULL DEFAULT 5
);

-- ---------------------------------------------------------------- budget
CREATE TABLE budgets (
    id                      INTEGER PRIMARY KEY,
    year                    INTEGER NOT NULL UNIQUE,
    expected_income_cents   INTEGER,
    pop_percent             INTEGER NOT NULL DEFAULT 10,   -- flag when month-to-date passes guideline by this %
    pop_min_cents           INTEGER NOT NULL DEFAULT 2500  -- ...and by at least this much
);

CREATE TABLE budget_lines (
    budget_id       INTEGER NOT NULL REFERENCES budgets(id),
    category_id     INTEGER NOT NULL REFERENCES categories(id),
    monthly_cents   INTEGER,                               -- guideline per month
    annual_cents    INTEGER,                               -- or per year, for lumpy categories
    PRIMARY KEY (budget_id, category_id)
);

-- ---------------------------------------------------------------- retirement goal
CREATE TABLE retirement_goal (
    id                              INTEGER PRIMARY KEY CHECK (id = 1),   -- one row per household
    goal_annual_income_today_cents  INTEGER,
    other_income_annual_today_cents INTEGER NOT NULL DEFAULT 0,
    return_before_bps               INTEGER NOT NULL DEFAULT 600,
    return_during_bps               INTEGER NOT NULL DEFAULT 400,
    inflation_bps                   INTEGER NOT NULL DEFAULT 300,
    withdrawal_rate_bps             INTEGER NOT NULL DEFAULT 400,
    plan_to_age                     INTEGER NOT NULL DEFAULT 95,
    updated_at                      VARCHAR(32) NOT NULL
);

-- ---------------------------------------------------------------- flags and notifications
CREATE TABLE flags (
    id              INTEGER PRIMARY KEY,
    kind            VARCHAR(40) NOT NULL,                  -- see docs/04-domain-rules.md, "Flags"
    subject_type    VARCHAR(24) NOT NULL,                  -- recurring | account | category | plan | transaction | import
    subject_id      INTEGER NOT NULL,
    period          VARCHAR(10) NOT NULL,                  -- 'YYYY-MM' or 'YYYY-MM-DD'
    severity        VARCHAR(8) NOT NULL CHECK (severity IN ('info','warn','alert')),
    message         VARCHAR(400) NOT NULL,
    created_at      VARCHAR(32) NOT NULL,
    acknowledged_at VARCHAR(32),
    acknowledged_by INTEGER REFERENCES users(id),
    ack_note        VARCHAR(400),
    resolved_at     VARCHAR(32),                           -- set by the evaluator when the condition clears
    UNIQUE (kind, subject_type, subject_id, period)
);
CREATE INDEX ix_flags_open ON flags(resolved_at, acknowledged_at);

CREATE TABLE notification_channels (
    id          INTEGER PRIMARY KEY,
    kind        VARCHAR(16) NOT NULL CHECK (kind IN ('webhook','email')),
    name        VARCHAR(100) NOT NULL,
    config_enc  TEXT NOT NULL,                             -- encrypted JSON: url / smtp settings
    is_active   INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE notification_prefs (
    flag_kind   VARCHAR(40) PRIMARY KEY,
    mode        VARCHAR(16) NOT NULL CHECK (mode IN ('immediate','summary','off'))
);

CREATE TABLE notifications_sent (
    id          INTEGER PRIMARY KEY,
    channel_id  INTEGER NOT NULL REFERENCES notification_channels(id),
    flag_id     INTEGER REFERENCES flags(id),              -- NULL = monthly summary
    sent_at     VARCHAR(32) NOT NULL,
    status      VARCHAR(16) NOT NULL CHECK (status IN ('sent','failed')),
    error       VARCHAR(400)
);

-- ---------------------------------------------------------------- SimpleFIN
CREATE TABLE simplefin_connections (
    id              INTEGER PRIMARY KEY,
    access_url_enc  TEXT NOT NULL,
    created_at      VARCHAR(32) NOT NULL,
    last_sync_at    VARCHAR(32),
    last_error      VARCHAR(400),
    is_active       INTEGER NOT NULL DEFAULT 1
);

-- ---------------------------------------------------------------- logs
CREATE TABLE audit_log (
    id          INTEGER PRIMARY KEY,
    at          VARCHAR(32) NOT NULL,
    user_id     INTEGER REFERENCES users(id),              -- NULL = system (import, evaluator)
    api_token_id INTEGER REFERENCES api_tokens(id),
    action      VARCHAR(40) NOT NULL,                      -- e.g. transaction.update, split.create, rule.update, login.success
    entity      VARCHAR(40) NOT NULL,
    entity_id   VARCHAR(40),
    before_json TEXT,
    after_json  TEXT,
    ip          VARCHAR(45)
);
CREATE INDEX ix_audit_entity ON audit_log(entity, entity_id);
CREATE INDEX ix_audit_at ON audit_log(at);

CREATE TABLE system_events (
    id      INTEGER PRIMARY KEY,
    at      VARCHAR(32) NOT NULL,
    level   VARCHAR(8) NOT NULL CHECK (level IN ('info','warn','error')),
    source  VARCHAR(40) NOT NULL,                          -- inbox, simplefin, evaluator, notifier, backup
    message VARCHAR(400) NOT NULL,
    detail  TEXT
);
CREATE INDEX ix_system_events_at ON system_events(at);
