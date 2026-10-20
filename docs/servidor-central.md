# ProBackUp Server 3.3 — despliegue interno

## Componentes

- `ProBackUp.Server.exe`: servidor web HTTPS autónomo x64, como servicio Windows.
- `Install-ProBackUpServer.ps1`: instalación administrativa, identidad virtual `NT SERVICE\ProBackUpServer`, recuperación automática y firewall limitado a redes internas.
- `ProBackUp-Setup.exe`: cliente actualizado; Configuración → Servidor central.

El servidor recibe **metadatos**, no archivos ni credenciales de las carpetas compartidas. El cliente sigue respaldando cuando el servidor no está disponible y conserva una cola en ProgramData. El servidor almacena SQLite local en `%PROGRAMDATA%\ProBackUpServer`, con WAL, transacciones, reintentos idempotentes y timeout de bloqueo. No coloque esta base en SMB. Es una instalación de un solo servidor; no es un clúster de alta disponibilidad.

## Requisitos de producción

Windows x64 con las actualizaciones de seguridad al día, un nombre DNS interno y un certificado PFX de su CA para ese nombre (SAN DNS y uso de servidor TLS). El PFX necesita clave privada y contraseña de al menos 12 caracteres. La CA y sus intermediarias deben ser confiables para el servidor y **todos los clientes**, en el almacén del equipo. La comprobación de revocación debe poder alcanzar los puntos de distribución de la CA. No se instala ni se confía automáticamente en certificados autofirmados.

Esta versión integra **Active Directory** por LDAPS: host DNS de controlador, puerto 636 habitual, Base DN, cuenta dedicada de consulta de solo lectura y UPN de administrador inicial. Se consulta `userPrincipalName`, `objectGUID` y `userAccountControl`; no es un adaptador genérico probado con OpenLDAP. No use una cuenta administradora de dominio para las consultas. La cuenta de consulta y la contraseña del usuario solo se transmiten dentro de LDAPS, con comprobación explícita de SAN DNS, cadena confiable, uso de servidor y revocación del certificado y sin seguir referencias a otros servidores. Deshabilite TLS 1.0/1.1 en las políticas Schannel del servidor y controladores: la versión TLS de LDAPS depende de Windows; la API LDAP no configura el mínimo por conexión. El endpoint HTTPS sí restringe TLS 1.2/1.3. No hay opción LDAP sin TLS ni opción para ignorar errores de certificado.

## Instalación

1. Extraiga `ProBackUp-Server.zip` en una carpeta temporal del servidor.
2. Abra PowerShell como administrador y ejecute `./Install-ProBackUpServer.ps1`. El asistente de consola solicita los datos anteriores y verifica LDAPS y el administrador antes de guardar. No pase contraseñas como argumentos. Para limitar subredes concretas use `-ClientSubnets '10.20.0.0/16','10.30.5.0/24'`. Por defecto se permite `LocalSubnet`, perfiles Domain/Private; nunca Public.
3. Entre a `https://nombre-dns:8443/` (o el puerto elegido) con el UPN autorizado y su contraseña del directorio.
4. En Administración configure LDAP y los usuarios específicos. Cada línea: `usuario@empresa.local Reader` o `admin@empresa.local Admin`. `Reader` consulta reportes; `Admin` gestiona LDAP, claves y auditoría. No hay comodines, acceso por pertenecer al dominio ni usuarios locales de emergencia por web.
5. Registre cada máquina y copie su clave, mostrada una sola vez. En el cliente instale la nueva versión y abra Configuración → Servidor central; introduzca URL HTTPS y clave. Use «Probar conexión y enviar pendientes» y guarde. El servicio reporta cada minuto; ante fallo reintenta con espera creciente hasta cinco minutos y variación aleatoria.

La instalación no pone datos de directorio ficticios ni arranca en modo abierto si falta configuración. Si la verificación falla queda detenida. Para recuperar configuración con acceso administrativo **local** ejecute `ProBackUp.Server.exe --configure`; reinicie el servicio después. Este procedimiento reemplaza LDAP y certificado pero conserva la base y las claves de equipos. La actualización exige el mismo directorio propio y servicio con ruta coincidente; preserva ProgramData. El script no desinstala ni borra respaldos de clientes.

## Seguridad de acceso

HTTPS únicamente, TLS 1.2/1.3, certificado válido y nombres DNS. No se aceptan redirecciones al enviar claves de agentes. No se utiliza proxy automático en clientes. Sesión de navegador HttpOnly/Secure/SameSite Strict, protección antifalsificación en escrituras y login, CSP sin scripts externos ni HTML dinámico de reportes. Sesión de 15 minutos inactiva y máximo absoluto de 8 horas. Cada petición verifica la lista y revisión de configuración; cada cinco minutos se vuelve a consultar la identidad del directorio y que no esté deshabilitada. Si falla esta consulta, la sesión se rechaza. Cambiar LDAP o permisos invalida todas las sesiones existentes. No se cachean contraseñas de usuarios.

