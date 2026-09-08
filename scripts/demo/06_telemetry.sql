-- pgNimbus demo data — 06: the `telemetry` schema, a deliberately hostile grid.
--
-- Every other file here shows types off; this one exists to be *heavy*. It is
-- the shape that made the results grid crawl in the field: one very wide table
-- (46 columns) whose four jsonb columns hold documents of tens to hundreds of
-- kilobytes each, so a single screen of rows carries several megabytes of text.
-- Keep it in the demo set — a change to the grid's cell rendering should be
-- scrolled through here before it ships.
--
-- Randomness note: the payload builders are correlated subqueries (they read
-- s.g from the outer row), so PostgreSQL evaluates them per row. Do NOT rewrite
-- them as uncorrelated `CROSS JOIN LATERAL (... ORDER BY random() LIMIT 1)` —
-- that is evaluated once and every row gets the same "random" pick.

BEGIN;

DROP SCHEMA IF EXISTS telemetry CASCADE;
CREATE SCHEMA telemetry;

CREATE TABLE telemetry.api_events (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_at           timestamptz NOT NULL,
    trace_id              uuid        NOT NULL,
    span_id               uuid        NOT NULL,
    parent_span_id        uuid,
    service               text        NOT NULL,
    service_version       text        NOT NULL,
    environment           text        NOT NULL,
    region                text        NOT NULL,
    availability_zone     text        NOT NULL,
    host                  text        NOT NULL,
    pod                   text        NOT NULL,
    container_id          text        NOT NULL,
    method                text        NOT NULL,
    route                 text        NOT NULL,
    path                  text        NOT NULL,
    query_string          text,
    status_code           integer     NOT NULL,
    status_class          smallint GENERATED ALWAYS AS (status_code / 100) STORED,
    is_error              boolean  GENERATED ALWAYS AS (status_code >= 400) STORED,
    duration_ms           numeric(10,3) NOT NULL,
    db_time_ms            numeric(10,3) NOT NULL,
    queue_time_ms         numeric(10,3) NOT NULL,
    bytes_in              bigint      NOT NULL,
    bytes_out             bigint      NOT NULL,
    client_ip             inet        NOT NULL,
    user_agent            text        NOT NULL,
    referrer              text,
    user_id               uuid,
    session_id            uuid,
    tenant                text        NOT NULL,
    api_key_prefix        text,
    rate_limit_remaining  integer     NOT NULL,
    cache_status          text        NOT NULL,
    retry_count           smallint    NOT NULL DEFAULT 0,
    error_code            text,
    error_message         text,
    stack_trace           text,
    request_headers       jsonb       NOT NULL,
    request_body          jsonb       NOT NULL,
    response_headers      jsonb       NOT NULL,
    response_body         jsonb       NOT NULL,
    trace_spans           jsonb       NOT NULL,
    context               jsonb       NOT NULL,
    feature_flags         jsonb       NOT NULL,
    tags                  text[]      NOT NULL DEFAULT '{}',
    recorded_at           timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  telemetry.api_events               IS 'Wide (46-column) request log with multi-100 KB jsonb payloads — the results-grid stress case.';
COMMENT ON COLUMN telemetry.api_events.request_body  IS 'Tens to hundreds of KB: the line items the request carried.';
COMMENT ON COLUMN telemetry.api_events.response_body IS 'Tens to hundreds of KB: the records the response returned.';
COMMENT ON COLUMN telemetry.api_events.trace_spans   IS 'One object per span of the distributed trace.';

INSERT INTO telemetry.api_events
    (received_at, trace_id, span_id, parent_span_id, service, service_version, environment,
     region, availability_zone, host, pod, container_id, method, route, path, query_string,
     status_code, duration_ms, db_time_ms, queue_time_ms, bytes_in, bytes_out, client_ip,
     user_agent, referrer, user_id, session_id, tenant, api_key_prefix, rate_limit_remaining,
     cache_status, retry_count, error_code, error_message, stack_trace,
     request_headers, request_body, response_headers, response_body, trace_spans,
     context, feature_flags, tags)
SELECT
    s.received_at,
    s.trace_id,
    s.span_id,
    CASE WHEN s.g % 4 <> 0 THEN s.parent_span_id END,
    (ARRAY['checkout-api','catalog-api','identity','search','billing','shipping'])[s.svci],
    '2.' || (s.g % 9) || '.' || (s.g % 17),
    (ARRAY['production','staging','canary'])[s.envi],
    (ARRAY['eu-central-1','us-east-1','ap-southeast-2'])[s.regi],
    (ARRAY['eu-central-1','us-east-1','ap-southeast-2'])[s.regi] || (ARRAY['a','b','c'])[s.azi],
    'ip-10-' || (s.g % 250) || '-' || ((s.g * 7) % 250) || '-' || ((s.g * 13) % 250),
    (ARRAY['checkout','catalog','identity','search','billing','shipping'])[s.svci] || '-' || substr(md5(s.g::text), 1, 10),
    substr(md5('container' || s.g), 1, 24),
    (ARRAY['GET','POST','PUT','PATCH','DELETE'])[s.methi],
    (ARRAY['/v1/orders/{id}','/v1/carts/{id}/items','/v1/products','/v1/search','/v1/invoices/{id}','/v1/shipments'])[s.routei],
    (ARRAY['/v1/orders/','/v1/carts/','/v1/products/','/v1/search/','/v1/invoices/','/v1/shipments/'])[s.routei] || s.g,
    CASE WHEN s.g % 3 = 0 THEN 'page=' || (s.g % 40) || '&limit=100&expand=items,customer' END,
    s.status_code,
    round(s.duration::numeric, 3),
    round((s.duration * 0.42)::numeric, 3),
    round((s.duration * 0.06)::numeric, 3),
    s.body_items * 180 + 512,
    s.result_rows * 240 + 1024,
    ('198.51.' || (s.g % 250) || '.' || ((s.g * 11) % 250))::inet,
    (ARRAY['Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15',
           'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36',
           'checkout-sdk/4.2.1 (dotnet; net10.0; win-x64)',
           'okhttp/4.12.0'])[s.uai],
    CASE WHEN s.g % 5 <> 0 THEN 'https://shop.example.com/checkout?step=' || (s.g % 6) END,
    s.user_id,
    s.session_id,
    'tenant-' || lpad(((s.g % 24) + 1)::text, 3, '0'),
    'pk_live_' || substr(md5('key' || (s.g % 30)), 1, 12),
    1000 - (s.g % 1000),
    (ARRAY['HIT','MISS','BYPASS','STALE'])[s.cachei],
    CASE WHEN s.status_code >= 500 THEN (s.g % 4)::smallint ELSE 0::smallint END,
    CASE WHEN s.status_code >= 400
         THEN (ARRAY['ERR_VALIDATION','ERR_UPSTREAM_TIMEOUT','ERR_CONFLICT','ERR_RATE_LIMIT','ERR_INTERNAL'])[s.erri] END,
    CASE WHEN s.status_code >= 400
         THEN 'Request ' || s.trace_id || ' failed after ' || round(s.duration::numeric, 1)
              || ' ms while calling the ' || (ARRAY['pricing','inventory','tax','payment','fraud'])[s.erri] || ' service' END,
    -- A few KB of plausible stack, so the long-text columns are heavy too.
    CASE WHEN s.status_code >= 500 THEN (
        SELECT string_agg(
            '   at Shop.' || (ARRAY['Checkout','Pricing','Inventory','Payments','Tax','Fraud'])[1 + (f % 6)]
                || '.' || (ARRAY['HandleAsync','ResolveAsync','ValidateAsync','CommitAsync','LoadAsync'])[1 + (f % 5)]
                || '(Order order, CancellationToken ct) in /src/shop/' || substr(md5((s.g * f)::text), 1, 8)
                || '.cs:line ' || (17 * f + s.g % 400),
            chr(10) ORDER BY f)
        FROM generate_series(1, 40) f) END,
    -- Small documents: the headers.
    jsonb_build_object(
        'accept', 'application/json',
        'accept-encoding', 'gzip, br',
        'authorization', 'Bearer ' || substr(md5('tok' || s.g), 1, 24) || '...',
        'content-type', 'application/json; charset=utf-8',
        'traceparent', '00-' || replace(s.trace_id::text, '-', '') || '-' || substr(replace(s.span_id::text, '-', ''), 1, 16) || '-01',
        'x-request-id', s.trace_id,
        'x-forwarded-for', '198.51.' || (s.g % 250) || '.' || ((s.g * 11) % 250),
        'user-agent', 'checkout-sdk/4.2.1'),
    -- The heavy one: the request's line items.
    (SELECT jsonb_agg(jsonb_build_object(
                'seq', i,
                'sku', 'SKU-' || lpad(((s.g * 97 + i * 13) % 999999)::text, 6, '0'),
                'title', 'Item ' || i || ' - ' || substr(md5((s.g * i)::text), 1, 18),
                'qty', 1 + (i % 7),
                'unit_price', round(((5 + ((s.g * i) % 900)) / 7.0)::numeric, 2),
                'currency', 'EUR',
                'warehouse', 'WH-' || (1 + (i % 12)),
                'attributes', jsonb_build_object(
                    'color', (ARRAY['black','white','silver','blue','red','green'])[1 + (i % 6)],
                    'size', (ARRAY['XS','S','M','L','XL'])[1 + (i % 5)],
                    'gift_wrap', (i % 9) = 0,
                    'note', 'handling note ' || substr(md5((i * 31 + s.g)::text), 1, 26)),
                'promotions', jsonb_build_array(
                    jsonb_build_object('code', 'PROMO-' || ((s.g + i) % 400), 'pct', (i % 30)),
                    jsonb_build_object('code', 'LOYALTY-' || (i % 12), 'pct', (i % 7)))))
     FROM generate_series(1, s.body_items) i),
    jsonb_build_object(
        'content-type', 'application/json; charset=utf-8',
        'content-encoding', 'br',
        'cache-control', 'private, max-age=0',
        'x-ratelimit-remaining', 1000 - (s.g % 1000),
        'server-timing', 'db;dur=' || round((s.duration * 0.42)::numeric, 1) || ', app;dur=' || round((s.duration * 0.5)::numeric, 1)),
    -- The other heavy one: the records the response carried back.
    jsonb_build_object(
        'page', 1 + (s.g % 40),
        'page_size', s.result_rows,
        'total', s.result_rows * (3 + (s.g % 5)),
        'results', (SELECT jsonb_agg(jsonb_build_object(
                'id', md5((s.g * 7919 + r)::text),
                'kind', (ARRAY['order','shipment','invoice','refund'])[1 + (r % 4)],
                'created_at', to_char(now() - ((r % 400) || ' days')::interval, 'YYYY-MM-DD"T"HH24:MI:SSOF'),
                'amount', round(((r * 37 + s.g) % 5000)::numeric / 13, 2),
                'lines', 1 + (r % 9),
                'customer', jsonb_build_object(
                    'id', md5((r * 31 + s.g)::text),
                    'name', 'Customer ' || ((r * 17 + s.g) % 9000),
                    'email', 'c' || ((r * 17 + s.g) % 9000) || '@example.com',
                    'segment', (ARRAY['retail','wholesale','partner','internal'])[1 + (r % 4)]),
                'summary', 'Processed ' || (1 + (r % 9)) || ' lines for ' || substr(md5((r + s.g)::text), 1, 20)))
            FROM generate_series(1, s.result_rows) r)),
    -- The trace: one object per span.
    (SELECT jsonb_agg(jsonb_build_object(
                'span_id', substr(md5((s.g * 104729 + p)::text), 1, 16),
                'name', (ARRAY['http.server','db.query','cache.get','rpc.call','queue.publish','json.serialize'])[1 + (p % 6)],
                'start_ms', round((p * s.duration / GREATEST(s.spans, 1))::numeric, 3),
                'duration_ms', round((s.duration / GREATEST(s.spans, 1))::numeric, 3),
                'status', CASE WHEN (p % 23) = 0 THEN 'error' ELSE 'ok' END,
                'attributes', jsonb_build_object(
                    'db.statement', 'SELECT * FROM orders WHERE tenant_id = $1 AND created_at > $2 -- ' || substr(md5((p * s.g)::text), 1, 12),
                    'db.rows', (p * 13 + s.g) % 4000,
                    'peer.service', (ARRAY['postgres','redis','pricing','inventory','tax'])[1 + (p % 5)])))
     FROM generate_series(1, s.spans) p),
    jsonb_build_object(
        'deployment', jsonb_build_object('commit', substr(md5('sha' || s.g), 1, 40), 'built_at', to_char(now() - ((s.g % 60) || ' days')::interval, 'YYYY-MM-DD')),
        'client', jsonb_build_object('platform', (ARRAY['web','ios','android','pos'])[1 + (s.g % 4)], 'app_version', '5.' || (s.g % 12)),
        'experiment_buckets', (SELECT jsonb_object_agg('exp_' || e, (s.g + e) % 4) FROM generate_series(1, 18) e)),
    jsonb_build_object(
        'new_checkout', (s.g % 2) = 0,
        'async_tax', (s.g % 3) = 0,
        'split_shipments', (s.g % 5) = 0),
    (ARRAY['http','trace','billing','beta','slow','retried'])[1:1 + (s.g % 4)]
FROM (
    SELECT
        g,
        now() - ((random() * 45) || ' days')::interval               AS received_at,
        gen_random_uuid()                                            AS trace_id,
        gen_random_uuid()                                            AS span_id,
        gen_random_uuid()                                            AS parent_span_id,
        gen_random_uuid()                                            AS user_id,
        gen_random_uuid()                                            AS session_id,
        1 + floor(random() * 6)::int                                 AS svci,
        1 + floor(random() * 3)::int                                 AS envi,
        1 + floor(random() * 3)::int                                 AS regi,
        1 + floor(random() * 3)::int                                 AS azi,
        1 + floor(random() * 5)::int                                 AS methi,
        1 + floor(random() * 6)::int                                 AS routei,
        1 + floor(random() * 4)::int                                 AS uai,
        1 + floor(random() * 4)::int                                 AS cachei,
        1 + floor(random() * 5)::int                                 AS erri,
        (ARRAY[200,200,200,201,204,400,404,409,429,500,502,504])[1 + floor(random() * 12)::int] AS status_code,
        random() * 2400 + 3                                          AS duration,
        -- Every 25th row is a monster (a few hundred KB across the two big
        -- columns), so the worst case is always somewhere on screen.
        CASE WHEN g % 25 = 0 THEN 700 + (g % 200) ELSE 25 + floor(random() * 110)::int END AS body_items,
        CASE WHEN g % 25 = 0 THEN 500 + (g % 150) ELSE 20 + floor(random() * 90)::int  END AS result_rows,
        CASE WHEN g % 25 = 0 THEN 220 + (g % 60)  ELSE 12 + floor(random() * 40)::int  END AS spans
    FROM generate_series(1, 600) g
) s;

-- Realistic for a log table, and it gives the schema tree a GIN index to show.
CREATE INDEX api_events_received_at_idx ON telemetry.api_events (received_at DESC);
CREATE INDEX api_events_trace_idx       ON telemetry.api_events (trace_id);
CREATE INDEX api_events_context_gin     ON telemetry.api_events USING gin (context jsonb_path_ops);

ANALYZE telemetry.api_events;

COMMIT;
