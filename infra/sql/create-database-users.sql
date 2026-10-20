/*
  Database users for the Microsoft Entra identities — run ONCE per environment, after the first
  infrastructure deployment (and again only if the Web App or the deployment app registration is
  recreated, since their identity changes).

  Connect to the APPLICATION database (not master) as a member of the SQL administrators group
  ("CarRental SQL Admins"), with Microsoft Entra authentication:

    sqlcmd (go-sqlcmd, https://aka.ms/go-sqlcmd), after `az login`:
      sqlcmd -S <server>.database.windows.net -d carrental --authentication-method ActiveDirectoryAzCli \
             -i infra/sql/create-database-users.sql \
             -v WebAppName="<webAppName output>" -v DeployPrincipalName="<app registration display name>"

    Azure portal > SQL database > Query editor (Entra authentication): replace $(WebAppName) and
    $(DeployPrincipalName) by hand, then run the script (it is idempotent).

  CREATE USER ... FROM EXTERNAL PROVIDER resolves the names in Microsoft Entra ID with the token of the
  connected administrator; the name of a system-assigned managed identity is the Web App name.
  If a display name is ambiguous in the tenant, use instead:
      CREATE USER [<name>] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '<object id of the service principal>';
*/

-- 1. Web App system-assigned managed identity: the application itself.
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(WebAppName)')
    CREATE USER [$(WebAppName)] FROM EXTERNAL PROVIDER;
GO
ALTER ROLE db_datareader ADD MEMBER [$(WebAppName)];
ALTER ROLE db_datawriter ADD MEMBER [$(WebAppName)];
-- Hangfire creates and upgrades its own "HangFire" schema at startup (PrepareSchemaIfNecessary),
-- which needs DDL rights. Remove this line once that schema is installed by the migrations instead.
ALTER ROLE db_ddladmin ADD MEMBER [$(WebAppName)];
-- Stored procedures, if any (sp_getapplock itself only needs the public role).
GRANT EXECUTE TO [$(WebAppName)];
GO

-- 2. GitHub Actions deployment identity (app registration with OIDC federated credentials):
--    runs the EF Core migration bundle (schemas, tables, temporal tables, sequences).
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(DeployPrincipalName)')
    CREATE USER [$(DeployPrincipalName)] FROM EXTERNAL PROVIDER;
GO
ALTER ROLE db_owner ADD MEMBER [$(DeployPrincipalName)];
GO

-- 3. Check.
SELECT p.name AS principal_name, p.type_desc, r.name AS role_name
FROM sys.database_principals AS p
LEFT JOIN sys.database_role_members AS m ON m.member_principal_id = p.principal_id
LEFT JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
WHERE p.name IN (N'$(WebAppName)', N'$(DeployPrincipalName)')
ORDER BY p.name, r.name;
GO