Límite por IP y por cuenta en login; máximo cuatro consultas LDAP simultáneas, timeouts de cinco segundos, búsquedas escapadas y resultados limitados. La limitación complementa la política de bloqueo de AD; reiniciar el proceso reinicia los contadores en memoria. La auditoría registra acceso correcto/rechazado y cambios administrativos, sin contraseñas ni claves. Las cuentas bloqueadas, expiradas o con contraseña incorrecta fallan al autenticar. Una deshabilitación puede tardar hasta cinco minutos en invalidar una sesión existente; revocar de la lista del panel es inmediato en la siguiente petición.

Cada agente tiene una clave aleatoria de 256 bits almacenada solo como SHA-256 en el servidor. La revocación impide reportes nuevos y nuevas entregas de órdenes; esa clave solo consulta y confirma las órdenes de su propio equipo, sin autorizar lectura del panel ni administración. La clave identifica la instalación, pero no demuestra criptográficamente el nombre Windows que reporta. No clone una configuración con la misma clave a varias máquinas. La autenticación mutua por certificado de cliente y MFA de usuarios no están incluidas. Para entornos que las exijan debe integrarse un proveedor de identidad con MFA antes de exponer el panel fuera de la red interna. No publique el puerto en Internet.

Los secretos locales usan DPAPI del equipo y ACL limitadas a Administradores, SYSTEM y la identidad del servicio. La base de reportes no está cifrada por SQLite; use BitLocker en el volumen y proteja las copias. El servicio tiene permiso de modificación sobre sus propios datos, no privilegios de administrador del sistema. Los ejecutables no tienen firma Authenticode comercial.

## Datos y filtros

Filtre por equipo, tipo, resultado, fechas, destino y usuario. Horas del panel en Costa Rica (UTC−06:00); almacenamiento de fechas UTC. Paginación de 100 ejecuciones; se actualiza al pulsar Filtrar / actualizar. Estado «Sin reporte reciente» a los diez minutos sin contacto, distinto de respaldo fallido.

«Versión completa» es el tamaño lógico de todos los archivos recuperables. «Datos nuevos escritos» incluye los objetos nuevos y metadatos en incremental, el ZIP final comprimido o archivos + manifiesto en completo en carpeta. No mide paquetes SMB, escrituras temporales ni espacio total del destino. Los respaldos fallidos antes de publicar pueden dejar objetos incrementales sin referencia; esa escritura parcial no se contabiliza en la métrica del resultado. Las versiones depuradas actualizan su estado en el servidor. El usuario es el propietario de la tarea guardada; para tareas antiguas sin propietario se usa la cuenta de red explícita o la cuenta de ejecución, que puede ser SYSTEM. Edite/guarde la tarea para fijar su propietario.

Solo se encolan ejecuciones posteriores a activar la conexión; no se importa el historial anterior automáticamente. Los reportes pendientes quedan en `%PROGRAMDATA%\ProBackUp\central-outbox`; no se descartan por caídas del servidor. Vigile su crecimiento y `central-status.json`. Desactivar reporting detiene los envíos y conserva la cola. El historial local sigue disponible si no se puede crear un reporte. El control remoto permite solicitar respaldos de tareas ya configuradas en el cliente. No admite comandos de sistema ni exploración de archivos desde el panel.

## Operación y respaldo del servidor

Cree una copia consistente con `ProBackUp.Server.exe --backup-db D:\CopiaSegura\reports.db` desde consola administrativa local. No copie solo el archivo principal SQLite mientras esté activo: puede tener datos en WAL. Proteja también configuración, certificado y claves de Data Protection; los secretos DPAPI están vinculados al equipo y al migrar deberá reintroducir las contraseñas con `--configure`. Al restaurar la base detenga el servicio y no mezcle WAL/SHM antiguos con una copia distinta. Prepare un procedimiento de recuperación probado antes de producción.

Configure alertas externas de salud del servicio, vencimiento del certificado y espacio libre del servidor. Recovery de Windows reinicia procesos que fallan; no sustituye esas alertas ni una arquitectura redundante. Si AD cae no hay acceso local alternativo por web; los agentes siguen enviando reportes porque su autenticación es independiente.

## Validación

