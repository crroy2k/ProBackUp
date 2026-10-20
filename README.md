# ProBackUp · Cliente Windows

Aplicacion de respaldo para Windows y su instalador. No incluye el servidor ni el despliegue IIS.

## Compilar

Windows x64 y SDK .NET 10. Ejecuta en PowerShell:

```powershell
.\build.ps1
```

Los ejecutables y paquetes se generan en `dist/` y no estan versionados.

Las ramas `cliente`, `server` e `iis` contienen unicamente su componente y sus dependencias. `main` conserva el proyecto integrado. No se incluyen certificados, credenciales ni datos locales.
