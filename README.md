# ProBackUp 3.3

Respaldos para Windows 10/11 x64 con modo oscuro/claro, nuevo logo de datos, servicio automático, destinos de red mediante la sesión del usuario, programación por días y horas, respaldos completos e incrementales y versiones de archivos por día.

Icono en la bandeja al abrir la interfaz: cerrar/minimizar oculta la ventana; doble clic o el acceso directo la restauran. Menú con estado del servicio, historial, respaldos manuales y salida de la interfaz sin detener el servicio. Avisos de copias completadas/errores mediante notificaciones de Windows. La vista previa no crea iconos residuales.

La selección de versiones respeta la ejecución elegida, mantiene el archivo al cambiar de hora y bloquea su restauración si no existe en esa versión. Creación, historial y restauración comparten los nombres de los tres formatos. Un asistente de restauración común permite leer el formato real, elegir acceso de red y recuperar todo o un archivo. El catálogo limita los metadatos, sin rechazar ZIP completos de más de 64 MB. Las pruebas de regresión están en `docs/restore-test-results.txt`.

Versiones de archivos incluye búsqueda por nombre/ruta, filtro combinado de extensiones (`.pdf, .docx`, `*.xlsx`), contador de coincidencias y botón Limpiar. Mantiene filtros al cambiar de día/hora y conserva la identidad del archivo elegido aunque quede oculto por un filtro.

[Instalador](dist/ProBackUp-Setup.exe) · [Manual](LEEME.txt) · [Validación y límites](docs/validacion.md) · [Logo](assets/probackup-logo.png)

El instalador requiere administrador, instala en Program Files y registra/inicia `ProBackUpService` como LocalSystem. La administración también solicita elevación. El servicio espera a que el usuario de cada tarea tenga sesión iniciada. Configuración y claves se guardan en ProgramData con ACL para administradores y SYSTEM; las claves usan DPAPI LocalMachine.

Las tareas utilizan la sesión de Windows de su dueño mediante WTS e identidad acotada al acceso a datos; no guardan su contraseña. Si el usuario no tiene sesión activa, esperan hasta su próximo inicio. Se conservan los permisos originales de cada carpeta. El botón de prueba verifica escritura/lectura/eliminación.

Los incrementales usan objetos identificados por SHA-256 y manifiestos completos por ejecución, agrupados por día. Reutilizan contenidos intactos; las eliminaciones se reflejan en el nuevo estado conservando versiones anteriores según la política configurada. Cada versión puede restaurarse sin aplicar una cadena de deltas. Hay restauración de un archivo por fecha/hora desde el historial. Conserva el repositorio entero, no solamente los `.pbi`.

## Compilación

En Windows con SDK .NET 10, ejecuta `./build.ps1` en PowerShell. Publica app e instalador autónomos x64, ejecuta pruebas, empaqueta payload y genera hashes SHA-256. El host de compilación no necesita elevarse: la lógica del instalador se prueba mediante su ensamblado y carpeta temporal, sin registrar servicios ni cambiar Program Files. La instalación real solicita UAC.

Código: `ProBackUp.App/`, `ProBackUp.Setup/`. Identidad visual e instrucciones de generación: [assets/README.md](assets/README.md). Inspección del instalador de referencia: [docs/revision-fbackup.md](docs/revision-fbackup.md).

No incluye VSS, cifrado de los respaldos, OAuth SMTP. El equipo debe estar encendido. Los ejecutables no tienen firma Authenticode. Las pruebas SMB contra un NAS y el ciclo real del servicio bajo SCM se indican como pendientes, no como comprobaciones superadas.


ALMACENAMIENTO INCREMENTAL (2.4)
Se consulta el espacio disponible con las credenciales de la tarea, tanto en ejecución
manual como desde el servicio. El servidor decide si informa la cuota individual o
el espacio del volumen. Probar conexión muestra los GB disponibles informados.
Por defecto: sin límite manual, sin vencimiento por días y limpieza por falta de
espacio activada. Puedes desactivar la limpieza para detener y avisar sin borrar.
Conservar días: 0 no vence versiones; por ejemplo 20 conserva los últimos 20 días,
pero siempre mantiene al menos una versión. El vencimiento se procesa después de
publicar una copia nueva. No se limpian carpetas de respaldos completos ZIP/carpeta.
El límite manual opcional mide el repositorio de esta tarea, no toda la cuenta.
Antes de copiar contenido nuevo se comprueba el espacio y un margen de 64 MB.
Si falta espacio, se eliminan versiones de más antigua a reciente y solo objetos
que ninguna versión conservada o copia en curso necesita. Antes se verifican SHA-256
y tamaño de los objetos conservados; la corrupción impide la limpieza.
No se elimina la última versión para hacer sitio a la nueva: hace falta espacio
para conservar la anterior mientras se guarda el contenido modificado. Puede fallar
aunque la nueva versión por sí sola cupiera en la cuota. El fallo queda en historial
y genera correo si está configurado. Las versiones depuradas se marcan en historial.
Si la cuota cambia o otro usuario consume espacio durante la copia, el servidor puede
rechazar la escritura; se registra el error y no se publica una versión incompleta.
La política afecta solo al repositorio incremental propio; no borra archivos externos.

