-- TechEmpower-compatible PostgreSQL schema and seed data.
-- Run once before executing the TFB-style benchmark scenarios.
--
-- Usage:
--   psql -h localhost -U benchmarkdbuser -d hello_world -f setup_db.sql
--
-- Or via docker:
--   docker exec -i <pg-container> psql -U benchmarkdbuser -d hello_world < setup_db.sql

-- ── world table ──────────────────────────────────────────────────────────────
-- Matches TechEmpower spec: 10,000 rows, id 1-10000, randomNumber 1-10000.

DROP TABLE IF EXISTS world;

CREATE TABLE world (
    id           INTEGER NOT NULL,
    randomnumber INTEGER NOT NULL,
    PRIMARY KEY (id)
);

INSERT INTO world (id, randomnumber)
SELECT
    s.id,
    floor(random() * 10000 + 1)::INTEGER
FROM generate_series(1, 10000) AS s(id);

-- ── fortune table ─────────────────────────────────────────────────────────────
-- Matches TechEmpower spec: 12 rows from the original Fortunes benchmark.

DROP TABLE IF EXISTS fortune;

CREATE TABLE fortune (
    id      INTEGER      NOT NULL,
    message VARCHAR(2048) NOT NULL,
    PRIMARY KEY (id)
);

INSERT INTO fortune (id, message) VALUES
    (1,  'fortune: No such file or directory'),
    (2,  'A computer scientist is someone who fixes things that aren''t broken.'),
    (3,  'After enough decimal places, nobody gives a damn.'),
    (4,  'A bad random number generator: 1, 1, 1, 1, 1, 4.33e+67, 1, 1, 1'),
    (5,  'A computer program does what you tell it to do, not what you want it to do.'),
    (6,  'Emacs is a nice operating system, but I prefer UNIX. — Tom Christaensen'),
    (7,  'Any program that runs right is obsolete.'),
    (8,  'A list is only as strong as its weakest link. — Donald Knuth'),
    (9,  'Feature: A bug with seniority.'),
    (10, 'Computers make very fast, very accurate mistakes.'),
    (11, '<script>alert("This should not be displayed in a browser alert box.");</script>'),
    (12, 'フレームワークのベンチマーク');
