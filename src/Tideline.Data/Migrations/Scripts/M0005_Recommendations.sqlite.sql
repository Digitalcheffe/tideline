CREATE TABLE recommendation_rules (
    id              INTEGER PRIMARY KEY,
    name            VARCHAR(100) NOT NULL UNIQUE,
    flag_kind       VARCHAR(40) NOT NULL,                  -- a kind from docs/ARCHITECTURE.md
    min_severity    VARCHAR(8) NOT NULL DEFAULT 'info' CHECK (min_severity IN ('info','warn','alert')),
    contains        VARCHAR(100),                          -- optional: the flag's text has to contain this
    suggestion      VARCHAR(600) NOT NULL,
    is_active       INTEGER NOT NULL DEFAULT 1,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    created_at      VARCHAR(32) NOT NULL,
    updated_at      VARCHAR(32) NOT NULL
);
CREATE INDEX ix_reco_rules_kind ON recommendation_rules(flag_kind);

CREATE TABLE recommendation_dismissals (
    rule_id         INTEGER NOT NULL REFERENCES recommendation_rules(id),
    flag_id         INTEGER NOT NULL REFERENCES flags(id),
    dismissed_at    VARCHAR(32) NOT NULL,
    dismissed_by    INTEGER REFERENCES users(id),
    PRIMARY KEY (rule_id, flag_id)
);

-- Starter suggestions in plain words. They are only text: edit, switch off or delete any of them in the app.
INSERT INTO recommendation_rules (name, flag_kind, min_severity, suggestion, sort_order, created_at, updated_at) VALUES
('A bill has not been paid', 'recurring_missing', 'info', 'Check whether the bill was paid some other way, or is late. If it is late, pay it now; the flag clears by itself once the payment shows up.', 10, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A bill came in at an odd amount', 'recurring_out_of_range', 'info', 'Open the bill and look at this month''s amount. If the price went up for good, change the expected range under Settings, Recurring so it stops flagging.', 20, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A card was not paid in full', 'card_not_paid_in_full', 'info', 'Pay the whole statement balance before the due date. Paying in full each month is what keeps a card free of interest.', 30, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Interest was charged on a card', 'card_interest', 'info', 'Paying the full balance next month stops more interest. If this keeps happening, look at what is being put on the card.', 40, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('The ledger and the bank disagree', 'balance_mismatch', 'info', 'Compare the bank''s balance with the ledger on the account. A file that was missed or imported twice is the usual cause; import the file for the days involved again.', 50, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('An import did not work', 'import_failed', 'info', 'Read the reason on the Imports page. The usual causes are a file for the wrong account, or a file that starts before the cutover date.', 60, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A transfer has no other side', 'move_unpaired', 'info', 'Find the other half in the other account''s export, or pair the two lines by hand in the Ledger. If it was not a transfer, open the line and release it.', 70, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Looks like a new loan', 'new_debt', 'info', 'Add it under Settings, Money accounts as a loan, with its rate and payment, so it counts in net worth and the debt countdown.', 80, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Looks like a new recurring charge', 'new_recurring', 'info', 'If it will keep coming, add it as a recurring item. It then gets a row in the Bills grid and a missed payment is noticed.', 90, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Spending is over a budget guideline', 'budget_pop', 'info', 'Open the lines behind it. If it was a one-off, tag it so it is easy to find; if it is the new normal, change the budget.', 100, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Income is short', 'income_short', 'info', 'Check whether a paycheck has not arrived yet, or was given the wrong category so it is not counted as income.', 110, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A plan has slipped', 'plan_wavered', 'info', 'Open the plan on the Planning screen. It shows how much more each month would put it back on track.', 120, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A plan is behind', 'plan_behind', 'info', 'Open the plan on the Planning screen to see how much more each month it needs, or move the target date if the goal has changed.', 130, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('A large one-time item', 'one_time_review', 'info', 'Check it really is a one-time cost. If it will come around again, give it a recurring item or a plan so it is not a surprise.', 140, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('Lines are waiting for review', 'review_needed', 'info', 'Open the lines tagged review and give each one the right category. Select several and fix them together.', 150, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
('The retirement gap grew', 'retirement_gap_grew', 'info', 'Look at the balances and the assumptions on the Retirement screen for anything out of date. It is a projection, not advice.', 160, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));
