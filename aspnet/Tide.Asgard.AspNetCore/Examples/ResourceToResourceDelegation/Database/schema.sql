-- The web application's database. Postgres runs this the first time it starts with an empty volume.

-- Patient records. No field is stored in plain text: each column is a cipher locked by the Tide network, under the
-- tags noted beside it.
CREATE TABLE patients (
    id              uuid  PRIMARY KEY,
    personal_info   bytea NOT NULL, -- name, age and address, locked together ("name", "age", "address")
    age             bytea NOT NULL, -- the age on its own, for anonymous statistics ("age")
    medical_details bytea NOT NULL  -- ("medical")
);

-- The policy patient data is locked under
CREATE TABLE policies (
    id                text  PRIMARY KEY,
    data              bytea NOT NULL,
    change_request_id text            -- set while TideCloak waits for admins to approve the policy
);

-- The analytics service's login: it reads patient ciphers, and nothing else
CREATE ROLE analytics LOGIN PASSWORD 'analytics';
GRANT SELECT ON patients TO analytics;
