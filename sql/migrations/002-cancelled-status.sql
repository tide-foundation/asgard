-- Adds the cancelled status, which an operator can put a run into.
--
-- Guarded on the current definition rather than dropped and recreated blindly,
-- so this is a no-op on a database that already has it. Every migration is
-- written to be safe to apply twice, because a crash between applying one and
-- recording it must not leave the schema unrepeatable.

do $$
begin
    if exists (
        select 1 from pg_constraint
        where conname = 'asgard_job_runs_status_check'
          and pg_get_constraintdef(oid) not like '%cancelled%'
    ) then
        alter table asgard_job_runs drop constraint asgard_job_runs_status_check;
        alter table asgard_job_runs add constraint asgard_job_runs_status_check
            check (status in ('pending', 'leased', 'succeeded', 'dead', 'cancelled'));
    end if;
end $$;
