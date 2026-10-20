# ProBackUp Server para IIS · 3.4.0

Este paquete aloja la aplicación **directamente dentro de IIS** mediante ASP.NET Core Module V2, en proceso (`w3wp.exe`). IIS termina HTTPS; no se necesita ARR ni un puerto Kestrel detrás. El servicio independiente queda detenido y deshabilitado para impedir dos instancias. La configuración, usuarios AD, claves de clientes, historial y base SQLite siguen en `C:\ProgramData\ProBackUpServer`, fuera del sitio web.

La creación de esta carpeta no instala ni modifica el servidor actual. Este paquete debe copiarse y ejecutarse en el equipo Windows Server donde se alojará IIS.

## Contenido

- `sitio/`: publicación Windows x64 autocontenida, DLL, ejecutable, dependencias, logo, favicon y `web.config` listo para IIS.
- `Backend/`: instalador y configurador gráfico para preparar o actualizar la configuración local, LDAPS y permisos antes de migrar.
- `Instalar-IIS.ps1`: valida prerrequisitos, certificado y pertenencia del sitio, crea un pool exclusivo, publica una versión, concede permisos al pool y comprueba HTTPS.
- `Verificar-IIS.ps1`: comprueba sitio, pool, certificado y API, sin depender de que DNS ya apunte a este equipo.
- `Restaurar-Servicio.ps1`: detiene el sitio/pool propio y restaura el modo de inicio original del servicio, conservando datos y publicaciones.
- `Actualizar-URL-Cliente.ps1`: cambia al puerto nuevo en cada cliente del mismo DNS, conservando la clave cifrada; no necesita recuperar ni registrar una clave nueva.
- `SHA256SUMS.txt`: integridad de todos los archivos. No contiene certificados privados, contraseñas ni reportes de tu instalación.

## Requisitos en el servidor IIS

1. Windows Server x64 con IIS 10, consola de administración y módulo Windows PowerShell `WebAdministration`. Usa **Windows PowerShell 5.1 de 64 bits como administrador**, no PowerShell 7.
2. **Hosting Bundle de .NET 10 actualizado**, instalado después de IIS. Aunque el paquete incluye su runtime, IIS necesita registrar `AspNetCoreModuleV2`. Descarga oficial: https://dotnet.microsoft.com/en-us/download/dotnet/10.0 (ASP.NET Core Runtime → Hosting Bundle). Si lo instalaste antes de IIS, repáralo. No es necesario instalar el SDK.
3. Certificado corporativo vigente para `probackup.coopeagri.local`, con SAN exacto y clave privada, instalado en **Equipo local → Personal** (`Cert:\LocalMachine\My`). La CA y su revocación deben ser accesibles/confiables. No se omiten validaciones TLS.
4. Conectividad del servidor hacia Active Directory por LDAPS y cuenta de consulta configurada. `curl.exe` de Windows para la comprobación HTTPS local. Application Initialization es recomendable para precarga.
5. Mantén en IIS/Schannel la política corporativa de TLS 1.2/1.3. El TLS externo depende de IIS y Windows; el script no modifica protocolos globales de otros sitios.
6. El DNS público interno apuntará a la IP del servidor IIS. No es necesario cambiar DNS antes de instalar: la prueba usa `curl --resolve` conservando la validación real del certificado.

El certificado y su contraseña **no vienen en el paquete**. Para importar tu PFX, desde consola administrativa local:

```powershell
$clave = Read-Host 'Contraseña del PFX' -AsSecureString
Import-PfxCertificate -FilePath 'D:\Certificados\probackup.coopeagri.local.pfx' -CertStoreLocation Cert:\LocalMachine\My -Password $clave
Remove-Variable clave
```

## Instalación

Copia la carpeta completa, por ejemplo a `D:\Descargas\IISProBackUpServer`. No ejecutes desde una unidad mapeada que desaparece al elevar permisos.

### 1. Preparar la aplicación y Active Directory

```powershell
& 'D:\Descargas\IISProBackUpServer\Backend\Install-ProBackUpServer.ps1'
```

Si este equipo ya tiene ProBackUp, actualiza sus binarios conservando la configuración existente. En un equipo nuevo se abrirá el configurador gráfico: introduce DNS, un puerto de servicio libre (por ejemplo 8445), PFX, LDAPS, cuenta de consulta y usuarios UPN autorizados. El certificado PFX de este paso permite también volver al servicio independiente; IIS usa su binding del almacén Personal.

**No copies `server.json` ni claves DPAPI de otra máquina**: están cifrados para el equipo original. Configura LDAP de nuevo en el equipo nuevo. Para conservar los equipos, sus claves y los reportes al cambiar de máquina, debes trasladar una copia consistente de `reports.db` mediante `--backup-db` y restáurala con servicio y pool detenidos; no mezcles WAL/SHM. Sin esa base, las claves antiguas no serán reconocidas y habrá que registrar los clientes de nuevo. No copies las claves de Data Protection: los usuarios del panel volverán a iniciar sesión. Conserva copias y prueba antes de mover producción. Los respaldos de archivos permanecen en los destinos de los clientes.

### 2. Publicar en IIS

