# Repositorio de ProBackUp

Ramas: `main` contiene la base comun; `cliente`, `server` e `iis` parten de la misma version inicial y permiten desarrollar cada componente. Se conserva el arbol completo en todas las ramas porque comparten contratos, iconos, apariencia y scripts de construccion.

- Cliente: `ProBackUp.App`, `ProBackUp.Setup`, `ProBackUp.Shared`, `assets`, `build.ps1`.
- Servidor: `ProBackUp.Server`, `ProBackUp.Server.Config`, recursos compartidos, `build-server.ps1`.
- IIS: servidor y scripts fuente de `IISProBackUpServer`, `build-iis.ps1`.

Requisitos de compilacion: Windows x64 y SDK .NET 10. Ejecutar `build.ps1`, `build-server.ps1` o `build-iis.ps1` segun la distribucion. Los instaladores y ZIP se generan en `dist` y no forman parte del repositorio de fuentes. `build.ps1` genera el payload del instalador antes de compilarlo.

No se versionan certificados, claves privadas, configuraciones de maquinas, bases de datos, operaciones administrativas locales, dependencias descargadas ni resultados de compilacion. Configurar LDAP, certificados y permisos en cada instalacion.
