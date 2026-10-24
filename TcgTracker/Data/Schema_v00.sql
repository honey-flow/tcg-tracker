PRAGMA user_version = 0;

CREATE TABLE schema_meta (
    schema_meta_id INTEGER PRIMARY KEY CHECK (schema_meta_id = 1),
    schema_sha256  TEXT    NOT NULL    CHECK (length(schema_sha256) = 64),
    created_at     TEXT    NOT NULL 
);

-- ==============================================================
-- Printings: Identity. What a card IS, independent of ownership.
-- One row per distinct printing in the world.
-- ==============================================================
CREATE TABLE printings (
    printing_id        INTEGER PRIMARY KEY,
    game               TEXT    NOT NULL
                               CHECK (game IN ('pokemon', 'onepiece', 'mtg')),
    name               TEXT    NOT NULL,
    card_type          TEXT    NOT NULL DEFAULT 'none',
    set_code           TEXT    NOT NULL,
    collector_number   TEXT    NOT NULL,
    finish             TEXT    NOT NULL DEFAULT 'normal',
    rarity             TEXT    NOT NULL DEFAULT 'none',
    image_uri          TEXT,

    print_variant      TEXT    NOT NULL DEFAULT 'none',
    variant_key        TEXT    NOT NULL DEFAULT 'none',

-- ----- Source Identified: Scryfall UUID, TCGdex card_id, and optcgapi card_image_id -----
    source_id          TEXT,

-- ----- Source Name -----
    source_name        TEXT,

-- ----- TCGPlayer product ID -----
    tcgplayer_id       INTEGER,

-- ----- Cardmarket product ID (EUR prices) -----
    cardmarket_id      INTEGER,

-- ----- Complete API Response, verbatim -----
    raw_json           TEXT,

-- ----- Pokemon Specific -----
    pkmn_promo_stamp   TEXT    NOT NULL DEFAULT 'none',

-- ----- One Piece Specific -----
    op_parallel_type   TEXT    NOT NULL DEFAULT 'none',

-- ----- Magic The Gathering Specific -----
    mtg_frame_effects  TEXT    NOT NULL DEFAULT 'none',

    created_at         TEXT    NOT NULL DEFAULT (datetime('now')),

    UNIQUE (game, set_code, collector_number, finish, print_variant, variant_key, pkmn_promo_stamp, op_parallel_type)
);

CREATE TABLE variant_collisions ( 
    collision_id    INTEGER PRIMARY KEY,
    game            TEXT    NOT NULL, 
    source_name     TEXT    NOT NULL,
    source_card_id  TEXT    NOT NULL,

    collision_count INTEGER NOT NULL,

    detected_at     TEXT    NOT NULL DEFAULT (datetime('now')),

    resolved_at     TEXT,

    UNIQUE (source_name, source_card_id)
);

CREATE INDEX idx_printings_game_name ON printings (game, name);

CREATE INDEX idx_printings_tcgplayer ON printings (tcgplayer_id);

-- ==============================================================
-- Locations: Where you physically keep things.
-- A tree -- cabinet > drawer > box. parent_location_id points at
-- another location. NULL means top level. Copies may point at
-- ANY level.
-- ==============================================================
CREATE TABLE locations (
    location_id        INTEGER PRIMARY KEY,

    parent_location_id INTEGER REFERENCES locations(location_id),

    name               TEXT    NOT NULL,
    description        TEXT,

    is_active          INTEGER NOT NULL DEFAULT 1
                               CHECK (is_active IN (0,1)),

    is_sale_locked     INTEGER NOT NULL DEFAULT 0
                               CHECK (is_sale_locked IN (0,1))
);

CREATE UNIQUE INDEX idx_locations_sibling ON locations (parent_location_id, name)
    WHERE parent_location_id IS NOT NULL;

CREATE UNIQUE INDEX idx_locations_toplevel ON locations (name)
    WHERE parent_location_id IS NULL;

CREATE INDEX idx_locations_parent ON locations (parent_location_id);

-- ==============================================================
-- Prices: Two tables because two questions asked.
-- current_prices: "what is this card worth today?" Every
--  priced printing , one row per number the source gives,
--  overwritten on each refresh. Fast columns for search
--  and sorting

