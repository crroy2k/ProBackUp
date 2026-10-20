# Revisión del archivo de referencia

Archivo: `referencias/fbsetup-full.exe`, 106.654.304 bytes.

SHA-256: `13E93A4CDEA89FDEFEF9FA2EF4D04F294690A4FA0F3DF026DAFEE72B2821BBF2`.

Los recursos PE identifican **FBackup 9.9.1044.0**, compañía **Softland**, nombre original `backupBootstrapper.exe`. PowerShell `Get-AuthenticodeSignature` reportó firma válida en este equipo. 7-Zip identifica un ejecutable PE x86 con contenedor CAB y contenido adicional: es un instalador bootstrapper, no el código fuente del producto.

La [descripción oficial de FBackup](https://www.fbackup.com/fbackup.html) describe tareas con orígenes, destinos, respaldo completo ZIP o espejo y programación. La [página oficial de descarga](https://www.fbackup.com/download.html) identifica la misma versión 9.9.1044. No se ejecutó el instalador ni se modificó el archivo original. No se afirma haber reproducido internamente su motor ni su interfaz instalada.

REA 6.5.0 ejecutó `inspect-pe-resources` sobre el archivo original con límite explícito de 128 MiB. La evidencia está guardada en `fbackup-pe-resources.json`: PE32, máquina 332 (x86), 28 entradas examinadas y 11 recursos inventariados. Se trata de inspección estática de recursos; no de descompilación del motor de respaldo ni de observación de su ejecución.

ProBackUp implementa desde cero los flujos solicitados y confirmados: respaldo local/de red, ZIP/espejo versionado, programación, historial, restauración y una pestaña de configuración SMTP con puertos, TLS, credenciales y destinatarios. No es una modificación binaria de FBackup ni sustituye su licencia o marca.

Pruebas de aceptación: `test-results.txt` e `installer-test-results.txt`. Capturas renderizadas de la interfaz en `previews/`. El correo se valida contra un servidor SMTP en loopback, sin entrega a terceros. No se ha validado con credenciales del proveedor de correo del usuario ni con un NAS externo.