```powershell
& 'D:\Descargas\IISProBackUpServer\Instalar-IIS.ps1' -HostName 'probackup.coopeagri.local'
```

El script mostrará los certificados disponibles y pedirá su huella. Para especificarla:

```powershell
& 'D:\Descargas\IISProBackUpServer\Instalar-IIS.ps1' -HostName 'probackup.coopeagri.local' -CertificateThumbprint 'HUELLA_DE_40_CARACTERES'
```

Resultado predeterminado: **https://probackup.coopeagri.local/** en puerto 443. Crea sitio y pool `ProBackUpServer` y publicaciones versionadas en `C:\Program Files\ProBackUpIIS\releases`. Solo el pool recibe acceso a los datos de ProBackUp. La autenticación anónima de IIS está activada porque la aplicación mantiene su propio login AD y autenticación de agentes; no habilites autenticación Windows sobre todo el sitio porque impediría el envío de los clientes.

La regla propia de firewall permite el puerto HTTPS en Domain/Private para LocalSubnet. Puedes indicar subredes en la primera instalación con `-ClientSubnets '172.28.0.0/16'`. No cambia reglas de otros sitios. Si una política corporativa controla el firewall, aplícala en esa política. El script no usa `iisreset`, no detiene sitios ajenos y no instala componentes de Windows automáticamente.

Para conservar la URL de los clientes con el puerto actual, puedes publicar IIS en **8445**:

```powershell
& 'D:\Descargas\IISProBackUpServer\Instalar-IIS.ps1' -HostName 'probackup.coopeagri.local' -HttpsPort 8445
```

El servicio se detiene antes de que IIS utilice ese puerto. Decide el puerto antes de la primera publicación; una actualización conserva DNS y puerto existentes.

### 3. Verificar y cambiar DNS/clientes

```powershell
& 'D:\Descargas\IISProBackUpServer\Verificar-IIS.ps1'
```

Apunta DNS al IIS y abre el panel por HTTPS. Entra con un usuario AD permitido, revisa equipos/reportes y prueba un envío de un cliente. Si cambiaste de 8445 a 443, en cada cliente sal de ProBackUp desde la bandeja y ejecuta como administrador:

```powershell
& 'D:\Descargas\IISProBackUpServer\Actualizar-URL-Cliente.ps1' -Url 'https://probackup.coopeagri.local/'
```

El ayudante valida HTTPS sin enviar la clave, mantiene el token cifrado y no altera tareas ni contraseñas. Solo permite cambiar el puerto del mismo DNS. Conserva una copia protegida de settings.json. El servicio del cliente puede seguir activo si no está ejecutando un respaldo/restauración; si está ocupado, repite al finalizar. Conservar el mismo nombre DNS y la misma base mantiene las claves de los agentes.

## Actualización y reversión

Para actualizar IIS, copia un paquete IIS nuevo y ejecuta `Instalar-IIS.ps1` con el mismo sitio, pool, DNS y puerto. Conserva publicaciones anteriores. No ejecutes el instalador Backend sobre una instalación que ya está en IIS: el marcador `iis-hosting.json` lo impide. Para volver al servicio:

```powershell
& 'D:\Descargas\IISProBackUpServer\Restaurar-Servicio.ps1'
```

El sitio queda detenido y se retira su binding propio para liberar el puerto, especialmente si conservaste 8445. Una publicación posterior puede recrearlo. No se borran bases, reportes ni publicaciones. La reversión requiere que el PFX original del servicio siga vigente; si renovaste solo el binding de IIS, actualiza también ese PFX antes de reactivar el servicio. Vuelve a la URL HTTPS/puerto de servicio en los clientes. Después podrás actualizar Backend o usar su configurador. Mientras está alojado en IIS, administra LDAP/usuarios desde el panel y certificados/bindings desde IIS.

La instalación guarda un respaldo de configuración de IIS con nombre `ProBackUp-before-...`; no lo restaura globalmente de forma automática para no sobrescribir cambios de otros sitios. Ante un fallo intenta restaurar solo su sitio/pool y servicio. Las versiones conservadas permiten una revisión manual. Comprueba el resultado antes de dar por completada una migración.

## Diagnóstico y validación

- **500.19 / módulo desconocido:** instala o repara Hosting Bundle después de IIS.
- **500.30 / 500.31:** consulta Visor de eventos → Aplicación → ASP.NET Core Module V2 y ProBackUp.Server. Verifica arquitectura x64, archivos publicados y permisos del pool en ProgramData.
- **401 al cliente antes de llegar a la API:** no fuerces autenticación Windows/Basic en el sitio; la aplicación usa sus propias claves.
- **Error de certificado:** comprueba SAN, cadena corporativa, fecha, binding y revocación; no uses `--insecure`.
- **AD no permite login:** revisa LDAPS y usuarios autorizados; no existe acceso web local que omita AD.

La publicación se construye y las pruebas del backend se ejecutan con directorio simulado y HTTPS local. Los scripts y XML se validan sin modificar IIS. **La instalación nativa, el binding y el login AD deben verificarse en tu IIS**; este paquete no se ha desplegado automáticamente en el servidor actual ni supone una prueba de carga o alta disponibilidad.

Fuentes: [IIS con ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/), [alojamiento en proceso](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/in-process-hosting), [Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle).
