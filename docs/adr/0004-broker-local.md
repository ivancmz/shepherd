# ADR-0004: Broker local: ciclo de vida, almacenamiento y conexiones

- Estado: **Aceptada**
- Fecha: 2026-11-13
- Relacionadas: [ADR-0001](0001-wsl-como-nodo-tcp.md), [ADR-0002](0002-entrega-y-despertar.md), [ADR-0003](0003-identidad-y-nombres.md)

## Contexto

El broker guarda buzones y nombres, enruta mensajes y despierta a los agentes (ADR-0002). Para despertarlos ejecuta `codex queue` y `wsl.exe`, así que tiene que correr **como el usuario y en su sesión interactiva de Windows**. También tiene que estar disponible sin intervención manual cuando un agente lo necesite.

## Decisión

**Proceso de fondo por usuario que arranca solo, en un único ejecutable.**

1. **Ejecutable único** `shepherd` (win-x64 `shepherd.exe` y linux-x64), con subcomandos:
   - `broker`: el proceso de fondo.
   - `mcp`: el shim MCP por stdio que lanzan Claude y Codex.
   - `launch`: el lanzador de agentes (ADR propio).
   - `send`, `inbox`, `status`, `agents`: CLI para el usuario y sus scripts.
   - `install` / `uninstall`: autoarranque opcional.
2. **Arranque:**
   - Bajo demanda: un shim o un comando de la CLI que no encuentra el broker lo lanza, desacoplado del proceso que lo lanzó, y espera a que esté listo (tiempo límite de unos 3 s).
   - **Solo puede haber un broker a la vez** por usuario. Lo garantiza la propia named pipe (si ya está creada, se usa la existente) junto con un mutex con nombre. Si dos procesos lo lanzan a la vez, gana uno y el otro se conecta a él.
   - Opcional: `shepherd install` crea una **tarea programada del usuario** ("al iniciar sesión"), no un servicio de Windows. Así el broker escucha a WSL desde el principio.
   - En WSL no hay broker: el shim de Linux se conecta al de Windows (ADR-0001). Si no puede conectarse, informa del error con herramientas que fallan de forma clara, sin arrancar ningún broker en Linux.
3. **Almacenamiento:**
   - SQLite en `%LOCALAPPDATA%\shepherd\shepherd.db`: nombres, sesiones, buzones, hilos y acuses. Modo WAL y escrituras en transacción, para que un corte no lo corrompa.
   - Log de auditoría `%LOCALAPPDATA%\shepherd\audit-YYYY-MM.jsonl` (quién, a quién, cuándo, tamaño, resultado de la entrega). Guarda el contenido completo, porque son tus propios agentes; si hace falta se podrá cambiar a solo metadatos.
   - La carpeta `%LOCALAPPDATA%\shepherd\` tiene una ACL solo para el usuario. Ahí vive también el secreto del nodo de ADR-0001.
4. **Conexiones:**
   - Agentes de Windows: named pipe por usuario con un descriptor de seguridad que solo permite el SID del usuario.
   - WSL: TCP en la interfaz de WSL con token (ADR-0001).
   - El mismo protocolo de mensajes en los dos transportes (ADR del protocolo de nodo).
5. **Apagado:** el broker **no se cierra solo** cuando no quedan agentes, porque los mensajes pendientes deben poder llegar a sesiones que se abran después. `shepherd broker stop` lo para limpiamente. Al reiniciarse, el estado se recupera de SQLite.

## Alternativas descartadas

- **Servicio de Windows:** corre fuera de la sesión del usuario (complica `codex queue` y `wsl.exe` como el usuario) y necesita permisos de administrador.
- **Broker dentro del shim del primer agente:** si ese agente se cierra, se cae el bus de todos.
- **Plugin de herdr:** deja fuera WT y WSL cuando herdr no está abierto. Sí se puede añadir más adelante un plugin que solo llame a `shepherd` para mostrar el estado.

## Consecuencias

- Dependencia de SQLite compatible con NativeAOT (`Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3`). Se verifica al empezar la implementación.
- El broker es un proceso más en segundo plano. Hace falta `shepherd status` para ver si corre, con qué agentes y con qué mensajes pendientes.
- Las actualizaciones del ejecutable exigen parar el broker antes de reemplazarlo, porque Windows bloquea el `.exe` en uso.
