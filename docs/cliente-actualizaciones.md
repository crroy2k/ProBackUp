ProBackUp Cliente 3.4.1

Nuevo: limitador de velocidad por tarea en Destino y acceso. Desactivado por defecto para tareas nuevas y existentes. Activa Limitar velocidad de subida e indica el maximo en Mbps (megabits por segundo; 1 Mbps = 125000 bytes/s). Comparte el presupuesto entre todos los archivos escritos al destino, los metadatos y la salida ZIP comprimida. Funciona en completos ZIP/espejo e incrementales y en ejecuciones manuales, programadas o remotas. No limita las restauraciones ni las lecturas de verificacion; el protocolo SMB y otro trafico de red tienen sobrecarga adicional. En enlaces compartidos, reparte el ancho de banda entre maquinas.

La interfaz abre con el usuario conectado, sin pedir administrador. La instalación y el botón Reparar servicio sí requieren UAC. Incluso si se introducen credenciales de otra cuenta administradora durante la instalación, el acceso a los datos se concede al usuario del escritorio y la aplicación se abre con ese usuario.

Instala o actualiza desde la sesión del usuario que realizará los respaldos. Cada instalación conserva un propietario de tareas. La configuración y el historial permiten acceso a ese usuario, administradores y SYSTEM; no se conceden permisos generales a todos los usuarios de Windows. El servicio verifica el SID autorizado por el instalador y usa la sesión de ese usuario también para sus archivos de configuración e historial. Nunca ejecuta tareas de respaldo con SYSTEM como alternativa. Las tareas antiguas sin SID se vinculan cuando su nombre coincide exactamente con el usuario conectado; revisa y guarda las tareas que antes se crearon con una cuenta administradora diferente.

Con la pantalla bloqueada, el respaldo continúa. Si el usuario cerró sesión, se espera a su próximo inicio; entonces se recupera la última programación vencida.

Configuración → Buscar actualizaciones consulta las publicaciones estables de cliente en crroy2k/ProBackUp. Omite versiones de servidor y versiones preliminares. Descarga el instalador por HTTPS a LocalAppData\ProBackUp\updates, verifica su tamaño y SHA-256 y solicita confirmación antes de iniciar el instalador con UAC. Conserva las tareas, configuración e historial. No se actualiza automáticamente ni interrumpe una operación de la interfaz.

Publicación: el workflow release-client.yml en main compila el cliente y publica ProBackUp-Setup.exe y SHA256SUMS.txt como client-v<VERSION>. No sobrescribe versiones existentes. Las pruebas que requieren escritorio interactivo se ejecutan localmente antes de publicar; la compilación en Actions no dispone de esa sesión.
