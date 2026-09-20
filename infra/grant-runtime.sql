-- Run as the owning migration role with -v runtime_role=<role> AFTER migrations.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"runtime_role";
REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM :"runtime_role";
GRANT SELECT ON TABLE "__EFMigrationsHistory" TO :"runtime_role";
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE "AuditEvents" FROM :"runtime_role";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"runtime_role";
