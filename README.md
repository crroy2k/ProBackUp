# ProBackUp · Servidor central

Servidor central HTTPS y configurador para Windows. Incluye solamente los contratos y recursos visuales que necesita el servidor. No incluye el cliente de respaldos ni el paquete IIS.

## Compilar

Windows x64 y SDK .NET 10. Ejecuta en PowerShell:

```powershell
.\build-server.ps1
```

Los ejecutables y paquetes se generan en `dist/` y no estan versionados.

Las ramas `cliente`, `server` e `iis` contienen unicamente su componente y sus dependencias. `main` conserva el proyecto integrado. No se incluyen certificados, credenciales ni datos locales.
