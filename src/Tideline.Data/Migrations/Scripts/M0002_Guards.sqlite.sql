CREATE TRIGGER tr_audit_log_no_update BEFORE UPDATE ON audit_log
BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER tr_audit_log_no_delete BEFORE DELETE ON audit_log
BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;

CREATE TRIGGER tr_tx_bank_fields_readonly BEFORE UPDATE OF account_id, posted_on, amount_cents, original_description,
    bank_balance_cents, check_number, external_id, source ON transactions
WHEN OLD.account_id IS NOT NEW.account_id OR OLD.posted_on IS NOT NEW.posted_on OR OLD.amount_cents IS NOT NEW.amount_cents
  OR OLD.original_description IS NOT NEW.original_description OR OLD.bank_balance_cents IS NOT NEW.bank_balance_cents
  OR OLD.check_number IS NOT NEW.check_number OR OLD.external_id IS NOT NEW.external_id OR OLD.source IS NOT NEW.source
BEGIN SELECT RAISE(ABORT, 'bank fields are read-only'); END;

CREATE TRIGGER tr_tx_no_delete_imported BEFORE DELETE ON transactions
WHEN OLD.source <> 'manual'
BEGIN SELECT RAISE(ABORT, 'imported transactions cannot be deleted'); END;
