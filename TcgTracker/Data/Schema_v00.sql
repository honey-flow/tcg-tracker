-- ==============================================================
-- Printings: Identity. What a card IS, independent of ownership.
-- One row per disctinct printing in the world.
-- ==============================================================
CREATE TABLE printings (
    printing_id      INTEGER PRIMARY KEY,
    game             TEXT    NOT NULL
                             CHECK (game IN ('pokemon', 'onepiece', 'mtg')),
    name             TEXT    NOT NULL,
    card_type        TEXT    NOT NULL DEFAULT 'none',
    set_code         TEXT    NOT NULL,
    collector_number TEXT    NOT NULL,
    finish           TEXT    NOT NULL DEFAULT 'normal',
    rarity           TEXT    NOT NULL DEFAULT 'none',
    image_uri        TEXT,
    external_id      TEXT,

-- ----- Pokemon Specific -----
    pkmn_promo_stamp TEXT    NOT NULL DEFAULT 'none',
    pkmn_edition     TEXT    NOT NULL DEFAULT 'none',

-- ----- One Piece Specific -----
    op_parallel_type TEXT    NOT NULL DEFAULT 'none',

-- ----- Magic The Gathering Specific -----
    mtg_frame_style  TEXT    NOT NULL DEFAULT 'none',

    UNIQUE (game, set_code, collector_number, finish, pkmn_promo_stamp, pkmn_edition, op_parallel_type, mtg_frame_style)
);

CREATE INDEX idx_printings_game_name ON printings (game, name);

-- ==============================================================
-- Locations: Where you physically keep things.
-- Small lookup table. Exists so location names are consistent
-- and so the UI can offer a dropdown instead of free text.
-- ==============================================================
CREATE TABLE locations (
    location_id INTEGER PRIMARY KEY,
    name        TEXT    NOT NULL UNIQUE,
    description TEXT,
    is_active   INTEGER NOT NULL DEFAULT 1
                        CHECK (is_active IN (0,1))
);

-- ==============================================================
-- Prices: Market value over time. A fact about the WORLD, not
-- about copies. Refreshed forever; acquisition price isn't.
-- ============================================================== 
CREATE TABLE prices (
    price_id    INTEGER PRIMARY KEY,
    printing_id INTEGER NOT NULL
                        REFERENCES  printings(printing_id),
    price_date  TEXT    NOT NULL,
    source      TEXT    NOT NULL,
    price_cents INTEGER NOT NULL,

    UNIQUE (printing_id, price_date, source)                  
);

CREATE INDEX idx_prices_printing_date ON prices (printing_id, price_date DESC);

-- ==============================================================
-- Sealed Purchases: A pack, box, or ETB you opened.
-- Exists because the purchase price belongs to the SEALED PRODUCT,
-- not to any card that came out of it.
-- ============================================================== 
CREATE TABLE sealed_purchases (
    purchase_id          INTEGER PRIMARY KEY,
    game                 TEXT    NOT NULL
                                 CHECK (game IN ('pokemon', 'onepiece', 'mtg')),
    product_type         TEXT    NOT NULL,
    product_name         TEXT    NOT NULL,
    set_code             TEXT,
    purchase_price_cents INTEGER NOT NULL,
    purchase_date        TEXT    NOT NULL,
    opened_date          TEXT,
    pack_count           INTEGER,
    status               TEXT    NOT NULL DEFAULT 'sealed'
                                 CHECK (status IN ('sealed', 'opened', 'sold')),
    sale_price_cents     INTEGER,
    sale_date            TEXT,
    notes                TEXT,
    created_at           TEXT    NOT NULL DEFAULT (datetime('now')),

-- Opened state coherence
    CHECK (
        (status = 'opened' AND opened_date IS NOT NULL)
        OR
        (status <> 'opened' AND opened_date IS NULL)
    ),

-- Sold stat coherence
    CHECK (
        (status = 'sold'  AND sale_price_cents IS NOT NULL
                          AND sale_date        IS NOT NULL)
        OR
        (status <> 'sold' AND sale_price_cents IS NULL
                          AND sale_date        IS NULL)
    )
);

CREATE INDEX idx_sealed_status ON sealed_purchases (status);

