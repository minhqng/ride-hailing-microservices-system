# Jaeger — local trace UI

Start the optional `obs` profile with `docker compose --profile obs up -d`. UI: http://localhost:16686. Jaeger accepts OTLP on ports 4317 (gRPC) and 4318 (HTTP); application instrumentation is a later increment.
