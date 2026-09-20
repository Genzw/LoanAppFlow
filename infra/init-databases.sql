\getenv api_migrator_password API_MIGRATOR_PASSWORD
\getenv api_runtime_password API_RUNTIME_PASSWORD
\getenv mock_migrator_password MOCK_MIGRATOR_PASSWORD
\getenv mock_runtime_password MOCK_RUNTIME_PASSWORD
CREATE ROLE loanapp_migrator LOGIN PASSWORD :'api_migrator_password';
CREATE ROLE loanapp_runtime LOGIN PASSWORD :'api_runtime_password';
CREATE ROLE loanapp_mock_migrator LOGIN PASSWORD :'mock_migrator_password';
CREATE ROLE loanapp_mock_runtime LOGIN PASSWORD :'mock_runtime_password';
CREATE DATABASE loanapp OWNER loanapp_migrator;
CREATE DATABASE loanapp_mock OWNER loanapp_mock_migrator;
REVOKE ALL ON DATABASE loanapp FROM PUBLIC;
REVOKE ALL ON DATABASE loanapp_mock FROM PUBLIC;
GRANT CONNECT ON DATABASE loanapp TO loanapp_runtime;
GRANT CONNECT ON DATABASE loanapp_mock TO loanapp_mock_runtime;
\connect loanapp
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO loanapp_runtime;
\connect loanapp_mock
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO loanapp_mock_runtime;
