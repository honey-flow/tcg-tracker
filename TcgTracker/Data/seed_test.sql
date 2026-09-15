-- ----- Locations
INSERT OR IGNORE INTO locations (location_id, name, description)
    VALUES (2, 'Binder 1', 'Blue 9-pocket binder, shelf');

-- ----- Printings
INSERT INTO printings (game, name, set_code, collector_number, finish, rarity, pkmn_edition)
  VALUES ('pokemon', 'Charizard', 'BS', '4', 'holo', 'Rare Holo', '1st');

INSERT INTO printings (game, name, set_code, collector_number, finish, rarity)
  VALUES ('pokemon', 'Pikachu', 'SV1', '25', 'reverse', 'Common');

INSERT INTO printings (game, name, card_type, set_code, collector_number, finish, rarity, op_parallel_type)
  VALUES ('onepiece', 'Monkey D. Luffy', 'Leader', 'OP01', '001', 'normal', 'L', 'manga art');

INSERT INTO printings (game, name, card_type, set_code, collector_number, finish, rarity, mtg_frame_style)
  VALUES ('mtg', 'Lightning Bolt', 'Instant', 'LEA', '161', 'normal', 'Common', 'showcase');

-- ----- Copies
-- 1. Raw, owned, full acquisition data. The common case.
INSERT INTO copies (printing_id, location_id, condition, status, acquisition_price_cents, acquisition_date)
  VALUES (2, 2, 'NM', 'owned', 150, '2024-03-11');

-- 2. Raw, owned, NULL acquisition price -- a pull from a pack
INSERT INTO copies (printing_id, location_id, condition, status)
  VALUES (3, 2, 'LP', 'owned');

-- 3. Raw, sold. Exercises the sold-state CHECK's positive branch
INSERT INTO copies (printing_id, location_id, condition, status, acquisition_price_cents, acquisition_date, sale_price_cents, sale_date)
  VALUES (4, 1, 'MP', 'sold', 800, '2023-07-02', 2200, '2025-01-19');

-- 4. Graded slab. Exercises the graded-state CHECK and the supercedes chain.
--    First the raw copy it came from, now marked  at_grader...
INSERT INTO copies (printing_id, location_id, condition, status, acquisition_price_cents, acquisition_date)
  VALUES (1, 1, 'NM', 'at_grader', 45000, '2022-11-30');

--    ...Then the slab that came back, pointing at it
INSERT INTO copies (printing_id, location_id, condition, status, grading_company, grade, cert_number, supersedes_copy_id, acquisition_price_cents, acquisition_date, grading_fee_cents)
  VALUES (1, 2, 'graded', 'owned', 'PSA', 9.0, '78451203', 4, 45000, '2022-11-30', 7500);

-- ----- Prices
INSERT INTO prices (printing_id, price_date, source, price_cents)
  VALUES (1, '2025-09-01', 'tcgplayer_market', 512000),
         (1, '2025-09-08', 'tcgplayer_market', 498000),
         (1, '2025-09-13', 'tcgplayer_market', 521000);