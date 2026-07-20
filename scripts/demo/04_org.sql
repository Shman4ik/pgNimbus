-- pgNimbus demo data — 04: the `org` schema (ltree hierarchy).
--
-- An org chart stored as an ltree materialized path (GiST-indexed), plus
-- employees with int4range salary bands, text[] skills, jsonb profiles and a
-- self-referencing manager_id. Safe to re-run (schema is dropped/recreated).

BEGIN;

DROP SCHEMA IF EXISTS org CASCADE;
CREATE SCHEMA org;

-- --- Units: hierarchy via ltree --------------------------------------------
CREATE TABLE org.units (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    path        ltree UNIQUE NOT NULL,
    name        text NOT NULL,
    headcount   integer NOT NULL DEFAULT 0,
    budget      money,
    cost_center text
);
COMMENT ON TABLE org.units IS 'Org hierarchy stored as an ltree materialized path (GiST-indexable tree).';

INSERT INTO org.units (path, name, headcount, budget, cost_center) VALUES
    ('company',                      'Acme Corp',         0, 25000000::money, 'CC-000'),
    ('company.engineering',          'Engineering',       0,  9000000::money, 'CC-100'),
    ('company.engineering.platform', 'Platform',         18,  3200000::money, 'CC-110'),
    ('company.engineering.apps',     'Applications',     24,  3800000::money, 'CC-120'),
    ('company.engineering.data',     'Data & ML',        12,  2000000::money, 'CC-130'),
    ('company.sales',                'Sales',             0,  6000000::money, 'CC-200'),
    ('company.sales.emea',           'Sales EMEA',       16,  2500000::money, 'CC-210'),
    ('company.sales.amer',           'Sales Americas',   20,  2800000::money, 'CC-220'),
    ('company.sales.apac',           'Sales APAC',       10,  1700000::money, 'CC-230'),
    ('company.marketing',            'Marketing',        14,  3000000::money, 'CC-300'),
    ('company.operations',           'Operations',        0,  4000000::money, 'CC-400'),
    ('company.operations.support',   'Customer Support', 22,  1500000::money, 'CC-410'),
    ('company.operations.finance',   'Finance',           9,  1200000::money, 'CC-420'),
    ('company.operations.people',    'People & Culture',  7,   900000::money, 'CC-430');

CREATE INDEX ix_units_path_gist ON org.units USING gist (path);

-- --- Employees: int4range salary band, self-referencing manager -------------
CREATE TABLE org.employees (
    id          integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    unit_id     integer NOT NULL REFERENCES org.units(id),
    manager_id  integer REFERENCES org.employees(id) ON DELETE SET NULL,
    name        text NOT NULL,
    title       text NOT NULL,
    email       citext UNIQUE NOT NULL,
    salary_band int4range NOT NULL,
    salary      money NOT NULL,
    skills      text[] NOT NULL DEFAULT '{}',
    hired_on    date NOT NULL,
    profile     jsonb NOT NULL DEFAULT '{}'::jsonb,
    is_manager  boolean NOT NULL DEFAULT false
);
COMMENT ON TABLE org.employees IS 'Employees with int4range salary bands, text[] skills, jsonb profile and a self-referencing manager_id.';

-- One manager per leaf unit (nlevel >= 3) first...
INSERT INTO org.employees (unit_id, name, title, email, salary_band, salary, skills, hired_on, profile, is_manager)
SELECT
    u.id,
    (ARRAY['Alex','Sam','Jordan','Taylor','Morgan','Casey','Riley','Jamie'])[1+floor(random()*8)::int] || ' ' ||
    (ARRAY['Lee','Park','Cohen','Diaz','Fischer','Rossi','Novak','Bauer'])[1+floor(random()*8)::int],
    u.name || ' Lead',
    'lead.' || u.id || '@acme.example',
    int4range(120000, 180000),
    (round((random()*60000 + 120000)::numeric, 0))::money,
    ARRAY['leadership','strategy','postgres'],
    (date '2018-01-01' + (random()*2000)::int),
    jsonb_build_object('level','M', 'remote', random()>0.5),
    true
FROM org.units u
WHERE nlevel(u.path) >= 3;

-- ...then rank-and-file employees, each reporting to a random manager.
INSERT INTO org.employees (unit_id, manager_id, name, title, email, salary_band, salary, skills, hired_on, profile, is_manager)
SELECT
    mgr.units[s.mi],
    mgr.ids[s.mi],
    (ARRAY['Emma','Liam','Olivia','Noah','Ava','Ethan','Sophia','Mason','Isabella','Lucas','Mia','Leo'])[s.fni] || ' ' ||
    (ARRAY['Smith','Nguyen','Garcia','Muller','Rossi','Andersson','Okafor','Silva','Petrov','Khan'])[s.lni],
    (ARRAY['Engineer','Senior Engineer','Analyst','Specialist','Coordinator','Associate','Manager','Architect'])[s.ti],
    'emp' || s.g || '.' || s.rnd || '@acme.example',
    (ARRAY[int4range(60000,90000), int4range(90000,130000), int4range(130000,200000)])[s.bi],
    (round((random()*140000 + 60000)::numeric, 0))::money,
    (ARRAY['sql','python','react','go','rust','figma','excel','kubernetes','ml','sales'])[1:1+floor(random()*4)::int],
    (date '2019-01-01' + (random()*2500)::int),
    jsonb_build_object(
        'level', (ARRAY['IC1','IC2','IC3','IC4','IC5'])[s.li],
        'remote', random()>0.4,
        'languages', to_jsonb((ARRAY['en','de','fr','es','ja'])[1:1+floor(random()*2)::int])
    ),
    false
FROM (SELECT array_agg(id) ids, array_agg(unit_id) units FROM org.employees WHERE is_manager) mgr
CROSS JOIN (
    SELECT
        g,
        1 + floor(random() * (SELECT count(*) FROM org.employees WHERE is_manager))::int AS mi,
        1 + floor(random()*12)::int AS fni,
        1 + floor(random()*10)::int AS lni,
        1 + floor(random()*8)::int  AS ti,
        1 + floor(random()*3)::int  AS bi,
        1 + floor(random()*5)::int  AS li,
        floor(random()*100000)::int AS rnd
    FROM generate_series(1, 300) g
) s;

COMMIT;
