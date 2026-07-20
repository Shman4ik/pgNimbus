-- pgNimbus demo data — 01: the simple `public` shop.
--
-- Creates and populates a small, approachable e-commerce schema plus the
-- PostgreSQL extensions the richer schemas (02-05) rely on. Safe to re-run:
-- the public.* demo tables are dropped and recreated.
--
-- Randomness note: every random() call lives in a subquery target list over
-- generate_series (evaluated per row), and related rows are chosen by indexing
-- into an array of candidate ids. Do NOT "pick a random row" with an
-- uncorrelated `CROSS JOIN LATERAL (SELECT ... ORDER BY random() LIMIT 1)` —
-- PostgreSQL evaluates that once and every row gets the SAME pick.

BEGIN;

CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS hstore;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS ltree;
CREATE EXTENSION IF NOT EXISTS vector;

DROP TABLE IF EXISTS public.order_items CASCADE;
DROP TABLE IF EXISTS public.orders      CASCADE;
DROP TABLE IF EXISTS public.products    CASCADE;
DROP TABLE IF EXISTS public.customers   CASCADE;

CREATE TABLE public.customers (
    id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name varchar(50)  NOT NULL,
    last_name  varchar(50)  NOT NULL,
    email      varchar(255) UNIQUE NOT NULL,
    created_at timestamptz  NOT NULL DEFAULT now(),
    is_active  boolean      NOT NULL DEFAULT true
);
CREATE TABLE public.products (
    id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title          varchar(200) NOT NULL,
    description    text,
    price          numeric(10,2) NOT NULL CHECK (price > 0),
    stock_quantity integer NOT NULL DEFAULT 0,
    sku            varchar(32) UNIQUE NOT NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE public.orders (
    id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id  integer NOT NULL REFERENCES public.customers(id) ON DELETE CASCADE,
    order_date   timestamptz NOT NULL DEFAULT now(),
    status       varchar(20) NOT NULL DEFAULT 'pending',
    total_amount numeric(10,2) NOT NULL DEFAULT 0
);
CREATE TABLE public.order_items (
    id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id   integer NOT NULL REFERENCES public.orders(id) ON DELETE CASCADE,
    product_id integer NOT NULL REFERENCES public.products(id),
    quantity   integer NOT NULL CHECK (quantity > 0),
    unit_price numeric(10,2) NOT NULL
);

COMMENT ON TABLE public.customers IS 'Retail customers (simple starter schema).';
COMMENT ON TABLE public.orders    IS 'Customer orders; see commerce.orders for the richly-typed variant.';

-- ~500 customers
INSERT INTO public.customers (first_name, last_name, email, created_at, is_active)
SELECT
    (ARRAY['Emma','Liam','Olivia','Noah','Ava','Ethan','Sophia','Mason','Isabella','Lucas',
           'Mia','Oliver','Amelia','Elijah','Harper','James','Evelyn','Benjamin','Abigail','Henry'])[1+floor(random()*20)::int],
    (ARRAY['Smith','Johnson','Williams','Brown','Jones','Garcia','Miller','Davis','Rodriguez','Martinez',
           'Hernandez','Lopez','Gonzalez','Wilson','Anderson','Thomas','Taylor','Moore','Jackson','Martin'])[1+floor(random()*20)::int],
    'user' || g || '_' || floor(random()*100000)::int || '@example.com',
    now() - (random()*730 || ' days')::interval,
    random() > 0.15
FROM generate_series(1, 500) g;

-- ~200 products
INSERT INTO public.products (title, description, price, stock_quantity, sku, updated_at)
SELECT
    (ARRAY['Wireless','Ergonomic','Premium','Compact','Portable','Smart','Vintage','Rugged','Ultra','Eco'])[1+floor(random()*10)::int]
      || ' ' ||
    (ARRAY['Mouse','Keyboard','Headphones','Monitor','Webcam','Charger','Speaker','Hub','Stand','Cable',
           'Backpack','Bottle','Notebook','Lamp','Mug'])[1+floor(random()*15)::int]
      || ' ' || g,
    'Auto-generated demo product #' || g || '. Great for showcasing PgNimbus result rendering.',
    round((random()*900 + 5)::numeric, 2),
    floor(random()*500)::int,
    'SKU-' || lpad(g::text, 6, '0'),
    now() - (random()*200 || ' days')::interval
FROM generate_series(1, 200) g;

-- ~2000 orders, each tied to a random customer
INSERT INTO public.orders (customer_id, order_date, status, total_amount)
SELECT cust.a[s.ci], s.od, s.st, s.amt
FROM (SELECT array_agg(id) a FROM public.customers) cust
CROSS JOIN (
    SELECT
        1 + floor(random() * (SELECT count(*) FROM public.customers))::int AS ci,
        now() - (random()*365 || ' days')::interval AS od,
        (ARRAY['pending','paid','shipped','delivered','cancelled','refunded'])[1+floor(random()*6)::int] AS st,
        round((random()*800 + 10)::numeric, 2) AS amt
    FROM generate_series(1, 2000) g
) s;

-- 1-4 line items per order (item count correlated on order id so it varies)
INSERT INTO public.order_items (order_id, product_id, quantity, unit_price)
SELECT s.oid, prod.a[s.pi], s.qty, prod.p[s.pi]
FROM (SELECT array_agg(id) a, array_agg(price) p FROM public.products) prod
CROSS JOIN (
    SELECT
        o.id AS oid,
        1 + floor(random() * (SELECT count(*) FROM public.products))::int AS pi,
        1 + floor(random()*4)::int AS qty
    FROM public.orders o
    CROSS JOIN LATERAL generate_series(1, 1 + (o.id % 4)) li
) s;

COMMIT;
