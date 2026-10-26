ALTER TABLE import_batches ADD COLUMN dismissed_at VARCHAR(32);  -- a failed import a person has looked at and cleared from the list; the row and its audit entry stay