NOTIFICACIONES DE RESULTADO (2.5)
Al terminar un respaldo, la interfaz muestra «Ejecutado con éxito» con un check
verde. El aviso de éxito se cierra en 10 segundos. Si falla o se cancela, muestra
una X roja y «Llame a T.I. Ext. 233»; permanece hasta que se cierre con Entendido
o se abra Ver historial. Los avisos pendientes se muestran en orden, sin omitir
fallos cuando termina más de una tarea. No se alerta por versiones depuradas.
También se emite el aviso nativo de Windows; su visibilidad depende de No molestar.
La alerta propia requiere que ProBackUp esté abierto, aunque esté en la bandeja.
El servicio sin interfaz no puede mostrar ventanas en la sesión del usuario.
Las pruebas crean avisos reales, verifican colores, textos, cola, ausencia de
duplicados y cierre al salir; capturas en docs/previews/notificacion-*.png.

SERVIDOR CENTRAL (3.0)
Nuevo servidor HTTPS para Windows con panel de reportes, LDAP de Active Directory
por LDAPS, usuarios UPN específicos y roles Admin/Reader. Configuración LDAP,
registro/revocación de equipos y auditoría desde el panel. El cliente incorpora
Configuración → Servidor central, claves DPAPI, cola persistente y reintentos.
Los respaldos siguen operando si el servidor falla. Compilar: ./build-server.ps1.
Despliegue y límites de seguridad: docs/servidor-central.md. Validación real AD/CA,
SCM con cuenta virtual y carga prolongada pendiente; no se desplegó en esta sesión.


EJECUCIÓN DESDE EL SERVIDOR (3.2)
En el panel central: Administración > cliente > Respaldar > seleccionar tarea > Ejecutar respaldo ahora. Requiere cliente 3.3, conexión al servidor activada y servicio ProBackUpService en ejecución. Recoge órdenes cada 15 segundos, espera si hay una operación local y caduca solicitudes aún no recogidas a las 24 horas; las que esperan sesión se conservan hasta que el usuario vuelva. Los reintentos no duplican copias. Solo un administrador del panel puede solicitar respaldos. Las actualizaciones del programa siguen siendo manuales.


EJECUCIÓN CON SESIÓN DE WINDOWS (3.3)
Las tareas existentes se migran a la sesión del usuario dueño de cada tarea y se eliminan las contraseñas de red de la configuración activa y su copia inmediata. SMTP conserva su configuración independiente. Revisa el usuario de la tarea en Destino y acceso. No se usa la sesión de otro usuario ni LocalSystem para leer o escribir los respaldos.
El servicio no necesita la ventana de ProBackUp abierta. Al iniciar sesión recupera el último horario vencido, sin acumular copias por todos los horarios perdidos. Un fallo no se repite cada 20 segundos; puede reintentarse en un nuevo inicio de sesión. Una copia local o remota correcta posterior al horario satisface ese vencimiento.
Las órdenes remotas recogidas sin sesión disponible quedan en Esperando sesión y no caducan mientras esperan. Una orden de una tarea cuyo usuario esté ausente no bloquea a los demás usuarios. Las órdenes aún no recogidas por un equipo desconectado conservan el límite de 24 horas.


CORRECCIÓN UAC (3.3.1)
Corrige el error Windows 1346 al ejecutar la interfaz como administrador. El acceso a datos usa el token del escritorio normal del mismo usuario y sesión, sin depender de un token vinculado de UAC que solo permita identificación. No usa el token elevado como alternativa ni cambia permisos. El servicio mantiene su acceso mediante WTS a la sesión del dueño.


Mapa de carpetas: [Organizacion del proyecto](docs/organizacion.md).

