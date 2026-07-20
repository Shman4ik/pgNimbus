-- pgNimbus demo data — 03: the `iot` schema (partitioning + more types).
--
-- Devices exercise macaddr, inet/cidr, bit/varbit, geometric point/polygon and
-- interval. `readings` is the demo's largest relation: a RANGE-partitioned-by-
-- month time-series table (~200k rows) with a DEFAULT partition catching any
-- out-of-range timestamps. Safe to re-run (schema is dropped and recreated).

BEGIN;

DROP SCHEMA IF EXISTS iot CASCADE;
CREATE SCHEMA iot;

CREATE TYPE iot.device_status AS ENUM ('online','offline','maintenance','error','provisioning');

-- --- Devices ---------------------------------------------------------------
CREATE TABLE iot.devices (
    id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    serial       uuid NOT NULL DEFAULT gen_random_uuid() UNIQUE,
    name         text NOT NULL,
    mac          macaddr UNIQUE NOT NULL,
    ip           inet,
    subnet       cidr,
    firmware     bit(16),
    flags        bit(8),
    location     point,
    coverage     polygon,
    config       jsonb NOT NULL DEFAULT '{}'::jsonb,
    status       iot.device_status NOT NULL DEFAULT 'provisioning',
    battery_pct  numeric(5,2) CHECK (battery_pct BETWEEN 0 AND 100),
    uptime       interval,
    installed_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE iot.devices IS 'IoT fleet: macaddr, inet/cidr, bit/varbit firmware+flags, geometric point/polygon, interval uptime.';

INSERT INTO iot.devices (name, mac, ip, subnet, firmware, flags, location, coverage, config, status, battery_pct, uptime, installed_at)
SELECT
    (ARRAY['Sensor','Gateway','Camera','Thermostat','Meter','Valve','Beacon','Hub'])[1+floor(random()*8)::int] || '-' || lpad(g::text,4,'0'),
    ('08:00:2b:'
        || lpad(to_hex(floor(random()*256)::int),2,'0') || ':'
        || lpad(to_hex(floor(random()*256)::int),2,'0') || ':'
        || lpad(to_hex(floor(random()*256)::int),2,'0'))::macaddr,
    ('172.16.' || floor(random()*255)::int || '.' || floor(random()*254+1)::int)::inet,
    ('172.16.' || floor(random()*255)::int || '.0/24')::cidr,
    (floor(random()*65536)::int)::bit(16),
    (floor(random()*256)::int)::bit(8),
    point(round((random()*360-180)::numeric,4), round((random()*180-90)::numeric,4)),
    polygon(box(point(round((random()*10)::numeric,2), round((random()*10)::numeric,2)),
                point(round((random()*10+10)::numeric,2), round((random()*10+10)::numeric,2)))),
    jsonb_build_object(
        'model', (ARRAY['ESP32','RPi4','nRF52','STM32'])[1+floor(random()*4)::int],
        'sample_rate_hz', (ARRAY[1,5,10,60])[1+floor(random()*4)::int],
        'thresholds', jsonb_build_object('high', floor(random()*100), 'low', floor(random()*10))
    ),
    (enum_range(NULL::iot.device_status))[1+floor(random()*5)::int],
    round((random()*100)::numeric, 2),
    (floor(random()*4000) || ' hours')::interval,
    now() - (random()*500 || ' days')::interval
FROM generate_series(1, 120) g;

-- --- Readings: RANGE-partitioned by month ----------------------------------
CREATE TABLE iot.readings (
    id          bigint GENERATED ALWAYS AS IDENTITY,
    device_id   integer NOT NULL REFERENCES iot.devices(id) ON DELETE CASCADE,
    recorded_at timestamptz NOT NULL,
    metric      text NOT NULL,
    value       double precision NOT NULL,
    unit        text,
    quality     smallint CHECK (quality BETWEEN 0 AND 100),
    flags       bit(8),
    payload     jsonb,
    PRIMARY KEY (id, recorded_at)
) PARTITION BY RANGE (recorded_at);
COMMENT ON TABLE iot.readings IS 'Time-series sensor readings, RANGE-partitioned by month (see child partitions in the tree). The largest relation in the demo.';

CREATE TABLE iot.readings_2026_01 PARTITION OF iot.readings FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');
CREATE TABLE iot.readings_2026_02 PARTITION OF iot.readings FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');
CREATE TABLE iot.readings_2026_03 PARTITION OF iot.readings FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');
CREATE TABLE iot.readings_2026_04 PARTITION OF iot.readings FOR VALUES FROM ('2026-04-01') TO ('2026-05-01');
CREATE TABLE iot.readings_2026_05 PARTITION OF iot.readings FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');
CREATE TABLE iot.readings_2026_06 PARTITION OF iot.readings FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');
CREATE TABLE iot.readings_2026_07 PARTITION OF iot.readings FOR VALUES FROM ('2026-07-01') TO ('2026-08-01');
CREATE TABLE iot.readings_default PARTITION OF iot.readings DEFAULT;

INSERT INTO iot.readings (device_id, recorded_at, metric, value, unit, quality, flags, payload)
SELECT
    s.device_id,
    s.recorded_at,
    (ARRAY['temperature','humidity','pressure','co2','lux','voltage'])[s.mi],
    round((random() * (ARRAY[50.0,100.0,1100.0,2000.0,10000.0,24.0])[s.mi])::numeric, 3)::double precision,
    (ARRAY['°C','%','hPa','ppm','lx','V'])[s.mi],
    s.quality,
    s.flags,
    CASE WHEN s.has_payload THEN jsonb_build_object('raw', floor(random()*1024), 'calibrated', random()>0.5) END
FROM (
    SELECT
        1 + floor(random()*120)::int AS device_id,
        timestamptz '2026-01-01' + (random() * (now() - timestamptz '2026-01-01')) AS recorded_at,
        1 + floor(random()*6)::int   AS mi,
        floor(random()*101)::int     AS quality,
        (floor(random()*256)::int)::bit(8) AS flags,
        random() < 0.15              AS has_payload
    FROM generate_series(1, 200000) g
) s;

CREATE INDEX ix_readings_device_time ON iot.readings (device_id, recorded_at DESC);
CREATE INDEX ix_readings_metric      ON iot.readings (metric);
-- Deliberately narrow partial index that typical queries won't hit, so it
-- surfaces in the "unused indexes" panel of the Database Overview.
CREATE INDEX ix_readings_quality_unused ON iot.readings (quality) WHERE quality < 10;

COMMIT;
