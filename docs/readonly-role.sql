-- Run once as a superuser (or the database owner) against the Twitch bot's database.
-- The app already runs every query in a READ ONLY transaction; this role is the real guarantee,
-- because a role without write privileges can't change data no matter what SQL is typed.

CREATE ROLE extractor_ro LOGIN PASSWORD 'change-me';

-- Belt and braces: new transactions default to read-only for this role.
ALTER ROLE extractor_ro SET default_transaction_read_only = on;

-- Replace "twitch" with your database name.
GRANT CONNECT ON DATABASE twitch TO extractor_ro;

-- Repeat this block for every schema you want to query (e.g. analytics).
GRANT USAGE ON SCHEMA public TO extractor_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO extractor_ro;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO extractor_ro;

-- Make tables the bot creates later readable too. Run this as the role that creates the tables
-- (usually the bot's own user), or add "FOR ROLE <bot_user>".
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO extractor_ro;
