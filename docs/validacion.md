# Validación de ProBackUp 3.0

Compilación en Windows x64, SDK .NET 10.0.303. Los proyectos compilan sin advertencias. La aplicación se prueba con almacenamiento temporal y correo SMTP local, sin envío a terceros.

Pruebas ejecutadas en `test-results.txt`: completos ZIP y espejo, restauración y corrupción, Unicode/binarios/carpetas vacías, cancelación, bloqueos, rutas de escape, DPAPI, configuración, SMTP de éxito/error; horarios por días y horas, omisión de días desmarcados, no duplicación, recuperación del último horario perdido y rechazo de horas inválidas; incrementales iniciales y sin cambios, modificación/nuevos/eliminados, restauración de estados anteriores y de archivo individual, corrupción de objeto, agrupación por día y prueba de acceso local. El bucle real `BackupService.Work` ejecutó una tarea vencida sin interfaz y se detuvo mediante cancelación.

Pruebas del instalador en `installer-test-results.txt`: extracción del payload SHA-256, actualización de carpeta propia, ejecución de las pruebas desde la aplicación extraída, rechazo de carpeta ajena y creación de acceso directo. La lógica se ejecuta vía el mismo ensamblado .NET para no disparar UAC en el host; no se confunde con una instalación real del servicio.

El icono ICO contiene 7 resoluciones (16 a 256 px) y ambos proyectos lo usan. Se revisaron capturas del encabezado con logo y del nuevo editor de tareas. Capturas de temas en `previews/`.

Las seis pruebas de `tray-test-results.txt` crean una ventana y un NotifyIcon reales, verifican cierre a bandeja, minimizar, restauración por evento de activación, reapertura e icono retirado al salir. La compilación ejecuta estas pruebas contra el ejecutable autónomo copiado a una carpeta temporal local. Las vistas previas no crean iconos. El servicio no se detiene ni se inicia desde el menú de bandeja. La entrega visible de avisos depende de las preferencias de notificación de Windows.

`restore-test-results.txt` valida dos versiones reales distintas del mismo día y el diálogo WinForms: abre la elegida en el historial, conserva la identidad del archivo al cambiar de versión, no sustituye un archivo eliminado por otro, recupera la selección al volver y restaura los bytes exactos. Comprueba ZIP de más de 64 MB, detección por contenido con extensión cambiada, recuperación completa, fecha ausente de respaldos antiguos y el asistente común con selección explícita de archivo presente/ausente. Las capturas `restauracion-unificada.png` y `versiones-corregidas.png` fueron revisadas visualmente.

Las pruebas del diálogo verifican búsqueda por ruta sin distinguir mayúsculas, combinación de extensiones (`*.pdf; DOCX`), extensión exacta (`txt` no incluye `txtx`), ausencia de coincidencias, selección oculta no restaurable, persistencia de filtros al cambiar de hora y recuperación de la selección al limpiar.

## Verificación pendiente en equipo con administrador

Esta sesión es de integridad media. No se elevó ni se creó un servicio real para probar. Se debe validar instalación/UAC, permisos Program Files/ProgramData, registro SCM, inicio automático sin sesión, detención/actualización/desinstalación, recuperación de fallos y acceso SMB usando la cuenta actual/otra cuenta con credenciales reales. Tampoco se probó correo TLS con un proveedor externo. El instalador contiene estas operaciones, pero no se presentan como integración verificada.

