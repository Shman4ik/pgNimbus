-- pgNimbus demo data — 02: the richly-typed `commerce` schema.
--
-- The showcase schema. Exercises custom enums, a composite type, a domain,
-- and columns spanning uuid, arrays, jsonb, hstore, inet/cidr, geometric
-- point/box, ranges, money, interval, bytea, pgvector, and generated +
-- tsvector columns. Safe to re-run (schema is dropped and recreated).

BEGIN;

DROP SCHEMA IF EXISTS commerce CASCADE;
CREATE SCHEMA commerce;

-- --- Custom types ----------------------------------------------------------
CREATE TYPE commerce.order_status     AS ENUM ('cart','pending','paid','packed','shipped','delivered','cancelled','refunded');
CREATE TYPE commerce.payment_method   AS ENUM ('card','paypal','apple_pay','google_pay','bank_transfer','crypto','gift_card');
CREATE TYPE commerce.product_category AS ENUM ('electronics','home','apparel','books','toys','grocery','sports','beauty','automotive');
CREATE TYPE commerce.review_sentiment AS ENUM ('negative','neutral','positive');

-- Composite type (shows up as a real type in the schema tree)
CREATE TYPE commerce.address AS (
    street  text,
    city    text,
    region  text,
    postal  text,
    country char(2)
);