`docs/server-test-results.txt`: HTTPS real en loopback con certificado temporal confiado **solo por el cliente de pruebas**; directorio simulado para probar permisos, sesiones, CSRF, claves y revocación, filtros, métricas, reintentos, auditoría y copia SQLite. El directorio simulado se instancia solo dentro de `--self-test`; no hay un interruptor de configuración para habilitarlo en producción. Pruebas del cliente: cola persistente, fallo/reintento, secretos cifrados y métricas reales de ZIP e incremental.

Pendiente: integración con AD/LDAPS y CA corporativa reales, despliegue elevado SCM bajo la cuenta virtual, firewall y pruebas de carga prolongadas en su red. No se afirma que estas integraciones estén ya verificadas. Realice un piloto con máquinas de prueba y una revisión de seguridad antes de producción. El escaneo NuGet del proyecto Server no detectó vulnerabilidades conocidas al construir esta versión; no es una auditoría de seguridad completa.

Referencias: [servicios Windows ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service), [antifalsificación ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery), [LDAPS](https://learn.microsoft.com/en-us/dotnet/api/system.directoryservices.protocols.ldapsessionoptions.securesocketlayer).

## Configurador gráfico local (3.1)

Abra `ProBackUp.Server.Config.exe` junto a los archivos del servidor. Solicita elevación de administrador y usa los mismos colores oscuro/claro, logo y navegación lateral del cliente. Las secciones son Resumen, HTTPS y certificado, Active Directory y Usuarios y acceso. El instalador también crea un acceso en Inicio → ProBackUp Server → Configurar ProBackUp Server.

Desde HTTPS seleccione el PFX y escriba su contraseña. «Validar certificado y puerto» comprueba clave privada, SAN DNS, vigencia, cadena de CA/revocación y puerto disponible. Desde Active Directory pulse «Detectar dominio» para completar controlador y Base DN; introduzca una cuenta dedicada de lectura y su contraseña. «Probar LDAPS y administrador» valida la conexión y que exista el primer administrador de la lista de usuarios.

Las contraseñas vacías conservan las existentes solo si no cambia el certificado almacenado o el host/cuenta de consulta. No se muestra ni se exporta su texto. Puede gestionar usuarios Admin/Reader desde la tabla; sin un administrador no se guarda. Los errores permanecen en la ventana para corregirlos.

«Guardar configuración» vuelve a validar HTTPS y LDAP antes de detener el servicio. Prepara la instalación si falta, guarda PFX y JSON de forma controlada, ajusta la regla HTTPS del firewall para Domain/Private y conserva las subredes previamente configuradas (LocalSubnet si es nueva). Solo inicia/reinicia si está marcada la casilla correspondiente. Mantiene copias `.previous` protegidas e intenta restaurar archivos y regla si falla la operación. No modifica respaldos ni la base de reportes. El servicio queda detenido durante la aplicación de los cambios. Abra una sola herramienta de configuración: cierre el asistente de consola anterior para que no pueda sobrescribir la configuración posteriormente.

Desde Resumen puede instalar, iniciar, detener, actualizar estado y abrir el panel en su navegador. Iniciar/abrir requiere guardar previamente los cambios. Las validaciones y el guardado se ejecutan sin bloquear la ventana; durante ellos se impide editar/cerrar para no interrumpir una aplicación parcial.

Pruebas: `docs/server-config-test-results.txt` valida rollback de archivos, confirmación, bloqueo de escrituras, rechazo de PFX inválido, administrador inexistente, secretos DPAPI y renderizado real de las cuatro secciones en ambos temas. Capturas en `docs/previews/server-config`. Las operaciones SCM/firewall y el arranque final se verifican en el equipo de destino con administrador; las pruebas automáticas no alteran servicios reales ni el firewall.

Las tarjetas de equipos muestran verde si el último respaldo exitoso tiene menos de 24 horas, amarillo desde las 24 horas y rojo desde las 72 horas. Los equipos sin respaldos exitosos registrados o con clave revocada aparecen en rojo. Los latidos y los intentos fallidos no reinician este contador. Se muestran por separado el último respaldo y el último contacto, con actualización de las tarjetas cada minuto mientras el panel está abierto.

Administración presenta los clientes en una tabla. Editar cambia el nombre visible conservando identidad, clave e historial. Eliminar requiere confirmación y borra transaccionalmente el cliente y sus reportes centrales, invalidando su clave; no borra archivos en los destinos. Ambas acciones requieren rol Admin y protección CSRF, y quedan registradas en auditoría.


## Ejecución remota de respaldos (3.2)

Actualice servidor y clientes a 3.3. En el cliente mantenga activada la conexión al servidor central y el servicio Windows ProBackUpService en ejecución. El servicio anuncia hasta 200 tareas (ID, nombre, tipo y destino) y consulta órdenes por HTTPS cada 15 segundos. No transmite las contraseñas de red o SMTP, ni requiere puertos entrantes en el cliente. La interfaz o la bandeja no necesitan estar abiertas.

En Administración → Registro de equipos, pulse **Respaldar**, seleccione la tarea y pulse **Ejecutar respaldo ahora**. Solo Admin puede hacerlo, con CSRF y auditoría. La ventana muestra las últimas 50 solicitudes y actualiza sus estados cada cinco segundos: Pendiente, En curso, Correcto, Error, Cancelado, Rechazado o Caducado. Las solicitudes repetidas de la misma tarea se agrupan mientras están pendientes o en curso. El historial central conserva los resultados y cantidades una vez enviados por el cliente.

La orden caduca a las 24 horas si el cliente no la recoge. Si la recoge sin sesión del dueño, queda en Esperando sesión sin caducar y se ejecuta al iniciar sesión. Un equipo desconectado la recoge al reconectarse antes del vencimiento. Si hay una operación local activa, el bloqueo existente mantiene la orden pendiente. Se ejecuta con la tarea local y la sesión de Windows de su dueño, sin cambiar la programación habitual ni permitir al servidor introducir rutas o programas arbitrarios. Cada orden tiene una identidad persistente en el historial local para impedir copias duplicadas por reintentos. Si el servicio se interrumpe, comunica Error al recuperar la conexión y no repite automáticamente esa orden. Si se pierde la red durante la copia, el respaldo continúa y su confirmación queda en la cola local `remote-results`.

Revocar una clave cancela solicitudes pendientes o esperando sesión, pero no detiene una copia que ya arrancó en el cliente. Eliminar un cliente elimina también su catálogo y sus órdenes centrales, conservando los archivos de destino. No se implementan actualizaciones remotas del programa.

Esta versión agrega tablas y eleva el esquema SQLite a 2. Antes de actualizar, detenga el servidor y conserve una copia protegida de la base y sus archivos WAL/SHM si existen; no reutilice una base de esquema 2 con el servidor 3.0. Para revertir, restaure juntos ejecutable y copia anterior de la base con el servicio detenido.

Pruebas adicionales: permisos Admin/Reader, CSRF, aislamiento entre claves, caducidad, deduplicación, estados terminales, ejecución real sobre archivos de prueba, bloqueo por operación en curso, recuperación tras interrupción y reintentos de confirmación. La ejecución remota contra los equipos de producción requiere instalar el nuevo cliente.


## Sesión del dueño y recuperación al iniciar sesión (3.3)

El servicio sigue instalado como LocalSystem para consultar sesiones mediante WTS, pero las operaciones de archivos usan exclusivamente el token del usuario de la tarea. La identidad se compara por SID cuando está guardado; al migrar una tarea antigua sin SID se exige coincidencia exacta de DOMINIO\usuario. No se usa la sesión de otro usuario. En la interfaz elevada se usa el token limitado de UAC para acceso a datos; la configuración e historial protegidos permanecen bajo la identidad del proceso. El bloqueo de operación coordina ejecución local, programada y remota.

No hace falta guardar ni renovar contraseñas de Windows. Al instalar/abrir 3.3, se migran las tareas existentes y se limpian los secretos de red de settings.json y su copia .bak inmediata. No se altera SMTP ni la autenticación LDAPS del servidor. El usuario se revisa en Destino y acceso. Una sesión bloqueada cuenta como iniciada; una cerrada o RDP desconectada queda esperando. Si una copia ya empezó, puede terminar usando el token adquirido antes del cierre.

El servicio recibe avisos de cambios de sesión y además comprueba cada 20 segundos. Al entrar el usuario recupera solo el último horario vencido desde el alta/configuración de la tarea, aunque haya pasado mucho tiempo sin sesión. Una copia correcta local o remota posterior al horario evita duplicarla. Una ejecución fallida de un horario se intenta una vez por inicio de sesión, con identidad de inicio basada en el LUID de autenticación, para evitar bucles cada pocos segundos.

Las órdenes recogidas sin sesión se muestran como Esperando sesión y se conservan hasta que el usuario vuelva o TI revoque/elimine el cliente. Los equipos que no recogen la orden siguen sujetos a caducidad de 24 horas. Se entregan hasta 200 órdenes por consulta para que una sesión ausente no bloquee a otra tarea del mismo equipo.

Referencias de implementación: [WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken) y [WindowsIdentity.RunImpersonated](https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsidentity.runimpersonated).

Inicio de sesion (3.4.2): permite escribir solo el usuario, por ejemplo rourena. El servidor lo resuelve a un unico UPN de la lista autorizada; si el nombre es ambiguo entre dominios, debe escribirse el UPN completo. Conserva LDAPS, permisos y limites de intentos. El pie de pagina queda centrado.
