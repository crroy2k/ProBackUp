# Repositorio de ProBackUp

`main` conserva el proyecto integrado. Las ramas especializadas contienen solamente su componente y las dependencias necesarias:

- `cliente`: `ProBackUp.App`, `ProBackUp.Setup`, contratos `ProBackUp.Shared`, recursos visuales y `build.ps1`.
- `server`: servidor central, configurador grafico, contratos, recursos visuales y `build-server.ps1`. No incluye el cliente ni scripts de IIS.
- `iis`: servidor central y configurador necesarios para preparar LDAP/datos, contratos, recursos visuales, scripts de despliegue IIS y scripts de construccion del paquete. No incluye el cliente.

Cada rama tiene su propio README con instrucciones. Los recursos de apariencia usados por el servidor estan en `assets`, sin dependencia del proyecto cliente.

Compilacion: Windows x64 y SDK .NET 10. Los instaladores y ZIP se generan en `dist` y no se versionan. No se incluyen certificados, credenciales ni datos de instalaciones.
