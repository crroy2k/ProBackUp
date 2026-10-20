# ProBackUp · Servidor para IIS

Aplicacion del servidor central y archivos de despliegue nativo en IIS. El backend y su configurador son necesarios para preparar LDAP y datos locales. No incluye el cliente de respaldos.

## Compilar

Windows x64 y SDK .NET 10. Ejecuta en PowerShell:

```powershell
.\build-iis.ps1
```

Los ejecutables y paquetes se generan en `dist/` y no estan versionados.

Las ramas `cliente`, `server` e `iis` contienen unicamente su componente y sus dependencias. `main` conserva el proyecto integrado. No se incluyen certificados, credenciales ni datos locales.
