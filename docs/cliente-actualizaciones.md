ProBackUp Cliente 3.4.3

Mejoras de seguridad en la actualización, el instalador y la restauración.

La búsqueda de versiones funciona con el usuario del escritorio. Tras confirmar, Windows solicita administrador para que el ayudante instalado en Program Files descargue y verifique la nueva versión en una carpeta protegida. Las versiones anteriores requieren instalar este paquete completo una vez para incorporar el nuevo ayudante.

El instalador usa rutas de sistema absolutas, valida propietarios y enlaces antes de adoptar datos y reconcilia los permisos autorizados y rechaza accesos de otras cuentas. Una carpeta con propietario inesperado requiere revisión de T.I.

Las restauraciones fijan las identidades de los directorios mientras escriben, verifican cada copia durante la transferencia y limitan los bytes al tamaño del manifiesto. Las pruebas de seguridad de Windows se ejecutan también en CI.

Se conservan tareas, configuración e historial en ProgramData. El instalador mantiene la elevación de administrador y la aplicación se abre con el usuario del escritorio.
