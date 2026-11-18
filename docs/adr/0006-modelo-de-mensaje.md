# ADR-0006: Modelo de mensaje

- Estado: **Aceptada**. El apartado "Conservación" está sustituido por [ADR-0011](0011-conservacion-de-la-auditoria.md); "El usuario como participante" está matizado por [ADR-0010](0010-la-cli-no-firma-desde-un-agente.md)
- Fecha: 2026-11-13
- Relacionadas: [ADR-0002](0002-entrega-y-despertar.md), [ADR-0003](0003-identidad-y-nombres.md), [ADR-0004](0004-broker-local.md), [ADR-0005](0005-protocolo-de-nodo.md)

## Contexto

Hoy las peticiones entre agentes (p. ej. lider → dev) y sus respuestas viajan por el portapapeles o por `herdr agent prompt`, sin estado ni rastro de qué responde a qué. Hace falta suficiente estructura para que "pídele una revisión a X y que vuelva la respuesta" funcione solo, sin convertirlo en un correo completo que gaste contexto en cada agente.

## Decisión

**Modelo ligero con hilos.**

### Mensaje

| Campo | Descripción |
|---|---|
| `id` | Identificador único (UUIDv7, ordenable por tiempo) |
| `thread_id` | Hilo. Un mensaje nuevo abre uno. Las respuestas heredan el suyo |
| `in_reply_to` | Mensaje al que responde (opcional) |
| `from` | Remitente `nombre@nodo`. **Lo pone el shim o la CLI**, nunca el agente (ADR-0003) |
| `to` | Uno o varios destinatarios `nombre[@nodo]` |
| `kind` | `note` (aviso, no espera respuesta), `request` (espera respuesta), `reply` |
| `subject` | Opcional, máximo 120 caracteres |
| `body` | Markdown, máximo **64 KiB** |
| `refs` | Referencias a ficheros: `{path, node, lines?}`. Máximo 20 |
| `created_at` | Marca de tiempo del broker |

### Estado por destinatario

`queued` → `notified` (aviso o canal entregado al transporte) → `read` (devuelto por `read_inbox` o entregado completo por el canal de Claude) → `answered` (el destinatario respondió en ese hilo). Las `request` sin respuesta aparecen en `status` y en `list_agents` ("lider espera a dev desde hace 12 min").

### Herramientas MCP del shim

- `send(to, body, kind="note", subject?, refs?)` → devuelve `message_id` y `thread_id`.
- `reply(message_id, body, refs?)`: responde al remitente en el mismo hilo.
- `read_inbox(unread_only=true, limit=20)`: devuelve los mensajes y los marca como leídos.
- `list_agents()`: según ADR-0003, más las peticiones pendientes.
- `register(name)`: según ADR-0003.
- **Sin esperas bloqueantes:** la respuesta despierta al que preguntó (canal en Claude, `codex queue` en Codex).

### Referencias a ficheros

- No se copia el contenido. Se envía la ruta, el nodo y, opcionalmente, un rango de líneas.
- **El broker traduce rutas entre Windows y WSL** según el nodo del destinatario: `C:\x\y` ↔ `/mnt/c/x/y`. Las rutas solo-Linux (`/home/...`) se entregan a un agente de Windows como `\\wsl.localhost\<distro>\home\...`.
- Las rutas de otros PC quedan para el ADR de federación.

### El usuario como participante

- `shepherd send <nombre> "<texto>"` desde la consola envía como `<usuario>@<nodo>` (nombre configurable; por defecto, el usuario del sistema), marcado como humano (`human: true` en el mensaje y en la `meta` del canal).
- Los agentes pueden escribir al usuario por ese nombre. Se lee con `shepherd inbox`, y `shepherd status` muestra cuántos hay pendientes.

### Conservación

- Los mensajes leídos o respondidos se borran a los **90 días** (configurable). Los no leídos no caducan.
- El log de auditoría (ADR-0004) se rota por mes y es independiente de esta limpieza.

## Alternativas descartadas

- **Mínimo (de, para, texto):** no hay forma de saber qué responde a qué ni si se ha leído.
- **Correo completo** (asuntos obligatorios, CC/CCO, prioridades, reservas de ficheros, como mcp_agent_mail): más herramientas y más contexto gastado en cada agente del que justifica el uso.

## Consecuencias

- El esquema SQLite tiene tablas de mensajes, destinatarios con estado e hilos.
- El texto que llega a un agente siempre lleva remitente y tipo, para que el receptor lo trate como dato de otro agente o del usuario.
- Traducir rutas exige que el broker conozca el nodo y la distro de cada agente (ADR-0003).
- No hay reacciones ni ediciones de mensajes: un mensaje enviado no se modifica.
