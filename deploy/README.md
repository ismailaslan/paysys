# Running Paysys somewhere else

Everything the system needs comes from environment variables and a Postgres database. Nothing is written to local disk.

| What | How |
| --- | --- |
| Docker host | `docker compose -f deploy/docker-compose.yml up --build` (API on 8080, gateway on 8081) |
| Kubernetes | `kubectl apply -f deploy/k8s/00-config.yaml -f deploy/k8s/postgres.yaml`, then `migrate-job.yaml`, wait for it to complete, then `api.yaml` and `gateway.yaml` |
| Schema | `deploy/Dockerfile.migrate` applies the EF Core migrations (the bank API does not create its tables at start; the gateway does) |
| Web site | Built with the API address baked in (`ApiBaseUrl`); rebuild it when the API address changes (see `.github/workflows/pages.yml`) |

All keys and passwords in these files are local placeholders. Replace them with your own before a real host. The
`deploy-test` workflow starts both setups from nothing on every change to these files and checks two replicas of the
API and the gateway, and `deploy/tools/two_instance_load.py` checks load and a shared sign-in lockout across two instances.
