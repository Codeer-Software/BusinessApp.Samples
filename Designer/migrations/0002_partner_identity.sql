-- 取引先の素性の列を足す（docs/13 §1-2）。正典は ddl/004_masters.sql の partners。
-- entity_type は画面では必須・DB は NULL 可（既存の行と「まだ分類していない」を偽の値で埋めない）。
ALTER TABLE partners ADD COLUMN address TEXT;
ALTER TABLE partners ADD COLUMN entity_type TEXT CHECK (entity_type IN ('corporation', 'sole_proprietor', 'unincorporated_association', 'other'));
ALTER TABLE partners ADD COLUMN corporate_number TEXT CHECK (corporate_number IS NULL OR corporate_number GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]');
ALTER TABLE partners ADD COLUMN parent_partner_id INTEGER REFERENCES partners(id) CHECK (parent_partner_id IS NULL OR parent_partner_id <> id);