La implementación sigue las reglas de [cuentas de servicio de Windows](https://learn.microsoft.com/en-us/windows/win32/ad/about-service-logon-accounts) y [LogonUser](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-logonuserw): LocalSystem usa la identidad del equipo en red; `LOGON32_LOGON_NEW_CREDENTIALS` aporta credenciales remotas nuevas, cuya autenticación se comprueba al acceder al servidor. No se utiliza la sesión interactiva ni se pasan contraseñas a procesos.

Los respaldos no son instantáneas VSS. La retención y limpieza automática son configurables para incrementales. Un intento registrado como en curso tras una caída no se repite automáticamente; hay reintento manual. Un `.pbi` depende de los objetos de su repositorio. Los ejecutables no tienen firma Authenticode.


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

Pruebas de almacenamiento: vencimiento por días, restauración completa tras depurar datos compartidos, objetos sin referencias, historial depurado, espacio insuficiente simulado, última versión protegida, limpieza desactivada, límite manual, repositorio corrupto, objetos de una copia en curso y ejecución real fallida por cuota sin publicar copia parcial. Consulta de espacio local ejecutada; cuota individual SMB pendiente de comprobar con el servidor real.

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


## Cliente 3.2.1: identidad para carpetas privadas

La misma tarea exige credenciales personales persistidas para rutas UNC, tanto en ejecución local como programada o remota. Una tarea antigua con Cuenta del servicio debe editarse una vez para introducir la contraseña de Windows en el cliente. Las tareas locales mantienen la opción de servicio. La comprobación de conexión también usa la cuenta elegida. Las lecturas de metadatos finales de ZIP y carpeta se realizan dentro de la misma identidad que copia los datos; no vuelven a LocalSystem al calcular el tamaño final.

Regresiones verificadas: rechazo previo a acceder a red sin identidad persistida; detección de origen/destino UNC y UNC extendido; recuperación DPAPI; conservación de cuenta y contraseña guardadas al editar desde otra sesión; orden remota no sustituye la cuenta local. Los permisos de carpetas compartidas y NTFS no se modifican. El servidor sigue recibiendo únicamente catálogo, estados y resultados, sin contraseñas ni contenidos.

Las pruebas usan archivos y credenciales ficticias, sin autenticar contra la carpeta corporativa. La validación final del acceso de cada cuenta necesita introducir su contraseña en el cliente y probar una ejecución bajo el servicio. La configuración existente de Mi respaldo no contiene contraseña; no es posible completarla automáticamente a partir de la sesión interactiva.


## Cliente y servidor 3.3: sesión de Windows

Las tareas migran a Mode=Session, conservan propietario/orígenes/destino/tipo y limpian contraseñas de red en la configuración activa y copia inmediata. Se usa WTSQueryUserToken desde LocalSystem, exclusivamente para una sesión WTSActive del dueño; no se adoptan sesiones ajenas. Se compara SID cuando está disponible y se fija en disco al primer acceso, con coincidencia de cuenta DOMINIO\usuario durante la migración. Un token de interfaz elevada usa su token limitado UAC para datos. La configuración y el historial protegidos se leen/escriben temporalmente bajo la identidad del proceso, sin cambiar los permisos del destino. La interfaz de tareas no muestra campo de contraseña de Windows. Al abrir elevado como otro administrador, el usuario sugerido procede de la sesión del escritorio.

Sin sesión no se crea historial de intento ni se consume el horario. El servicio escucha cambios de sesión y comprueba cada 20 segundos; recoge el último horario vencido, no todos los anteriores. Un error se intenta una vez por LUID de inicio de sesión; puede recuperarse al volver a iniciar. Una copia correcta local/remota posterior al horario evita duplicarla. El servidor admite Esperando sesión sin vencimiento después de recoger la orden y entrega todas las tareas pendientes del cliente para no bloquear a otro dueño.

Pruebas: coincidencia de identidades/SID, migración sin secretos de red y con SMTP conservado, token real y await, fijación de SID, espera sin intento, recuperación de horarios tras ausencia prolongada, deduplicación y reintento al cambiar de inicio, estados remotos y permisos. Se conserva `docs/session-integration-results.txt` con una comprobación real bajo LocalSystem: obtuvo la sesión COOPEAGRI\rourena, enumeró el destino \\10.10.0.11\rourena con ese usuario y leyó el historial protegido mediante el proceso. No modificó configuración, permisos ni respaldos; se eliminó la tarea programada temporal de prueba. Falta validar un ciclo real de cierre/inicio de sesión tras instalar el cliente.

## Corrección UAC 3.3.1
Se reprodujo Windows 1346 en 3.3.0 ejecutando --self-test elevado: DuplicateCurrent intentaba duplicar un token vinculado UAC que solo permitía identificación. Se usa ahora el token primario del escritorio, verificando misma sesión y SID y rechazando un escritorio elevado. El servicio continúa usando WTS. La suite completa pasó en proceso normal y en proceso elevado: docs/uac-desktop-test-results.txt y docs/uac-elevated-test-results.txt. Se incluyó una regresión que ejecuta la ruta del escritorio directamente, también desde pruebas no elevadas. No se cambian permisos de archivos ni se solicita contraseña de Windows.