-- ==============================================================
-- Grading Submissions: A batch of cards set to a grader.
-- Exists because shipping and turnaround are facts about the 
-- BATCH, not about any card in it. A cost shared across many
-- items belongs to the thing that's shared. 
-- ============================================================== 
CREATE TABLE grading_submissions (
    submission_id         INTEGER PRIMARY KEY,
    company               TEXT    NOT NULL
                                  CHECK (company IN ('PSA', 'BGS', 'CGC', 'SGC')),
    order_number          TEXT,
    service_tier          TEXT    NOT NULL,
    status                TEXT    NOT NULL DEFAULT 'draft'
                                  CHECK (status IN ('draft', 'shipped', 'received', 'returned')),
    date_shipped          TEXT,
    date_returned         TEXT,
    shipping_out_cents    INTEGER,
    shipping_return_cents INTEGER,
    insurance_cents       INTEGER,
    notes                 TEXT,
    created_at            TEXT    NOT NULL DEFAULT (datetime('now')),

-- Draft state coherence
    CHECK (
        (status = 'draft'  AND date_shipped IS NULL)
        OR
        (status <> 'draft' AND date_shipped IS NOT NULL)
    ),

-- Returned state coherence
    CHECK (
        (status = 'returned'  AND date_returned IS NOT NULL)
        OR
        (status <> 'returned' AND date_returned IS NULL)
    )
);

CREATE INDEX idx_submissions_status ON grading_submissions (status);

-- ==============================================================
-- Copies: Instances. What you HAVE
-- One row per physical card. No quantity column -- two copies
-- of the same printing are two rows, because they can differ
-- in condition, price paid, and location.
-- ==============================================================
CREATE TABLE copies (
    copy_id                 INTEGER PRIMARY KEY,
    printing_id             INTEGER NOT NULL
                                    REFERENCES printings(printing_id),
    location_id             INTEGER NOT NULL
                                    REFERENCES locations(location_id),

-- ----- Condition and Grading -----
-- 'graded' is a condition value: once slabbed, the grade
-- supersedes raw condition as the description
    condition               TEXT    NOT NULL
                                    CHECK (condition IN ('NM', 'LP', 'MP', 'HP', 'DMG', 'graded')),
    grading_company         TEXT,
    grade                   REAL,
    cert_number             TEXT,

-- ----- Lifecycle -----
-- One column, not N booleans. Mutually exclusive states.
-- Adding a state adds a value here, not a column, and
-- doesn't break existing queries.
    status                  TEXT    NOT NULL DEFAULT 'owned'
                                    CHECK (status IN ('owned', 'sold', 'at_grader', 'lost', 'traded')),

-- The graded copy points back to the raw copy it came from.
-- Self-referencing FK. NULL for everything that supersedes
-- nothing, which is almost every row.
    supersedes_copy_id      INTEGER REFERENCES copies(copy_id),

-- Which grading batch this went out in. NULL unless graded.
    submission_id           INTEGER REFERENCES grading_submissions(submission_id),

-- ----- Acquisition: Immutable facts about the purchase -----
-- Money as INTEGER CENTS, not REAL. Floating point cant 
-- represent 0.10 exactly.   
    sealed_purchase_id      INTEGER REFERENCES sealed_purchases(purchase_id),
    acquisition_price_cents INTEGER,
    acquisition_date        TEXT,

-- ----- Grading Fee -----
-- What grading this card cost. Computed in C# at submission
-- time (rate cards change), stored because it can't be recomputed
-- honestly later
    grading_fee_cents       INTEGER,

-- ----- Sale -----
    sale_price_cents        INTEGER,
    sale_date               TEXT,
    created_at              TEXT    NOT NULL DEFAULT (datetime('now')),

-- Sold state coherence
    CHECK (
        (status = 'sold'  AND sale_price_cents IS NOT NULL
                          AND sale_date        IS NOT NULL)
        OR
        (status <> 'sold' AND sale_price_cents IS NULL
                          AND sale_date        IS NULL)
    ),

-- Graded state coherence
    CHECK (
        (condition = 'graded'  AND grading_company IS NOT NULL
                               AND grade           IS NOT NULL)
        OR
        (condition <> 'graded' AND grading_company IS NULL
                               AND grade           IS NULL)
    )
);

CREATE INDEX idx_copies_status   ON copies (status);
CREATE INDEX idx_copies_printing ON copies (printing_id);
CREATE INDEX idx_copies_location ON copies (location_id);

-- ----- Seed Data -----
INSERT OR IGNORE INTO locations (location_id, name, description)
    VALUES (1, 'Unsorted', 'Cards not yet assigned to a physical location');