-- price_snapshots: "what was it worth over time?" Only 
--  printings you hold. The source's pricing block VERBATIM.
--  Prices can't be re-fetched, so history keeps the source's
--  own words; interpreting them is code.
--
-- Graded prices (the old grade_key) need no column:
--  a graded-price source will simply report metrics like 'psa10'
-- ============================================================== 
CREATE TABLE current_prices (
    printing_id   INTEGER NOT NULL REFERENCES printings(printing_id),

    source        TEXT    NOT NULL
                          CHECK (source IN ('scryfall', 'tcgdex', 'optcgapi')),

    metric        TEXT    NOT NULL,

    currency      TEXT    NOT NULL
                          CHECK (currency IN ('USD', 'EUR')),
    
    amount_cents INTEGER NOT NULL
                          CHECK (amount_cents >= 0),

    -- source's date for the entry, not the date that it was pulled
    as_of         TEXT    NOT NULL,
    fetched_at    TEXT    NOT NULL,

    PRIMARY KEY (printing_id, source, metric)
);

CREATE TABLE price_snapshots (
    snapshot_id INTEGER PRIMARY KEY,
    printing_id INTEGER NOT NULL REFERENCES printings(printing_id),

    source      TEXT    NOT NULL
                          CHECK (source IN ('scryfall', 'tcgdex', 'optcgapi')),

    as_of       TEXT    NOT NULL,

    payload     TEXT    NOT NULL
                        CHECK (json_valid(payload)),

    fetched_at  TEXT    NOT NULL,

    UNIQUE (printing_id, source, as_of) 
);

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

-- =========================================================================================================================
-- Catalog Issues: What the catalog may be missing or got wrong, and why.
-- Exists so gaps are visible to the user, not silent
-- held_out   : the source lists it, the import couldn't store it
-- merged     : the source lists it more than once with identical data; stored once
-- corrected  : the import overrode a confirmed source error
-- stale      : stored, but the source no longer lists it
-- limitation : a known gap no import can detect (seeded below)
-- Rows are never deleted. An issue not seen by a later complete run gets resolved_at; one that comes back is reopened
-- =========================================================================================================================
CREATE TABLE catalog_issues (
    issue_id      INTEGER PRIMARY KEY,
    game          TEXT    NOT NULL
                          CHECK (game IN ('pokemon', 'onepiece', 'mtg')),
    source_name   TEXT    NOT NULL,
    kind          TEXT    NOT NULL
                          CHECK (kind IN ('held_out', 'merged', 'corrected', 'stale', 'limitation')),
    issue_key     TEXT    NOT NULL,
    summary       TEXT    NOT NULL,
    detail        TEXT,
    first_seen_at TEXT    NOT NULL,
    last_seen_at  TEXT    NOT NULL,
    resolved_at   TEXT,

    UNIQUE (game, source_name, kind, issue_key) 
);

CREATE INDEX idx_catalog_issues_open ON catalog_issues (game, resolved_at);

-- ----- Seed: Known Limitations -----
INSERT INTO catalog_issues (game, source_name, kind, issue_key, summary, detail, first_seen_at, last_seen_at)
    VALUES ('onepiece', 'optcgapi', 'limitation', 'starter-deck-reprints',
            'Starter-deck reprints identical to the original are recorded as the original.', 
            'optcgapi gives them the same id and the same data, so the app cannot tell them apart.',
            datetime('now'), datetime('now')),
           ('onepiece', 'optcgapi', 'limitation', 'unlisted-printings',
            'Some printings are not listed by optcgapi at all and are missing from the catalog.',
            'Typically promotional or event cards. Manual entry is planned.',
            datetime('now'), datetime('now')),
           ('onepiece', 'optcgapi', 'limitation', 'don-numbering',
            'DON!! card numbers are optcgapi''s internal counter, not printed on the card.',
            'Search DON!! cards by name, not number.',
            datetime('now'), datetime('now')); 

-- ----- Seed Data -----
INSERT OR IGNORE INTO locations (location_id, name, description)
    VALUES (1, 'Unsorted', 'Cards not yet assigned to a physical location');