ALTER TABLE transactions ADD COLUMN left_out_at VARCHAR(32);
ALTER TABLE transactions ADD COLUMN left_out_reason VARCHAR(200);
ALTER TABLE transactions ADD COLUMN left_out_import_id INTEGER REFERENCES import_batches(id);
