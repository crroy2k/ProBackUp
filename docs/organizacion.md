# Organizacion de ProBackUp

- ProBackUp.App, ProBackUp.Setup, ProBackUp.Server, ProBackUp.Server.Config y ProBackUp.Shared: codigo fuente.
- assets: logo e identidad visual.
- docs: manuales, validaciones y resultados de pruebas.
- dist: ejecutables, instaladores y ZIP de distribucion.
- IISProBackUpServer: paquete desplegable en IIS; publicar solamente su subcarpeta sitio.
- referencias: instalador original de FBackup utilizado como referencia.
- rea: herramienta instalada; conservar esta ruta porque el comando global apunta a ella.
- _archivo/compilacion-20261019: archivos generados bin, obj y artifacts archivados de forma reversible. No son necesarios para ejecutar los instaladores; los scripts build los regeneran.

Se conservaron payload.zip y payload.sha256 porque el proyecto del instalador los incluye como recursos. No se modificaron servicios, datos instalados, respaldos ni certificados.

La revision automatica de seguridad bloqueo el borrado recursivo. Los generados se archivaron en lugar de eliminarlos; no se libero espacio en disco.