-- Domain over citext with validation
CREATE DOMAIN commerce.email_addr AS citext
    CHECK (VALUE ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$');

-- --- Customers -------------------------------------------------------------
CREATE TABLE commerce.customers (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email          commerce.email_addr UNIQUE NOT NULL,
    first_name     text NOT NULL,
    last_name      text NOT NULL,
    full_name      text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED,
    tags           text[] NOT NULL DEFAULT '{}',
    interests      jsonb  NOT NULL DEFAULT '{}'::jsonb,
    attrs          hstore,
    loyalty_tier   smallint NOT NULL DEFAULT 0 CHECK (loyalty_tier BETWEEN 0 AND 5),
    loyalty_points integer  NOT NULL DEFAULT 0,
    signup_at      timestamptz NOT NULL DEFAULT now(),
    last_login_ip  inet,
    home_network   cidr,
    home_location  point,
    membership     daterange,
    avatar_thumb   bytea,
    search         tsvector GENERATED ALWAYS AS
                     (to_tsvector('simple', first_name || ' ' || last_name || ' ' || email)) STORED
);
COMMENT ON TABLE  commerce.customers               IS 'Richly-typed customer records: arrays, jsonb, hstore, inet/cidr, geometric point, ranges, bytea, generated + tsvector columns.';
COMMENT ON COLUMN commerce.customers.home_location IS 'Approximate (lon,lat) as a native point.';
COMMENT ON COLUMN commerce.customers.search        IS 'Generated full-text search vector.';

INSERT INTO commerce.customers
    (email, first_name, last_name, tags, interests, attrs, loyalty_tier, loyalty_points,
     signup_at, last_login_ip, home_network, home_location, membership, avatar_thumb)
SELECT
    'c' || g || '.' || floor(random()*100000)::int || '@shop.example',
    (ARRAY['Emma','Liam','Olivia','Noah','Ava','Ethan','Sophia','Mason','Isabella','Lucas',
           'Mia','Oliver','Amelia','Elijah','Harper','Aria','Leo','Nora','Kai','Zoe'])[1+floor(random()*20)::int],
    (ARRAY['Smith','Nguyen','Kowalski','García','Müller','Rossi','Andersson','Yamamoto','Okafor','Silva',
           'Petrov','Haddad','O''Brien','Novak','Dubois','Costa','Ivanov','Khan','Weber','Santos'])[1+floor(random()*20)::int],
    (ARRAY['vip','newsletter','wholesale','beta','mobile','loyal','gift','early-access'])[1:1+floor(random()*4)::int],
    jsonb_build_object(
        'theme', (ARRAY['light','dark','system'])[1+floor(random()*3)::int],
        'currency', (ARRAY['USD','EUR','GBP','JPY'])[1+floor(random()*4)::int],
        'notifications', jsonb_build_object('email', random()>0.3, 'sms', random()>0.7),
        'categories', to_jsonb((ARRAY['electronics','home','books','sports'])[1:1+floor(random()*3)::int])
    ),
    hstore(ARRAY['referrer', (ARRAY['google','friend','ad','organic'])[1+floor(random()*4)::int],
                 'lang', (ARRAY['en','de','fr','ja','pt'])[1+floor(random()*5)::int]]),
    floor(random()*6)::int,
    floor(random()*50000)::int,
    now() - (random()*900 || ' days')::interval,
    ('192.168.' || floor(random()*255)::int || '.' || floor(random()*255)::int)::inet,
    ('10.' || floor(random()*255)::int || '.0.0/16')::cidr,
    point(round((random()*360-180)::numeric,4), round((random()*180-90)::numeric,4)),
    daterange((now() - (random()*900||' days')::interval)::date,
              (now() + (random()*400||' days')::interval)::date),
    decode(md5(g::text), 'hex')  -- 16-byte pseudo "thumbnail" blob
FROM generate_series(1, 800) g;

-- --- Products --------------------------------------------------------------
CREATE TABLE commerce.products (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku             citext UNIQUE NOT NULL,
    name            text   NOT NULL,
    category        commerce.product_category NOT NULL,
    price           numeric(12,2) NOT NULL CHECK (price > 0),
    cost            money         NOT NULL,
    attributes      jsonb         NOT NULL DEFAULT '{}'::jsonb,
    dimensions_cm   box,
    tags            text[]        NOT NULL DEFAULT '{}',
    rating          numeric(2,1)  CHECK (rating BETWEEN 0 AND 5),
    embedding       vector(3),
    weight_grams    integer,
    in_stock        boolean       NOT NULL DEFAULT true,
    lead_time       interval,
    price_band      numrange,
    added_at        timestamptz   NOT NULL DEFAULT now(),
    discontinued_at timestamptz,
    search          tsvector GENERATED ALWAYS AS (to_tsvector('english', name)) STORED
);
COMMENT ON TABLE commerce.products IS 'Product catalog exercising enum, money, box, vector (pgvector), interval, numrange, jsonb and a generated tsvector.';

INSERT INTO commerce.products
    (sku, name, category, price, cost, attributes, dimensions_cm, tags, rating,
     embedding, weight_grams, in_stock, lead_time, price_band, added_at, discontinued_at)
SELECT
    'PRD-' || lpad(g::text, 6, '0'),
    (ARRAY['Wireless','Ergonomic','Premium','Compact','Portable','Smart','Vintage','Rugged','Ultra','Eco',
           'Deluxe','Classic','Pro','Mini','Max'])[1+floor(random()*15)::int]
      || ' ' ||
    (ARRAY['Mouse','Keyboard','Headphones','Monitor','Webcam','Charger','Speaker','Hub','Stand','Cable',
           'Backpack','Bottle','Notebook','Lamp','Mug','Jacket','Sneakers','Novel','Drone','Blender'])[1+floor(random()*20)::int],
    (enum_range(NULL::commerce.product_category))[1+floor(random()*9)::int],
    round((random()*900 + 5)::numeric, 2),
    (round((random()*400 + 2)::numeric, 2))::money,
    jsonb_build_object(
        'color', (ARRAY['black','white','silver','blue','red','green'])[1+floor(random()*6)::int],
        'warranty_months', (ARRAY[6,12,24,36])[1+floor(random()*4)::int],
        'specs', jsonb_build_object('bt', random()>0.5, 'usb_c', random()>0.4, 'wattage', floor(random()*120))
    ),
    box(point(0,0), point(round((random()*40+1)::numeric,1), round((random()*30+1)::numeric,1))),
    (ARRAY['sale','new','bestseller','limited','clearance','featured'])[1:1+floor(random()*3)::int],
    round((random()*4 + 1)::numeric, 1),
    ARRAY[round(random()::numeric,4), round(random()::numeric,4), round(random()::numeric,4)]::vector,
    floor(random()*3000 + 50)::int,
    random() > 0.1,
    (floor(random()*14)+1 || ' days')::interval,
    numrange(round((random()*100)::numeric,2), round((random()*100+100)::numeric,2)),
    now() - (random()*400 || ' days')::interval,
    CASE WHEN random() < 0.08 THEN now() - (random()*30||' days')::interval END
FROM generate_series(1, 300) g;

-- --- Orders ----------------------------------------------------------------
CREATE TABLE commerce.orders (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    order_no        bigint GENERATED ALWAYS AS IDENTITY,
    customer_id     uuid NOT NULL REFERENCES commerce.customers(id) ON DELETE CASCADE,
    status          commerce.order_status   NOT NULL DEFAULT 'pending',
    payment         commerce.payment_method,
    ship_to         commerce.address,
    subtotal        numeric(12,2) NOT NULL,
    tax             numeric(12,2) NOT NULL DEFAULT 0,
    total           money         NOT NULL,
    currency        char(3)       NOT NULL DEFAULT 'USD',
    placed_at       timestamptz   NOT NULL DEFAULT now(),
    delivery_window tstzrange,
    notes           text
);
COMMENT ON TABLE commerce.orders IS 'Orders with a composite address type, tstzrange delivery window, money total and enum status/payment.';

INSERT INTO commerce.orders
    (customer_id, status, payment, ship_to, subtotal, tax, total, currency, placed_at, delivery_window, notes)
SELECT
    cust.a[s.ci],
    (enum_range(NULL::commerce.order_status))[s.si],
    (enum_range(NULL::commerce.payment_method))[s.pi],
    ROW(s.house || ' ' || (ARRAY['Main','Oak','Maple','Elm','Cedar','Pine'])[s.streeti] || ' St',
        (ARRAY['Berlin','Paris','Tokyo','Lisbon','Madrid','Oslo','Prague','Milan'])[s.cityi],
        (ARRAY['BE','IDF','TK','LX','MD','OS','PR','MI'])[s.cityi],
        lpad(s.postal::text, 5, '0'),
        (ARRAY['DE','FR','JP','PT','ES','NO','CZ','IT'])[s.cityi]
    )::commerce.address,
    s.subtotal,
    round(s.subtotal * 0.19, 2),
    (round(s.subtotal * 1.19, 2))::money,
    (ARRAY['USD','EUR','GBP','JPY'])[s.curi],
    s.placed,
    tstzrange(s.placed + interval '2 days', s.placed + interval '5 days'),
    CASE WHEN s.notei <= 5 THEN (ARRAY['Leave at door','Gift wrap please','Call on arrival','Fragile','No contact delivery'])[s.notei] END
FROM (SELECT array_agg(id) a FROM commerce.customers) cust
CROSS JOIN (
    SELECT
        1 + floor(random() * (SELECT count(*) FROM commerce.customers))::int AS ci,
        1 + floor(random()*8)::int  AS si,
        1 + floor(random()*7)::int  AS pi,
        1 + floor(random()*6)::int  AS streeti,
        1 + floor(random()*8)::int  AS cityi,
        1 + floor(random()*4)::int  AS curi,
        1 + floor(random()*20)::int AS notei,   -- 1..5 => note, else null
        floor(random()*9999)::int   AS house,
        floor(random()*99999)::int  AS postal,
        round((random()*800 + 10)::numeric, 2) AS subtotal,
        now() - (random()*365 || ' days')::interval AS placed
    FROM generate_series(1, 3000) g
) s;

-- --- Order items (two-column composite PK) ---------------------------------
CREATE TABLE commerce.order_items (
    order_id   uuid   NOT NULL REFERENCES commerce.orders(id) ON DELETE CASCADE,
    product_id bigint NOT NULL REFERENCES commerce.products(id),
    quantity   integer NOT NULL CHECK (quantity > 0),
    unit_price numeric(12,2) NOT NULL,
    discount   numeric(4,3)  NOT NULL DEFAULT 0 CHECK (discount >= 0 AND discount < 1),
    PRIMARY KEY (order_id, product_id)
);
COMMENT ON TABLE commerce.order_items IS 'Order line items with a two-column composite primary key.';

INSERT INTO commerce.order_items (order_id, product_id, quantity, unit_price, discount)
SELECT DISTINCT ON (s.oid, prod.a[s.pi])
    s.oid, prod.a[s.pi], s.qty, prod.pr[s.pi], s.disc
FROM (SELECT array_agg(id ORDER BY id) a, array_agg(price ORDER BY id) pr FROM commerce.products) prod
CROSS JOIN (
    SELECT
        o.id AS oid,
        1 + floor(random() * (SELECT count(*) FROM commerce.products))::int AS pi,
        1 + floor(random()*5)::int AS qty,
        round((random()*0.3)::numeric, 3) AS disc
    FROM commerce.orders o
    CROSS JOIN LATERAL generate_series(1, 1 + (o.order_no % 4)) li
) s;

-- --- Reviews ---------------------------------------------------------------
CREATE TABLE commerce.reviews (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_id  bigint NOT NULL REFERENCES commerce.products(id) ON DELETE CASCADE,
    customer_id uuid   REFERENCES commerce.customers(id) ON DELETE SET NULL,
    rating      smallint NOT NULL CHECK (rating BETWEEN 1 AND 5),
    title       text,
    body        text,
    sentiment   commerce.review_sentiment,
    aspects     text[],
    helpful     integer NOT NULL DEFAULT 0,
    metadata    jsonb,
    posted_at   timestamptz NOT NULL DEFAULT now(),
    search      tsvector GENERATED ALWAYS AS
                  (to_tsvector('english', coalesce(title,'') || ' ' || coalesce(body,''))) STORED
);
COMMENT ON TABLE commerce.reviews IS 'Product reviews with enum sentiment, text[] aspects and a generated tsvector over title+body.';

INSERT INTO commerce.reviews (product_id, customer_id, rating, title, body, sentiment, aspects, helpful, metadata, posted_at)
SELECT
    prod.a[s.pi],
    cust.a[s.ci],
    s.rating,
    (ARRAY['Love it','Not bad','Disappointed','Exceeded expectations','Would buy again','Meh','Fantastic','Broke quickly'])[s.titlei],
    'This product is ' || (ARRAY['amazing and well built','decent for the price','not what I expected','the best I have owned','a bit flimsy'])[s.bodyi]
      || '. Shipping was ' || (ARRAY['fast','slow','on time'])[s.shipi] || '.',
    (CASE WHEN s.rating >= 4 THEN 'positive' WHEN s.rating = 3 THEN 'neutral' ELSE 'negative' END)::commerce.review_sentiment,
    (ARRAY['quality','price','shipping','design','durability','support'])[1:1+floor(random()*3)::int],
    s.helpful,
    jsonb_build_object('verified_purchase', s.vp, 'edited', s.edited),
    s.posted
FROM (SELECT array_agg(id) a FROM commerce.products)  prod
CROSS JOIN (SELECT array_agg(id) a FROM commerce.customers) cust
CROSS JOIN (
    SELECT
        1 + floor(random() * (SELECT count(*) FROM commerce.products))::int  AS pi,
        1 + floor(random() * (SELECT count(*) FROM commerce.customers))::int AS ci,
        1 + floor(random()*5)::int AS rating,
        1 + floor(random()*8)::int AS titlei,
        1 + floor(random()*5)::int AS bodyi,
        1 + floor(random()*3)::int AS shipi,
        floor(random()*200)::int   AS helpful,
        random() > 0.2  AS vp,
        random() > 0.85 AS edited,
        now() - (random()*300 || ' days')::interval AS posted
    FROM generate_series(1, 2000) g
) s;

COMMIT;
