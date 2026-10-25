ALTER TABLE accounts ADD COLUMN bank_number_hash VARCHAR(64);
ALTER TABLE accounts ADD COLUMN bank_number_enc TEXT;
DROP INDEX ux_accounts_bank_number;
CREATE UNIQUE INDEX ux_accounts_bank_hash ON accounts(institution, bank_number_hash);
