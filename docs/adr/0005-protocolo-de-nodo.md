# ADR-0005: Protocolo entre shim/CLI y broker

- Estado: **Aceptada**
- Fecha: 2026-11-13
- Relacionadas: [ADR-0001](0001-wsl-como-nodo-tcp.md), [ADR-0002](0002-entrega-y-despertar.md), [ADR-0003](0003-identidad-y-nombres.md), [ADR-0004](0004-broker-local.md)

## Contexto

El shim MCP (en Windows y en WSL) y la CLI hablan con el broker por dos transportes: la named pipe local y TCP en la interfaz de WSL. Por la misma conexión viajan peticiones del cliente y avisos que el broker empuja (`message.new`), que el shim de Claude convierte en notificaciones de canal.

## Decisión

**JSON-RPC 2.0 delimitado por líneas** (un objeto JSON UTF-8 por línea, `\n`), el mismo encuadre que MCP por stdio. Es idéntico en pipe y en TCP.

1. **Saludo obligatorio**, primera petición de cada conexión:
   ```json
   {"jsonrpc":"2.0","id":1,"method":"hello","params":{"protocol":1,"node":"wsl-ubuntu","client":"mcp|cli","token":"<solo TCP>"}}
   ```
   - Por TCP, sin token válido → error y cierre. Por la pipe no hace falta token: la protege la ACL del usuario (ADR-0004).
   - Si la versión de protocolo es incompatible → error `protocol_mismatch`, con las versiones admitidas.
   - Cualquier otra petición antes de `hello` → error y cierre.
2. **Peticiones del cliente** (lista inicial, se detalla en el modelo de mensaje): `register`, `send`, `reply`, `read_inbox`, `ack`, `list_agents`, `status`.
3. **Avisos del broker** (notificaciones JSON-RPC, sin `id`): `message.new`, `agent.changed`. Solo se envían a clientes registrados.
4. **Latido:** `ping` cada 15 s en los dos sentidos. Si no se recibe nada en 45 s, la conexión se da por muerta. El broker marca la sesión como desconectada y libera el nombre (ADR-0003), conservando el buzón.
5. **Reconexión:** el shim reintenta con espera creciente (0,5 s → 30 s como máximo) y repite `hello` + `register`. La **misma identidad técnica** (sessionId o thread_id) recupera su nombre en el acto, aunque el broker aún no haya detectado la conexión anterior como muerta. La conexión anterior se cierra.
6. **Límites:** 1 MiB por línea. Si se supera → error `too_large` sin cerrar la conexión. El contenido grande va por referencia a fichero.
7. **Errores:** códigos JSON-RPC estándar más códigos propios en el rango −32000…−32099 (`unauthorized`, `name_taken`, `unknown_agent`, `protocol_mismatch`, `too_large`).
8. **TCP de WSL sin TLS:** el tráfico no sale del PC (host ↔ VM de WSL). El cifrado entre máquinas se decide en el ADR de federación.
9. **Serialización:** `System.Text.Json` con generación de código (compatible con NativeAOT), con los tipos del protocolo en un proyecto compartido por broker, shim y CLI.

## Alternativas descartadas

- **gRPC:** esquema formal y streaming, pero pesado sobre named pipes, con generación de código y peor encaje con NativeAOT. Demasiado para el tamaño del bus.
- **Binario propio:** no se puede depurar a mano y no hace falta tanto rendimiento.

## Consecuencias

- El shim habla dos JSON-RPC: MCP con el agente por stdio y este protocolo con el broker. Comparten el mismo código de encuadre.
- Depurar es fácil: los mensajes se leen tal cual en el log y se pueden probar con un cliente de pipe o TCP mínimo.
- Para cambiar el protocolo de forma incompatible hay que subir `protocol` y mantener temporalmente las dos versiones en el broker.
