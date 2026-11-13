# ADR-0001: WSL se conecta al broker de Windows por TCP, como un nodo más

- Estado: **Aceptada**
- Fecha: 2026-11-13
- Contexto previo: resultados del spike (`spikes/channel-spike/`)

## Contexto

Los agentes de WSL (Claude 2.1.323, Codex 0.169.0 de Linux) forman un mundo aparte de los de Windows: tienen su propio registro de sesiones, su propio `~/.codex` y su propio daemon de Codex. La mensajería nativa de Claude no cruza la frontera Windows/WSL.

Lo que ha comprobado el spike:

- **Windows → WSL** funciona sin interop:
  - `wsl -e bash -ic 'codex queue --thread <id> --message ...'` despierta al Codex de WSL. Respondió en ~2 s.
  - Un servidor MCP de Linux, hecho con el mismo código C#, hace de canal del Claude de WSL. Un mensaje enviado desde Windows lo despertó y Claude respondió.
- **WSL → Windows** no puede usar binarios de Windows: con `systemd=true`, el registro binfmt `WSLInterop` desaparece (`Exec format error`, incluso con `cmd.exe`).
- El servidor MCP de Linux recibe `CLAUDE_CODE_SESSION_ID` y `WSL_DISTRO_NAME`, pero no las variables de Windows (`HERDR_PANE_ID`).

## Decisión

WSL es **un nodo del bus como cualquier otra máquina**. El shim MCP de Linux (binario linux-x64 del mismo proyecto C#) mantiene una **conexión TCP persistente y autenticada** con el broker de Windows:

- Por esa conexión el shim envía (registro, `send`, `read_inbox`...) y **recibe los avisos que empuja el broker**, que reenvía al agente por el canal de Claude. No se revisa ninguna carpeta en bucle.
- Es el mismo transporte que usarán después los otros PC de la LAN. Se diseña e implementa una sola vez.
- El broker de Windows escucha **solo en la interfaz de WSL** (vEthernet de WSL con `networkingMode=nat`), nunca en todas las interfaces. Cuando haya nodos de la LAN se decidirá en su propio ADR.
- **Autenticación por token**: el broker genera un secreto en una ruta del perfil de Windows con ACL solo para el usuario. El shim de WSL lo lee por `/mnt/c/...`, que es la misma frontera de confianza (tu usuario). Cada conexión lo presenta al abrirse.
- Hace falta una **regla del Firewall de Windows** limitada a ese puerto y a la interfaz de WSL. La crea un comando de instalación explícito, que muestra la regla antes de aplicarla.
- Para despertar al **Codex de WSL**: `codex queue` dentro de WSL, sea desde el broker por `wsl.exe` o desde el propio shim local. Se decide en el ADR de entrega. Hay que invocarlo con `bash -ic` o con la ruta absoluta, porque con `bash -lc` el `codex` del PATH resuelve al de Windows.

## Alternativas descartadas

- **A. Carpeta compartida en `/mnt/c`:** probada en el spike y funciona. Pero obliga a revisar la carpeta en bucle, el disco de Windows es lento visto desde WSL y no sirve para otros PC. Puede quedar como modo de emergencia si el TCP falla, sin ser el camino principal.
- **C. Arreglar el interop y usar el `.exe` de Windows desde WSL:** exige tocar la configuración de WSL (servicio systemd que vuelve a registrar `WSLInterop`) y no ayuda con otras máquinas.

## Consecuencias

- Hay que diseñar ya el protocolo de nodo (TCP + autenticación + reconexión), que antes estaba previsto para una fase posterior. Se adelanta el ADR de federación en su parte de transporte. El descubrimiento entre PC sigue para más adelante.
- Si se cambia WSL a `networkingMode=mirrored`, el shim podría usar `localhost`. El diseño debe aceptar la dirección del broker como configuración y no suponer NAT.
- La IP de Windows vista desde WSL (puerta de enlace NAT) puede cambiar tras reiniciar. El shim la descubre al conectar (ruta por defecto o variable configurada).
- La identidad de los agentes de WSL lleva el nombre de la distro (`WSL_DISTRO_NAME`) como "máquina": p. ej. `claude@wsl-ubuntu`.
